using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Domains;

public sealed class MemoryDomainRegistryStore(IRuntimeStore runtime, IPrivacyReader privacy) : IDomainRegistryStore
{
    public async Task<long> EpochAsync(CancellationToken ct) {
        var status = await privacy.StatusAsync(ct); PrivacyContext.RequireWrite(status); return status.Epoch;
    }
    public async Task<T> ReadAsync<T>(Func<DomainRegistryState, long, T> read, CancellationToken ct) {
        var epoch = await EpochAsync(ct); using var scope = PrivacyContext.Enter(epoch); return await runtime.ReadAsync(s => read(s.DomainRegistry, epoch), ct);
    }
    public async Task<T> UpdateAsync<T>(Func<DomainRegistryState, long, T> update, CancellationToken ct) {
        var epoch = await EpochAsync(ct); using var scope = PrivacyContext.Enter(epoch); return await runtime.UpdateAsync(s => {
            var enabled = DomainExecutionLifecycle.Enabled(s.DomainRegistry); var result = update(s.DomainRegistry, epoch);
            enabled.ExceptWith(DomainExecutionLifecycle.Enabled(s.DomainRegistry)); DomainExecutionLifecycle.DisableRuns(s, enabled); return result;
        }, ct);
    }
    public async Task<IReadOnlyList<DomainResolved>> ReadActiveAsync(string? key, CancellationToken ct) => await ReadAsync((state, epoch) =>
        (IReadOnlyList<DomainResolved>)state.Definitions.Values.Where(d => d.Status == DomainStatus.Active && d.CurrentVersionId is not null && (key is null || d.Key == key) &&
            state.UserStates.GetValueOrDefault(DomainPolicy.OwnerSubject + ":" + d.Id)?.Enabled != false)
            .Select(d => {
                var approval = state.Approvals.Values.Where(a => a.DomainVersionId == d.CurrentVersionId && a.Decision == "Approved" && a.PolicyVersion == DomainPolicy.Version)
                    .OrderByDescending(a => a.At).FirstOrDefault();
                return new DomainResolved(d, state.Versions[d.CurrentVersionId!.Value], epoch, approval is not null, approval?.RiskClass);
            }).ToArray(), ct);
}
