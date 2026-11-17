using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using System.Globalization;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Jarvis.Orchestrator.Intelligence;

namespace Jarvis.Api.Integrations;

// Every new OpenAI dispatch and legacy text dispatch shares the existing durable ledger.
public sealed class ManagedAi(IHttpClientFactory clients, IConfiguration config, IRuntimeStore store, UsageBudget budget, TimeProvider clock, RateCard? cards = null)
{
    private readonly RateCard rateCards = cards ?? new RateCard(config, clock);
    public (string Model, RealtimePricing Pricing) RealtimeRates() { var rates = rateCards.ReadRealtime(); budget.RequireConfigured(); return rates; }
    internal ReviewedVoiceRates ReviewedRealtime(VoiceMode mode) => rateCards.ReadRealtime(mode);
    internal VoiceModeOptions VoiceOptions(ActivityType activity, TrainingMode? drill)
    {
        Guard.Require(Enum.IsDefined(activity) && (drill is null || Enum.IsDefined(drill.Value)), 400, "Unknown voice activity or drill.");
        return new(VoiceModePolicy.Recommend(activity, drill),
            [VoiceOption(VoiceMode.Everyday), VoiceOption(VoiceMode.Serious)]);
    }
    internal VoiceModeEstimate VoiceOption(VoiceMode mode, VoiceWindowLimits? window = null)
    {
        try
        {
            var rates = ReviewedRealtime(mode);
            var cap = mode == VoiceMode.Serious ? SeriousDailyCap : null;
            Guard.Require(mode != VoiceMode.Serious || cap is > 0 and <= 200, 503, "Configure a positive Serious voice daily GBP cap.");
            var (usd, windows) = RealtimeWindowCost.EstimateTenMinutes(window ?? RealtimeWindow(), rates.Pricing, RealtimePersona());
            return new(mode, true, rates.Route.Model, budget.EstimateGbp(usd) * windows, "Reviewed maximum reservation for ten minutes.", cap);
        }
        catch (DomainException error) { return new(mode, false, null, null, error.Message); }
        catch (InvalidOperationException) { return new(mode, false, null, null, "The voice mode configuration is malformed."); }
    }
    internal string RealtimePersona() => RealtimeVoicePrompt.ReadPersona(config);
    internal RealtimeVoiceOutput RealtimeOutput() => RealtimeVoiceOutput.Read(config);
    internal VoiceWindowLimits RealtimeWindow()
    {
        var configured = config[ConfigKeys.VoiceWindowSeconds];
        if (string.IsNullOrWhiteSpace(configured)) return new(VoiceWindowLimits.DefaultSeconds);
        Guard.Require(int.TryParse(configured, out var seconds), 503, "The realtime voice window must be a whole number of seconds.");
        return new(seconds);
    }
    internal RealtimeTurnDetection VoiceTurnDetection() => RealtimeTurnDetection.Read(config);
    public (string Model, decimal PerMinute, string ApiKey) TranscriptionRates() { var rates = rateCards.ReadTranscription(); budget.RequireConfigured(); return rates; }
    public BudgetLimits Limits => new(config.GetValue(ConfigKeys.DailyLimitUsd, 2m), config.GetValue(ConfigKeys.MonthlyLimitUsd, 50m),
        config.GetValue(ConfigKeys.BackgroundLimitUsd, 15m), config.GetValue(ConfigKeys.VoiceLimitUsd, 30m),
        config.GetValue(ConfigKeys.InteractiveLimitUsd, 5m), SeriousDailyCap);
    private decimal? SeriousDailyCap => decimal.TryParse(config[ConfigKeys.VoiceSeriousDailyGbp], NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture, out var cap) ? cap : null;
    public string Model(string profile) => config[ConfigKeys.AiProfilePrefix(profile) + ConfigKeys.Model] ?? throw new DomainException(503, "AI model profile is missing.");
    public decimal Rate(string profile, string direction) => config.GetValue<decimal>(ConfigKeys.AiProfilePrefix(profile) + direction + "UsdPerMillion");
    public int OutputCap(string profile)
    {
        var cap = config.GetValue(ConfigKeys.AiProfilePrefix(profile) + ConfigKeys.MaxOutputTokens, profile == "Chat" ? 1500 : 2048);
        Guard.Require(cap is > 0 and <= 32768, 503, "AI profile output cap must be 1–32768 tokens."); return cap;
    }
    public void RequireReady(string profile)
    {
        if (profile == "Voice") RequireVoiceReady(VoiceMode.Everyday);
        else { rateCards.ReadText(profile, profile == "Research" ? TextCapability.GroundedPublicSearch : TextCapability.JsonReasoning,
            profile == "Research" ? ReasoningDataClass.Public : ReasoningDataClass.Personal); budget.RequireConfigured(); }
    }
    internal ReviewedVoiceRates RequireVoiceReady(VoiceMode mode)
    {
        RealtimePersona();
        RealtimeOutput();
        RealtimeWindow();
        VoiceTurnDetection();
        var reviewed = ReviewedRealtime(mode);
        budget.RequireConfigured();
        return reviewed;
    }
    public void RequireTranscriptionReady() => TranscriptionRates();
    public AiCapabilityStatus Status(string profile)
    {
        try
        {
            RequireReady(profile);
            return new(true, profile == "Voice" ? RealtimeRates().Model : Model(profile), "Configured; explicit cloud-processing consent is required.");
        }
        catch (DomainException e) { return new(false, config[ConfigKeys.AiProfilePrefix(profile) + ConfigKeys.Model] ?? "", e.Message); }
    }
    public static Guid Operation(string kind, Guid scope, Guid request) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{scope}:{request}"))[..16]);
    public async Task<JsonElement> TextAsync(Guid id, Guid scope, string profile, object payload, UsageLane lane, CancellationToken ct, ReviewedTextRates? reviewed = null, bool preReserved = false)
    {
        reviewed ??= rateCards.ReadText(profile, profile == "Research" ? TextCapability.GroundedPublicSearch : TextCapability.JsonReasoning,
            profile == "Research" ? ReasoningDataClass.Public : ReasoningDataClass.Personal);
        Guard.Require(reviewed.Route.Provider == ProviderIds.OpenAi && reviewed.Route.Profile == profile, 503, "OpenAI adapter route mismatch.");
        budget.RequireConfigured();
        var json = JsonSerializer.Serialize(payload); Guard.Require(Encoding.UTF8.GetByteCount(json) <= 180000, 400, "AI context is too large.");
        int outputCap;
        using (var parsed = JsonDocument.Parse(json))
        {
            Guard.Require(parsed.RootElement.GetProperty("model").GetString() == reviewed.Route.Model, 503, "Configured model and its priced profile must match.");
            outputCap = parsed.RootElement.GetProperty("max_output_tokens").GetInt32();
            Guard.Require(outputCap > 0 && outputCap <= reviewed.Route.OutputLimit, 400, "AI output cap exceeds its reviewed profile.");
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
        var inputRate = reviewed.Route.InputUsdPerMillion;
        var outputRate = reviewed.Route.OutputUsdPerMillion;
        var search = profile == "Research";
        var maximum = ((Encoding.UTF8.GetByteCount(json) + (search ? 24576m : 8192m)) * inputRate + outputCap * outputRate) / 1_000_000m + (search ? reviewed.SearchUsdPerCall : 0);
        if (preReserved) await RequireReservedAsync(store, id, scope, reviewed, maximum, ct);
        else await budget.ReserveAsync(id, scope, reviewed.Route.Model, maximum, reviewed.Limits, ct, lane, ProviderIds.OpenAi);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new("Bearer", reviewed.ApiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await Jarvis.Api.Operations.CoreObservability.SendAsync(clients.CreateClient("providers"), request, ProviderIds.OpenAi, ct);
        Guard.Require(response.IsSuccessStatusCode, response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? 429 : 502,
            "AI provider rejected the request. Its reservation is retained; automatic paid replay is blocked.");
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (result.TryGetProperty("usage", out var usage) && usage.TryGetProperty("input_tokens", out var input) && usage.TryGetProperty("output_tokens", out var output))
        {
            var settled = await budget.SettleAsync(id, reviewed.Cost(input.GetInt32(), output.GetInt32()) + (search ? reviewed.SearchUsdPerCall : 0),
                input.GetInt32(), output.GetInt32(), ct);
            Guard.Require(!settled.ReconciliationRequired, 502, "Provider usage exceeded the reserved bound; reconcile before further paid work.");
        }
        var raw = result.GetRawText(); Guard.Require(Encoding.UTF8.GetByteCount(raw) <= 500000, 502, "AI response exceeded the storage bound.");
        await store.UpdateAsync(s =>
        {
            RequireSourceCurrent(s, scope);
            s.ProviderResults[id] = new(id, scope, fingerprint, raw, clock.GetUtcNow()); return true;
        }, ct);
        return result;
    }
    internal static void RequireSourceCurrent(RuntimeState s, Guid scope)
    {
        Guard.Require(!s.RetiredReasoningScopes.Contains(scope), 409, "The reasoning source was deleted.");
            Guard.Require(!s.ImportedDocuments.TryGetValue(scope, out var document) || document.DeletedAt is null, 409, "Imported source was deleted during AI processing.");
            Guard.Require(!s.RetiredSessions.Contains(scope), 409, "The source session was retired during AI processing.");
            Guard.Require(!s.TrainingAttempts.TryGetValue(scope, out var technical) || technical.DeletedAt is null, 409, "The source answer was deleted during AI processing.");
            Guard.Require(!s.CoachingAttempts.TryGetValue(scope, out var coaching) || coaching.DeletedAt is null, 409, "The source answer was deleted during AI processing.");
            if (s.AgentTasks.TryGetValue(scope, out var task))
                Guard.Require(task.Status == AgentTaskStatus.Running && s.AgentRuns[task.RunId].DeletedAt is null &&
                    (task.SourceRefs ?? []).All(source => AgentRuntime.SourceCurrent(s, source)), 409, "Agent work or its source was revoked during AI processing.");

    }
    internal static Task<UsageReservation> RequireReservedAsync(IRuntimeReader store, Guid id, Guid scope, ReviewedTextRates rates, decimal minimum, CancellationToken ct) =>
        store.ReadAsync(s => {
            var charge = s.UsageReservations.GetValueOrDefault(id);
            Guard.Require(charge is not null && charge.RunId == scope && charge.Provider == rates.Route.Provider && charge.Model == rates.Route.Model &&
                charge.ActualUsd is null && !charge.ReconciliationRequired && charge.ReservedUsd >= minimum, 409, "Provider dispatch requires its matching durable reservation.");
            RequireSourceCurrent(s, scope); return charge;
        }, ct);
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
