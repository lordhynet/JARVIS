using Jarvis.Contracts;

namespace Jarvis.Api.Integrations;

// Compatibility bridge for the existing WorkflowService, not an AgentTask grant.
// Only the fenced legacy interview processor can create this host-owned scope.
internal static class LegacyReasoningAdmission
{
    private sealed record Admission(Guid ScopeId, Guid OperationId, Func<CancellationToken, Task<bool>> Current);
    private static readonly AsyncLocal<Admission?> Active = new();
    internal static IDisposable Enter(Guid scope, Guid operation, Func<CancellationToken, Task<bool>> current)
    {
        var previous = Active.Value; Active.Value = new(scope, operation, current); return new Restore(() => Active.Value = previous);
    }
    internal static async Task<bool> PermitsAsync(TextReasoningTask task, CancellationToken ct) =>
        Active.Value is { } admission && task.ScopeId == admission.ScopeId && task.OperationId == admission.OperationId &&
        task.Profile == "LegacyInterview" && task.Capability == TextCapability.JsonReasoning && await admission.Current(ct);
    private sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }
}
