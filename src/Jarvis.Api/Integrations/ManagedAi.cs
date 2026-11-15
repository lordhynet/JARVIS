using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Api.Integrations;

// Every new OpenAI dispatch and legacy text dispatch shares the existing durable ledger.
public sealed class ManagedAi(IHttpClientFactory clients, IConfiguration config, IRuntimeStore store, UsageBudget budget, TimeProvider clock)
{
    public BudgetLimits Limits => new(config.GetValue("PaidAI:DailyLimitUsd", 2m), config.GetValue("PaidAI:MonthlyLimitUsd", 50m),
        config.GetValue("PaidAI:BackgroundLimitUsd", 15m), config.GetValue("AI:VoiceLimitUsd", 30m), config.GetValue("AI:InteractiveLimitUsd", 5m));
    public string Model(string profile) => config["AI:" + profile + ":Model"] ?? throw new DomainException(503, "AI model profile is missing.");
    public decimal Rate(string profile, string direction) => config.GetValue<decimal>($"AI:{profile}:{direction}UsdPerMillion");
    public int OutputCap(string profile)
    {
        var cap = config.GetValue($"AI:{profile}:MaxOutputTokens", profile == "Chat" ? 1500 : 2048);
        Guard.Require(cap is > 0 and <= 32768, 503, "AI profile output cap must be 1–32768 tokens."); return cap;
    }
    public void RequireReady(string profile)
    {
        Guard.Require(!string.IsNullOrWhiteSpace(config["OpenAI:ApiKey"]), 503, "Configure OpenAI:ApiKey to enable AI and live voice.");
        Guard.Require(!string.IsNullOrWhiteSpace(Model(profile)) && (profile == "Voice" || Rate(profile, "Input") > 0 && Rate(profile, "Output") > 0), 503, "Configure the AI model and dated rates.");
        Guard.Require(config[$"AI:{profile}:RateCardModel"] == Model(profile), 503, "The selected AI model must match its reviewed rate card model.");
        Guard.Require(DateOnly.TryParse(config[$"AI:{profile}:RateCardDate"], out var date) && date <= DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) &&
            date >= DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-90), 503, "AI rate card requires review.");
        budget.RequireConfigured();
        if (profile == "Voice") RealtimePricing.Read(config);
    }
    public void RequireTranscriptionReady()
    {
        Guard.Require(!string.IsNullOrWhiteSpace(config["OpenAI:ApiKey"]), 503, "Configure OpenAI:ApiKey to enable transcription.");
        Guard.Require(config["AI:Transcription:RateCardModel"] == config["AI:Transcription:Model"], 503, "Transcription requires a reviewed model-specific rate card.");
        Guard.Require(DateOnly.TryParse(config["AI:Transcription:RateCardDate"], out var date) && date <= DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) &&
            date >= DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-90), 503, "AI rate card requires review.");
        budget.RequireConfigured();
    }
    public AiCapabilityStatus Status(string profile)
    {
        try { RequireReady(profile); return new(true, Model(profile), "Configured; explicit cloud-processing consent is required."); }
        catch (DomainException e) { return new(false, config[$"AI:{profile}:Model"] ?? "", e.Message); }
    }
    public static Guid Operation(string kind, Guid scope, Guid request) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{scope}:{request}"))[..16]);
    public async Task<JsonElement> TextAsync(Guid id, Guid scope, string profile, object payload, UsageLane lane, CancellationToken ct)
    {
        RequireReady(profile);
        var json = JsonSerializer.Serialize(payload); Guard.Require(Encoding.UTF8.GetByteCount(json) <= 180000, 400, "AI context is too large.");
        int outputCap;
        using (var parsed = JsonDocument.Parse(json))
        {
            Guard.Require(parsed.RootElement.GetProperty("model").GetString() == Model(profile), 503, "Configured model and its priced profile must match.");
            outputCap = parsed.RootElement.GetProperty("max_output_tokens").GetInt32();
            Guard.Require(outputCap > 0 && outputCap <= OutputCap(profile), 400, "AI output cap exceeds its reviewed profile.");
            if (parsed.RootElement.TryGetProperty("tools", out var tools))
                Guard.Require(profile == "Research" && tools.GetArrayLength() == 1 && tools[0].GetProperty("type").GetString() == "web_search" &&
                    parsed.RootElement.GetProperty("max_tool_calls").GetInt32() == 1, 400, "Only one explicitly budgeted public web search is permitted.");
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile + json)));
        var previous = await store.ReadAsync(s => s.ProviderResults.GetValueOrDefault(id), ct);
        if (previous is not null)
        {
            Guard.Require(previous.ScopeId == scope && previous.Fingerprint == fingerprint, 409, "AI request ID was reused for different input.");
            return JsonSerializer.Deserialize<JsonElement>(previous.Json);
        }
        var inputRate = Rate(profile, "Input");
        var outputRate = Rate(profile, "Output");
        var search = profile == "Research";
        var maximum = ((Encoding.UTF8.GetByteCount(json) + (search ? 24576m : 8192m)) * inputRate + outputCap * outputRate) / 1_000_000m + (search ? .01m : 0);
        await budget.ReserveAsync(id, scope, Model(profile), maximum, Limits, ct, lane, "openai");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new("Bearer", config["OpenAI:ApiKey"]);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await Jarvis.Api.Operations.CoreObservability.SendAsync(clients.CreateClient("providers"), request, "openai", ct);
        Guard.Require(response.IsSuccessStatusCode, response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? 429 : 502,
            "AI provider rejected the request. Its reservation is retained; automatic paid replay is blocked.");
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (result.TryGetProperty("usage", out var usage) && usage.TryGetProperty("input_tokens", out var input) && usage.TryGetProperty("output_tokens", out var output))
        {
            var settled = await budget.SettleAsync(id, (input.GetInt32() * inputRate + output.GetInt32() * outputRate) / 1_000_000m + (search ? .01m : 0),
                input.GetInt32(), output.GetInt32(), ct);
            Guard.Require(!settled.ReconciliationRequired, 502, "Provider usage exceeded the reserved bound; reconcile before further paid work.");
        }
        var raw = result.GetRawText(); Guard.Require(Encoding.UTF8.GetByteCount(raw) <= 500000, 502, "AI response exceeded the storage bound.");
        await store.UpdateAsync(s =>
        {
            Guard.Require(!s.ImportedDocuments.TryGetValue(scope, out var document) || document.DeletedAt is null, 409, "Imported source was deleted during AI processing.");
            Guard.Require(!s.RetiredSessions.Contains(scope), 409, "The source session was retired during AI processing.");
            Guard.Require(!s.TrainingAttempts.TryGetValue(scope, out var technical) || technical.DeletedAt is null, 409, "The source answer was deleted during AI processing.");
            Guard.Require(!s.CoachingAttempts.TryGetValue(scope, out var coaching) || coaching.DeletedAt is null, 409, "The source answer was deleted during AI processing.");
            if (s.AgentTasks.TryGetValue(scope, out var task))
                Guard.Require(task.Status == AgentTaskStatus.Running && s.AgentRuns[task.RunId].DeletedAt is null &&
                    (task.SourceRefs ?? []).All(source => AgentRuntime.SourceCurrent(s, source)), 409, "Agent work or its source was revoked during AI processing.");
            s.ProviderResults[id] = new(id, scope, fingerprint, raw, clock.GetUtcNow()); return true;
        }, ct);
        return result;
    }
    public static string Output(JsonElement result)
    {
        if (result.TryGetProperty("status", out var status)) Guard.Require(status.GetString() == "completed", 502, "AI response was incomplete.");
        var parts = new List<string>();
        if (result.TryGetProperty("output", out var output)) foreach (var item in output.EnumerateArray())
            if (item.TryGetProperty("content", out var content)) foreach (var p in content.EnumerateArray())
                if (p.TryGetProperty("type", out var t) && t.GetString() == "output_text") parts.Add(p.GetProperty("text").GetString()!);
        return Guard.Text(string.Join("\n", parts), "AI response", 20000);
    }
}
