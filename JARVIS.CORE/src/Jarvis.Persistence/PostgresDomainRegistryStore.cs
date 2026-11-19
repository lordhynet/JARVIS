using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

public sealed class PostgresDomainRegistryStore(IDbContextFactory<SessionDbContext> factory, TimeProvider? clock = null) : IDomainRegistryStore
{
    public async Task<long> EpochAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var status = await PostgresPrivacyGate.LockAsync(db, ct); await tx.CommitAsync(ct); return status.Epoch;
    }
    internal static async Task<DomainRegistryState> Snapshot(SessionDbContext db, CancellationToken ct)
    {
        var state = new DomainRegistryState {
            Definitions = (await db.DomainDefinitions.ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Contract()),
            Versions = (await db.DomainVersions.ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Contract()),
            Bindings = (await db.DomainCapabilityBindings.ToListAsync(ct)).Select(x => x.Contract()).ToList(),
            Approvals = (await db.DomainApprovals.ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Contract()),
            UserStates = (await db.UserDomainState.ToListAsync(ct)).ToDictionary(x => x.SubjectId + ":" + x.DomainId, x => x.Contract()),
            AuditEvents = (await db.DomainAuditEvents.ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Contract())
        };
        return state;
    }
    public async Task<T> ReadAsync<T>(Func<DomainRegistryState, long, T> read, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var status = await PostgresPrivacyGate.LockAsync(db, ct); var result = read(await Snapshot(db, ct), status.Epoch);
        await tx.CommitAsync(ct); return result;
    }
    public async Task<T> UpdateAsync<T>(Func<DomainRegistryState, long, T> update, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var status = await PostgresPrivacyGate.LockAsync(db, ct);
        // Administrative catalogue mutations serialize independently of the runtime JSONB hot row.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(746271591)", ct);
        var state = await Snapshot(db, ct); var enabled = DomainExecutionLifecycle.Enabled(state); var result = update(state, status.Epoch);
        enabled.ExceptWith(DomainExecutionLifecycle.Enabled(state));
        if (enabled.Count > 0) {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await PostgresRuntimeStore.UpdateInTransactionAsync(db, s => { DomainExecutionLifecycle.DisableRuns(s, enabled, (clock ?? TimeProvider.System).GetUtcNow()); return Task.FromResult(true); }, started, started, ct);
        }
        var existingDefinitions = db.DomainDefinitions.Local.ToDictionary(x => x.Id);
        foreach (var definition in state.Definitions.Values.Where(d => !existingDefinitions.ContainsKey(d.Id))) {
            var row = DomainDefinitionRow.From(definition); row.CurrentVersionId = null; db.DomainDefinitions.Add(row);
        }
        // Break the optional activation-pointer cycle inside this transaction before inserting versions.
        await db.SaveChangesAsync(ct);
        var knownVersions = db.DomainVersions.Local.ToDictionary(x => x.Id);
        foreach (var version in state.Versions.Values) {
            if (knownVersions.TryGetValue(version.Id, out var existing))
                Guard.Require(existing.ManifestHash == version.ManifestHash && existing.DomainId == version.DomainId && existing.Version == version.Version, 409, "Domain versions are immutable.");
            else db.DomainVersions.Add(DomainVersionRow.From(version));
        }
        var bindings = db.DomainCapabilityBindings.Local.Select(x => (x.DomainVersionId, x.CapabilityId)).ToHashSet();
        foreach (var binding in state.Bindings.Where(x => !bindings.Contains((x.DomainVersionId, x.CapabilityId)))) db.DomainCapabilityBindings.Add(DomainCapabilityBindingRow.From(binding));
        foreach (var definition in state.Definitions.Values) {
            var row = db.DomainDefinitions.Local.Single(x => x.Id == definition.Id);
            if (row.Contract() != definition) db.Entry(row).CurrentValues.SetValues(DomainDefinitionRow.From(definition));
        }
        var approvals = db.DomainApprovals.Local.Select(x => x.Id).ToHashSet();
        foreach (var approval in state.Approvals.Values.Where(x => !approvals.Contains(x.Id))) db.DomainApprovals.Add(DomainApprovalRow.From(approval));
        foreach (var userState in state.UserStates.Values) {
            var row = db.UserDomainState.Local.SingleOrDefault(x => x.SubjectId == userState.SubjectId && x.DomainId == userState.DomainId);
            if (row is null) db.UserDomainState.Add(UserDomainStateRow.From(userState));
            else if (row.Contract() != userState) db.Entry(row).CurrentValues.SetValues(UserDomainStateRow.From(userState));
        }
        var audits = db.DomainAuditEvents.Local.Select(x => x.Id).ToHashSet();
        foreach (var audit in state.AuditEvents.Values.Where(x => !audits.Contains(x.Id))) db.DomainAuditEvents.Add(DomainAuditEventRow.From(audit));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return result;
    }
    public async Task<IReadOnlyList<DomainResolved>> ReadActiveAsync(string? key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var status = await PostgresPrivacyGate.LockAsync(db, ct);
        var rows = await (from definition in db.DomainDefinitions.AsNoTracking()
            join version in db.DomainVersions.AsNoTracking() on definition.CurrentVersionId equals version.Id
            where definition.Status == DomainStatus.Active && (key == null || definition.Key == key) && version.DomainId == definition.Id &&
                !db.UserDomainState.Any(u => u.DomainId == definition.Id && u.SubjectId == DomainPolicy.OwnerSubject && !u.Enabled)
            select new { Definition = definition, Version = version,
                ApprovalRisk = db.DomainApprovals.Where(a => a.DomainVersionId == version.Id && a.Decision == "Approved" && a.PolicyVersion == DomainPolicy.Version)
                    .OrderByDescending(a => a.At).Select(a => a.RiskClass).FirstOrDefault() }).ToListAsync(ct);
        await tx.CommitAsync(ct);
        return rows.Select(x => new DomainResolved(x.Definition.Contract(), x.Version.Contract(), status.Epoch,
            x.ApprovalRisk is not null, x.ApprovalRisk)).ToArray();
    }
}
