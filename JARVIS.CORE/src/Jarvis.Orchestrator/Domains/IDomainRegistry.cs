using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Domains;

public sealed class DomainRegistryState
{
    public Dictionary<Guid, DomainDefinition> Definitions { get; set; } = [];
    public Dictionary<Guid, DomainVersion> Versions { get; set; } = [];
    public List<DomainCapabilityBinding> Bindings { get; set; } = [];
    public Dictionary<Guid, DomainApproval> Approvals { get; set; } = [];
    public Dictionary<string, UserDomainState> UserStates { get; set; } = [];
    public Dictionary<Guid, DomainAuditEvent> AuditEvents { get; set; } = [];
    public DomainRegistryExport Export() => new(Definitions.Values.ToArray(), Versions.Values.ToArray(), Bindings.ToArray(),
        Approvals.Values.ToArray(), UserStates.Values.ToArray(), AuditEvents.Values.ToArray());
}
public interface IDomainRegistryStore
{
    Task<long> EpochAsync(CancellationToken ct);
    Task<T> ReadAsync<T>(Func<DomainRegistryState, long, T> read, CancellationToken ct);
    Task<T> UpdateAsync<T>(Func<DomainRegistryState, long, T> update, CancellationToken ct);
    Task<IReadOnlyList<DomainResolved>> ReadActiveAsync(string? key, CancellationToken ct);
}
public interface IDomainRegistry
{
    Task<DomainProposal?> FindProposalAsync(Guid requestId, string fingerprint, CancellationToken ct);
    Task<DomainPreview> SaveDraftAsync(SaveDomainDraftRequest request, string actor, CancellationToken ct);
    Task<IReadOnlyList<DomainPreview>> ListAsync(CancellationToken ct);
    Task<DomainPreview> PreviewAsync(string key, Guid? versionId, CancellationToken ct);
    Task<DomainOperationReceipt> ActivateAsync(string key, ActivateDomainRequest request, BudgetActor actor, CancellationToken ct);
    Task<DomainOperationReceipt> DisableAsync(string key, DisableDomainRequest request, BudgetActor actor, CancellationToken ct);
    Task<DomainResolved?> ResolveActiveAsync(string key, CancellationToken ct);
    Task<IReadOnlyList<DomainResolved>> ActiveAsync(CancellationToken ct);
    Task<UserDomainState> GetStateAsync(string key, CancellationToken ct);
    Task<UserDomainState> SaveStateAsync(string key, SaveDomainStateRequest request, CancellationToken ct);
}
public static class DomainExecutionContext
{
    private static readonly AsyncLocal<string?> Current = new();
    public static string? Key => Current.Value;
    public static IDisposable Enter(string key) { var before = Current.Value; Current.Value = key; return new Scope(() => Current.Value = before); }
    private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }
    public static void Require(string key) => Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.ManualClient || Current.Value == key,
        403, "Domain state is limited to its trusted execution scope.");
}
