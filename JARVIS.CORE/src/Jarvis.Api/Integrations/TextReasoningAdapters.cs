using System.Net.Http.Json;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Api.Integrations;

public sealed record AdapterTextResult(string Text, IReadOnlyList<GroundingCitation> Citations, ModelRoute? ServedRoute = null);
public interface ITextReasoningAdapter
{
    string Provider { get; }
    IReadOnlySet<TextCapability> Capabilities { get; }
    Task<AdapterTextResult> ExecuteAsync(TextReasoningTask task, ReviewedTextRates rates, CancellationToken ct);
}

public sealed class OpenAiResponsesAdapter(ManagedAi ai) : ITextReasoningAdapter
{
    public string Provider => ProviderIds.OpenAi;
    public IReadOnlySet<TextCapability> Capabilities { get; } = Enum.GetValues<TextCapability>().ToFrozenSet();
    public async Task<AdapterTextResult> ExecuteAsync(TextReasoningTask task, ReviewedTextRates rates, CancellationToken ct)
    {
        object input = task.Messages.Count == 1 && task.Messages[0].Role == ChatRoles.User ? task.Messages[0].Text :
            task.Messages.Select(m => new { role = m.Role, content = m.Text }).ToArray();
        var model = rates.Route.Model; var max_output_tokens = rates.Route.OutputLimit; var instructions = task.Instructions;
        object payload = task.Capability switch {
            TextCapability.GroundedPublicSearch => new { model, store = false, max_output_tokens, instructions, input,
                tools = new[] { new { type = "web_search", search_context_size = "low" } }, max_tool_calls = 1, include = new[] { "web_search_call.action.sources" } },
            TextCapability.JsonReasoning => new { model, store = false, max_output_tokens, instructions, input, text = new { format = new { type = "json_object" } } },
            _ => (object)new { model, store = false, max_output_tokens, instructions, input }
        };
        var result = await ai.TextAsync(task.OperationId, task.ScopeId, task.Profile, payload, task.Lane, ct, rates, preReserved: true);
        var text = ManagedAi.Output(result); var citations = new List<GroundingCitation>();
        if (task.Capability == TextCapability.GroundedPublicSearch) {
            foreach (var item in result.GetProperty("output").EnumerateArray()) if (item.TryGetProperty("content", out var contents))
                foreach (var content in contents.EnumerateArray()) if (content.TryGetProperty("annotations", out var annotations))
                    foreach (var citation in annotations.EnumerateArray()) if (citation.TryGetProperty("type", out var type) && type.GetString() == "url_citation") {
                        var url = citation.GetProperty("url").GetString();
                        Guard.Require(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 &&
                            !uri.IsLoopback && Uri.CheckHostName(uri.Host) == UriHostNameType.Dns && !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase), 502, "Research citation URL is invalid.");
                        citations.Add(new(url!, citation.GetProperty("title").GetString() ?? "", citation.GetProperty("start_index").GetInt32(), citation.GetProperty("end_index").GetInt32()));
                    }
            Guard.Require(citations.Count is > 0 and <= 30, 502, "Research output had no usable source evidence.");
        }
        return new(text, citations);
    }
}

