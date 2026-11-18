using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Api.Integrations;

internal sealed class AgentReasoningTool(TextReasoningService reasoning, bool research) : IAgentToolHandler
{
    public string CapabilityId => research ? AgentCapabilityIds.Research : AgentCapabilityIds.Reasoning;
    public async Task<IPreparedAgentTool> PrepareAsync(AgentToolContext context, CancellationToken ct)
    {
        var text = JsonSerializer.Deserialize<JsonElement>(context.Call.ArgumentsJson).GetProperty("request").GetString()!;
        var request = JsonSerializer.Deserialize<TextReasoningTask>(text) ?? throw new DomainException(400, "Invalid reasoning tool input.");
        Guard.Require(request.OperationId == context.Call.Id && request.ScopeId == context.Task.Id &&
            request.Profile == context.Specialist.ProviderProfile && request.Capability == (research ? TextCapability.GroundedPublicSearch : TextCapability.JsonReasoning) &&
            research == (context.Task.Role == AgentRole.Research) && request.DataClass == context.Call.DataClass && request.AllowCloudProcessing &&
            context.Run.AllowCloudProcessing && request.Lane == UsageLane.Background, 403, "Reasoning request exceeds the specialist policy.");
        return new Prepared(await reasoning.PrepareAsync(request, ct), context.Call.Id, context.Task.Id);
    }
    private sealed class Prepared(PreparedReasoningCall call, Guid invocation, Guid task) : IPreparedAgentTool
    {
        public AgentToolCharge? Charge => call.Charge;
        public Guid? CachedReservationId => call.CachedReservationId;
        public async Task<string> ExecuteAsync(CancellationToken ct) {
            Guard.Require(AgentToolExecutionScope.Contains(invocation, task), 403, "Agent handler dispatch requires gateway authorization.");
            return JsonSerializer.Serialize(await call.ExecuteAsync(ct));
        }
        public Task RefusedAsync(CancellationToken ct) => call.RefusedAsync(ct);
    }
}

internal sealed class AgentTemplateTool : IAgentToolHandler
{
    public string CapabilityId => AgentCapabilityIds.Template;
    public Task<IPreparedAgentTool> PrepareAsync(AgentToolContext context, CancellationToken ct)
    {
        var artifact = JsonSerializer.Deserialize<JsonElement>(context.Call.ArgumentsJson).GetProperty("artifact").GetString()!;
        Guard.Require(JsonSerializer.Deserialize<JsonElement>(artifact).ValueKind == JsonValueKind.Object, 400, "Agent artifact must be JSON.");
        return Task.FromResult<IPreparedAgentTool>(new Prepared(artifact, context.Call.Id, context.Task.Id));
    }
    private sealed class Prepared(string artifact, Guid invocation, Guid task) : IPreparedAgentTool
    {
        public AgentToolCharge? Charge => null;
        public Guid? CachedReservationId => null;
        public Task<string> ExecuteAsync(CancellationToken ct) {
            Guard.Require(AgentToolExecutionScope.Contains(invocation, task), 403, "Agent handler dispatch requires gateway authorization.");
            ct.ThrowIfCancellationRequested(); return Task.FromResult(artifact);
        }
    }
}
