using System.Net.Http.Json;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Api.Integrations;

public sealed record AdapterTextResult(string Text, IReadOnlyList<GroundingCitation> Citations);
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
        var payload = new { model = rates.Route.Model, max_tokens = rates.Route.OutputLimit, system = task.Instructions,
            messages = task.Messages.Select(m => new { role = m.Role, content = m.Text }).ToArray() };
        var json = JsonSerializer.Serialize(payload); var fingerprint = PrivacyData.Hash(Provider + task.Profile + json);
        var previous = await store.ReadAsync(s => s.ProviderResults.GetValueOrDefault(task.OperationId), ct);
        JsonElement result;
        if (previous is not null) {
            Guard.Require(previous.ScopeId == task.ScopeId && previous.Fingerprint == fingerprint, 409, "Provider request ID was reused for different input.");
            result = JsonSerializer.Deserialize<JsonElement>(previous.Json);
        } else {
            var bound = rates.Cost(Encoding.UTF8.GetByteCount(json) + 8192, rates.Route.OutputLimit);
            var reservation = await ManagedAi.RequireReservedAsync(store, task.OperationId, task.ScopeId, rates, bound, ct);
            await RequireLegacyLive(task, ct);
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            request.Headers.Add("x-api-key", rates.ApiKey); request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await Jarvis.Api.Operations.CoreObservability.SendAsync(clients.CreateClient("providers"), request, Provider, ct);
            Guard.Require(response.IsSuccessStatusCode, response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? 429 : 502, "Provider rejected the request; its reservation is retained.");
            result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var usage = result.GetProperty("usage");
            var unpricedCache = new[] { "cache_creation_input_tokens", "cache_read_input_tokens" }.Any(name => usage.TryGetProperty(name, out var value) && value.GetInt32() != 0);
            if (unpricedCache) {
                await store.UpdateAsync(s => { var charge = s.UsageReservations[reservation.Id]; s.UsageReservations[reservation.Id] = charge with { ReconciliationRequired = true }; return true; }, ct);
                throw new DomainException(502, "Unexpected cache usage requires billing reconciliation.");
            }
            var input = usage.GetProperty("input_tokens").GetInt32(); var output = usage.GetProperty("output_tokens").GetInt32();
            var settled = await budget.SettleAsync(reservation.Id, rates.Cost(input, output), input, output, ct);
            Guard.Require(!settled.ReconciliationRequired, 502, "Provider exceeded its reservation; reconcile before further paid work.");
            var raw = result.GetRawText(); Guard.Require(Encoding.UTF8.GetByteCount(raw) <= 500000, 502, "Provider response exceeded its storage bound.");
            await RequireLegacyLive(task, ct);
            await store.UpdateAsync(s => {
                ManagedAi.RequireSourceCurrent(s, task.ScopeId);
                s.ProviderResults[task.OperationId] = new(task.OperationId, task.ScopeId, fingerprint, raw, clock.GetUtcNow()); return true;
            }, ct);
        }
        Guard.Require(result.GetProperty("stop_reason").GetString() == "end_turn", 502, "Provider response was incomplete.");
        var text = string.Concat(result.GetProperty("content").EnumerateArray().Where(x => x.GetProperty("type").GetString() == "text").Select(x => x.GetProperty("text").GetString()));
        return new(Guard.Text(text, "Provider text", 20000), []);
    }
}