public sealed class AnthropicMessagesAdapter(IHttpClientFactory clients, IRuntimeStore store, UsageBudget budget, TimeProvider clock, IWorkflowReader workflows) : ITextReasoningAdapter
{
    public const string ApiVersion = "2026-10-21";
    public const string FallbackBeta = "server-side-fallback-2026-07-01";
    public string Provider => ProviderIds.Anthropic;
    public IReadOnlySet<TextCapability> Capabilities { get; } = new[] { TextCapability.Conversation, TextCapability.JsonReasoning }.ToFrozenSet();
    private async Task RequireLegacyLive(TextReasoningTask task, CancellationToken ct)
    {
        if (task.Profile != "LegacyInterview") return;
        var run = await workflows.GetAsync(task.ScopeId, ct);
        Guard.Require(run?.Status == WorkflowStatus.Running && run.AllowCloudProcessing && run.ClaimToken == task.OperationId &&
            run.LeaseExpiresAt > clock.GetUtcNow() && run.ExpiresAt > clock.GetUtcNow(), 409, "Legacy workflow claim was revoked or expired.");
    }
    public async Task<AdapterTextResult> ExecuteAsync(TextReasoningTask task, ReviewedTextRates rates, CancellationToken ct)
    {
        Guard.Require(task.Capability != TextCapability.GroundedPublicSearch && rates.Route.Provider == Provider, 403, "Selected adapter cannot supply that capability.");
        var payload = new Dictionary<string, object> {
            ["model"] = rates.Route.Model, ["max_tokens"] = rates.Route.OutputLimit,
            ["system"] = rates.PromptCachingEnabled ? new object[] { new { type = "text", text = task.Instructions,
                cache_control = new { type = "ephemeral", ttl = "5m" } } } : task.Instructions,
            ["messages"] = task.Messages.Select(m => new { role = m.Role, content = m.Text }).ToArray()
        };
        if (rates.Fallbacks.Count > 0) payload["fallbacks"] = rates.Fallbacks.Select(r => new { model = r.Model, max_tokens = r.OutputLimit }).ToArray();
        var json = JsonSerializer.Serialize(payload); var fingerprint = PrivacyData.Hash(Provider + task.Profile + json);
        var previous = await store.ReadAsync(s => s.ProviderResults.GetValueOrDefault(task.OperationId), ct);
        JsonElement result;
        if (previous is not null) {
            Guard.Require(previous.ScopeId == task.ScopeId && previous.Fingerprint == fingerprint, 409, "Provider request ID was reused for different input.");
            result = JsonSerializer.Deserialize<JsonElement>(previous.Json);
        } else {
            var bound = rates.MaximumCost(Encoding.UTF8.GetByteCount(json) + 8192);
            var reservation = await ManagedAi.RequireReservedAsync(store, task.OperationId, task.ScopeId, rates, bound, ct);
            await RequireLegacyLive(task, ct);
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            request.Headers.Add("x-api-key", rates.ApiKey); request.Headers.Add("anthropic-version", ApiVersion);
            if (rates.Fallbacks.Count > 0) request.Headers.Add("anthropic-beta", FallbackBeta);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await Jarvis.Api.Operations.CoreObservability.SendAsync(clients.CreateClient("providers"), request, Provider, ct);
            ProviderRetryAfter.RequireSuccess(response, clock.GetUtcNow());
            result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            IReadOnlyList<ProviderModelUsage> usages;
            ModelRoute served;
            int input, output;
            try {
                served = ServedRoute(result, rates);
                usages = ReadUsage(result, rates, served);
                input = checked(usages.Sum(u => checked(u.InputTokens + u.CacheWriteTokens + u.CacheReadTokens)));
                output = usages.Sum(u => u.OutputTokens);
            } catch (Exception error) when (error is DomainException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) {
                await store.UpdateAsync(s => { var charge = s.UsageReservations[reservation.Id]; s.UsageReservations[reservation.Id] = charge with { ReconciliationRequired = true }; return true; }, ct);
                throw new DomainException(502, "Provider returned unreviewed model or usage data; billing reconciliation is required.");
            }
            await store.UpdateAsync(s => {
                if (s.ProviderAttempts.TryGetValue(task.OperationId, out var attempt))
                    s.ProviderAttempts[task.OperationId] = attempt with { ServedRoute = served, ModelUsage = usages };
                return true;
            }, ct);
            var settled = await budget.SettleAsync(reservation.Id, usages.Sum(u => u.CostUsd), input, output, ct);
            Guard.Require(!settled.ReconciliationRequired, 502, "Provider exceeded its reservation; reconcile before further paid work.");
            var raw = result.GetRawText(); Guard.Require(Encoding.UTF8.GetByteCount(raw) <= 500000, 502, "Provider response exceeded its storage bound.");
            await RequireLegacyLive(task, ct);
            await store.UpdateAsync(s => {
                ManagedAi.RequireSourceCurrent(s, task.ScopeId);
                s.ProviderResults[task.OperationId] = new(task.OperationId, task.ScopeId, fingerprint, raw, clock.GetUtcNow()); return true;
            }, ct);
        }
        var stop = result.GetProperty("stop_reason").GetString();
        Guard.Require(stop != "max_tokens", 502, "Provider exhausted the output cap, including thinking tokens. The response is incomplete.");
        Guard.Require(stop != "refusal", 502, "Provider refused the request. The response is incomplete.");
        Guard.Require(stop == "end_turn", 502, "Provider response was incomplete.");
        var text = string.Concat(result.GetProperty("content").EnumerateArray().Where(x => x.GetProperty("type").GetString() == "text").Select(x => x.GetProperty("text").GetString()));
        return new(Guard.Text(text, "Provider text", 20000), [], ServedRoute(result, rates));
    }

    private static ModelRoute ReviewedRoute(string? model, ReviewedTextRates rates) =>
        new[] { rates.Route }.Concat(rates.Fallbacks).FirstOrDefault(r => r.Model == model)
            ?? throw new DomainException(502, "The served model has no reviewed rate card.");

    private static ModelRoute ServedRoute(JsonElement result, ReviewedTextRates rates) =>
        ReviewedRoute(result.TryGetProperty("model", out var model) ? model.GetString() :
            rates.Fallbacks.Count == 0 ? rates.Route.Model : null, rates);

    private static IReadOnlyList<ProviderModelUsage> ReadUsage(JsonElement result, ReviewedTextRates rates, ModelRoute served)
    {
        var usage = result.GetProperty("usage");
        ValidateCacheDuration(usage);
        if (!usage.TryGetProperty("iterations", out var iterations) || iterations.ValueKind == JsonValueKind.Null) {
            Guard.Require(served.Model == rates.Route.Model, 502, "Fallback usage is missing its per-model billing record.");
            return [PriceUsage(usage, served, RefusalIsFree(result, usage))];
        }
        Guard.Require(iterations.ValueKind == JsonValueKind.Array && iterations.GetArrayLength() is > 0 and <= 4 &&
            iterations.GetArrayLength() <= rates.Fallbacks.Count + 1, 502, "Invalid per-model usage record.");
        var records = new List<ProviderModelUsage>();
        var lastIndex = -1;
        var routes = new[] { rates.Route }.Concat(rates.Fallbacks).ToArray();
        var items = iterations.EnumerateArray().ToArray();
        for (var index = 0; index < items.Length; index++) {
            var item = items[index]; var route = ReviewedRoute(item.GetProperty("model").GetString(), rates);
            var routeIndex = Array.FindIndex(routes, r => r.Model == route.Model);
            Guard.Require(routeIndex > lastIndex && (item.GetProperty("type").GetString() == (routeIndex == 0 ? "message" : "fallback_message") ||
                index < items.Length - 1 && item.GetProperty("type").GetString() == "message"),
                502, "Unexpected fallback billing order.");
            lastIndex = routeIndex;
            bool free;
            if (index == items.Length - 1) {
                Guard.Require(route.Model == served.Model && Token(item, "input_tokens") == Token(usage, "input_tokens") &&
                    Token(item, "output_tokens") == Token(usage, "output_tokens") && Token(item, "cache_creation_input_tokens", true) == Token(usage, "cache_creation_input_tokens", true) &&
                    Token(item, "cache_read_input_tokens", true) == Token(usage, "cache_read_input_tokens", true), 502, "Served-model usage does not match the final attempt.");
                free = RefusalIsFree(result, item);
            } else {
                var boundary = result.GetProperty("content").EnumerateArray().FirstOrDefault(block =>
                    block.GetProperty("type").GetString() == "fallback" && block.GetProperty("from").GetProperty("model").GetString() == route.Model);
                Guard.Require(boundary.ValueKind != JsonValueKind.Undefined, 502, "Fallback refusal has no billing category.");
                free = RefusalIsFree(boundary.GetProperty("trigger"), item, isTrigger: true);
            }
            records.Add(PriceUsage(item, route, free));
        }
        return records;
    }

    private static int Token(JsonElement usage, string name, bool optional = false)
    {
        if (optional && !usage.TryGetProperty(name, out _)) return 0;
        Guard.Require(usage.GetProperty(name).TryGetInt32(out var count) && count >= 0, 502, "Invalid provider token usage.");
        return count;
    }

    private static bool RefusalIsFree(JsonElement message, JsonElement usage, bool isTrigger = false)
    {
        // Reviewed 2026-11-17. Anthropic may change the billed category set; recheck
        // it alongside the rate card. Unknown categories retain the reservation.
        // https://platform.claude.com/docs/en/build-with-claude/refusals-and-fallback#how-refusals-are-billed
        if (!isTrigger && message.GetProperty("stop_reason").GetString() != "refusal") return false;
        if (Token(usage, "output_tokens") > 0) return false;
        var details = isTrigger ? message : message.TryGetProperty("stop_details", out var stop) ? stop : default;
        var category = details.ValueKind == JsonValueKind.Object && details.TryGetProperty("category", out var value) ? value.GetString() : null;
        Guard.Require(category is null or "cyber" or "general_harms" or "bio" or "frontier_llm" or "reasoning_extraction",
            502, "Unknown refusal billing category.");
        return category is null or "cyber" or "general_harms";
    }

    private static ProviderModelUsage PriceUsage(JsonElement usage, ModelRoute route, bool free)
    {
        var input = Token(usage, "input_tokens"); var output = Token(usage, "output_tokens");
        var write = Token(usage, "cache_creation_input_tokens", true); var read = Token(usage, "cache_read_input_tokens", true);
        Guard.Require((write == 0 || route.CacheWriteUsdPerMillion > 0) && (read == 0 || route.CacheReadUsdPerMillion > 0),
            502, "Cache usage has no reviewed price.");
        ValidateCacheDuration(usage);
        var cost = free ? 0 : (input * route.InputUsdPerMillion + output * route.OutputUsdPerMillion +
            write * (route.CacheWriteUsdPerMillion ?? 0) + read * (route.CacheReadUsdPerMillion ?? 0)) / 1_000_000m;
        return new(route, input, output, write, read, cost);
    }

    private static void ValidateCacheDuration(JsonElement usage)
    {
        if (usage.TryGetProperty("cache_creation", out var creation) && creation.ValueKind != JsonValueKind.Null)
            Guard.Require(Token(creation, "ephemeral_1h_input_tokens", true) == 0 &&
                Token(creation, "ephemeral_5m_input_tokens", true) == Token(usage, "cache_creation_input_tokens", true),
                502, "Unexpected cache duration requires reconciliation.");
    }
}
