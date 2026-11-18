using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Orchestrator.Runtime;

// In-memory mode shares one boundary across its three stores; PostgreSQL uses the control-row lock.
public sealed class MemoryPrivacyBoundary
{
    public object Sync { get; } = new();
    public PrivacyStatus Status { get; set; } = new(0, 1, false, null, null);
    public string? ErasureKeyFingerprint { get; set; }
    public Dictionary<Guid, PrivacyJob> Jobs { get; } = [];
    public Dictionary<Guid, DeviceGrant> DeviceGrants { get; internal set; } = [];
    internal Dictionary<Guid, DeviceCommand> DeviceCommands { get; } = [];
    internal Dictionary<Guid, List<DeviceReceiptView>> DeviceReceipts { get; } = [];
    public Task<T> Run<T>(Func<Task<T>> operation)
    {
        lock (Sync) { PrivacyContext.RequireWrite(Status); return Task.FromResult(operation().GetAwaiter().GetResult()); }
    }
}
public sealed class GuardedMemoryRuntime(InMemoryRuntimeStore inner, MemoryPrivacyBoundary boundary) : IRuntimeStore
{
    public Task<UsageReservation> SettleUsageAsync(Guid id, decimal actual, int input, int output, CancellationToken ct = default,
        DateTimeOffset? completedAt = null)
    {
        lock (boundary.Sync) return inner.UpdateAsync(s => UsageAccounting.Settle(s, id, actual, input, output, completedAt), ct);
    }
    public Task<T> ReadAsync<T>(Func<RuntimeState,T> read, CancellationToken ct = default) => boundary.Run(() => inner.ReadAsync(read, ct));
    public Task<bool> ReadPaidAiPausedAsync(CancellationToken ct = default) => boundary.Run(() => inner.ReadPaidAiPausedAsync(ct));
    public Task<T> UpdateAsync<T>(Func<RuntimeState,T> update, CancellationToken ct = default) => boundary.Run(() => inner.UpdateAsync(update, ct));
}
public sealed class GuardedMemorySessions(InMemorySessionStore inner, MemoryPrivacyBoundary boundary) : ISessionStore
{
    public Task<ConversationSession> AddAsync(ConversationSession value, CancellationToken ct = default) => boundary.Run(() => inner.AddAsync(value, ct));
    public Task<ConversationSession?> GetAsync(Guid id, CancellationToken ct = default) => boundary.Run(() => inner.GetAsync(id, ct));
    public Task<IReadOnlyList<ConversationSession>> ListAsync(CancellationToken ct = default) => boundary.Run(() => inner.ListAsync(ct));
    public Task<ConversationSession?> UpdateAsync(Guid id, Func<ConversationSession,ConversationSession> update, CancellationToken ct = default) => boundary.Run(() => inner.UpdateAsync(id, update, ct));
    public Task<ConversationSession?> AppendCheckpointAsync(Guid id, Func<ConversationSession,SessionCheckpoint> create, DateTimeOffset now, CancellationToken ct = default) => boundary.Run(() => inner.AppendCheckpointAsync(id, create, now, ct));
}
public sealed class GuardedMemoryWorkflows(InMemoryWorkflowStore inner, MemoryPrivacyBoundary boundary) : IWorkflowStore
{
    public Task<WorkflowRun> AddAsync(WorkflowRun value, CancellationToken ct = default) => boundary.Run(() => inner.AddAsync(value, ct));
    public Task<WorkflowRun?> GetAsync(Guid id, CancellationToken ct = default) => boundary.Run(() => inner.GetAsync(id, ct));
    public Task<IReadOnlyList<WorkflowRun>> ListAsync(CancellationToken ct = default) => boundary.Run(() => inner.ListAsync(ct));
    public Task<IReadOnlyList<WorkflowEvent>> EventsAsync(Guid id, int revision, CancellationToken ct = default) => boundary.Run(() => inner.EventsAsync(id, revision, ct));
    public Task<WorkflowRun?> ClaimAsync(DateTimeOffset now, Func<WorkflowRun,WorkflowRun> claim, CancellationToken ct = default) => boundary.Run(() => inner.ClaimAsync(now, claim, ct));
    public Task<WorkflowRun?> UpdateAsync(Guid id, Func<WorkflowRun,WorkflowRun> update, CancellationToken ct = default) => boundary.Run(() => inner.UpdateAsync(id, update, ct));
}
public sealed class InMemoryDeviceGrantStore(MemoryPrivacyBoundary boundary) : IDeviceGrantStore
{
    internal void Clear() => boundary.DeviceGrants.Clear();
    internal int PruneCore(DateTimeOffset now)
    {
        var expired = boundary.DeviceGrants.Values.Where(x => x.CanPrune(now)).Select(x => x.Id).ToArray();
        foreach (var id in expired) boundary.DeviceGrants.Remove(id);
        return expired.Length;
    }
    public Task<DeviceGrant> AddAsync(DeviceGrant grant, CancellationToken ct = default) => boundary.Run(() =>
        Task.FromResult(Add(boundary.DeviceGrants, grant)));
    internal static DeviceGrant Add(Dictionary<Guid, DeviceGrant> grants, DeviceGrant grant)
    {
        if (grants.TryGetValue(grant.Id, out var existing)) return existing.RequireSameRequest(grant);
        grants.Add(grant.Id, grant);
        return grant;
    }
    public Task<DeviceGrant?> GetAsync(Guid id, CancellationToken ct = default) => boundary.Run(() => Task.FromResult(boundary.DeviceGrants.GetValueOrDefault(id)));
    public Task<IReadOnlyList<DeviceGrant>> ListAsync(string? deviceId = null, CancellationToken ct = default) => boundary.Run(() =>
        Task.FromResult<IReadOnlyList<DeviceGrant>>(boundary.DeviceGrants.Values.Where(x => deviceId is null || x.DeviceId == deviceId)
            .OrderBy(x => x.DeviceId).ThenBy(x => x.CapabilityId).ToArray()));
    public Task<DeviceGrant?> FindActiveAsync(string deviceId, string capabilityId, string capabilityVersion, string credentialHash, DateTimeOffset now, CancellationToken ct = default) => boundary.Run(() =>
        Task.FromResult(boundary.DeviceGrants.Values.Where(x => x.DeviceId == deviceId && x.CapabilityId == capabilityId &&
            x.CapabilityVersion == capabilityVersion && x.CredentialHash == credentialHash && x.RevokedAt is null &&
            x.ExpiresAt > now && x.GrantedBy != null && x.Reason != null)
            .OrderByDescending(x => x.GrantedAt).FirstOrDefault()));
    public Task<bool> RevokeAsync(Guid id, DateTimeOffset now, CancellationToken ct = default) => boundary.Run(() =>
    {
        if (!boundary.DeviceGrants.TryGetValue(id, out var grant) || grant.RevokedAt is not null) return Task.FromResult(false);
        boundary.DeviceGrants[id] = grant with { RevokedAt = now }; return Task.FromResult(true);
    });
    public Task<int> RevokeForDeviceAsync(string deviceId, DateTimeOffset now, CancellationToken ct = default) => boundary.Run(() =>
        Task.FromResult(RevokeForDevice(boundary.DeviceGrants, deviceId, now)));
    internal static int RevokeForDevice(Dictionary<Guid, DeviceGrant> grants, string deviceId, DateTimeOffset now)
    {
        var active = grants.Values.Where(x => x.DeviceId == deviceId && x.RevokedAt is null).ToArray();
        foreach (var grant in active) grants[grant.Id] = grant with { RevokedAt = now };
        return active.Length;
    }
    public Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default) => boundary.Run(() =>
        Task.FromResult(PruneCore(now)));
}
public sealed class InMemoryDeviceCommandStore(MemoryPrivacyBoundary boundary) : IDeviceCommandReader
{
    public Task<IReadOnlyList<DeviceCommand>> ListForReviewAsync(DateTimeOffset now, CancellationToken ct = default) => boundary.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<DeviceCommand>>(boundary.DeviceCommands.Values
            .OrderByDescending(x => x.Status == DeviceCommandStatus.AwaitingConfirmation && x.ExpiresAt > now)
            .ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(IDeviceCommandReader.ReviewLimit).ToArray());
    });

    public Task<DeviceCommand?> GetAsync(Guid id, CancellationToken ct = default) => boundary.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(boundary.DeviceCommands.GetValueOrDefault(id));
    });
}

public sealed class InMemoryDeviceReceiptStore(MemoryPrivacyBoundary boundary) : IDeviceReceiptReader
{
    public Task<IReadOnlyList<DeviceReceiptView>> ListAsync(Guid commandId, CancellationToken ct = default) => boundary.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<DeviceReceiptView>>(boundary.DeviceReceipts.GetValueOrDefault(commandId)?.ToArray() ?? []);
    });
}

public sealed class InMemoryPrivacyStore(InMemoryRuntimeStore runtime, InMemorySessionStore sessions, InMemoryWorkflowStore workflows,
    MemoryPrivacyBoundary boundary, InMemoryDeviceGrantStore deviceGrants) : IPrivacyStore
{
    private RuntimeState State() => runtime.ReadAsync(x => x).GetAwaiter().GetResult();
    private List<ConversationSession> Sessions() => sessions.ListAsync().GetAwaiter().GetResult().ToList();
    public Task<PrivacyStatus> StatusAsync(CancellationToken ct = default) { lock(boundary.Sync) return Task.FromResult(boundary.Status); }
    public Task<CoreDataExport> ExportAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        lock(boundary.Sync)
        {
            RequireEpoch(); var snapshot = workflows.PrivacySnapshot();
            return Task.FromResult(PrivacyData.Export(boundary.Status, State(), Sessions(), snapshot.Runs, snapshot.Events, boundary.Jobs.Values.ToList(), now));
        }
    }
    public Task<IReadOnlyList<PrivacyJob>> JobsAsync(CancellationToken ct = default)
    { lock(boundary.Sync) { RequireEpoch(); return Task.FromResult<IReadOnlyList<PrivacyJob>>(boundary.Jobs.Values.OrderByDescending(x => x.CompletedAt).Take(100).ToList()); } }
    private void RequireEpoch() => Guard.Require(PrivacyContext.Epoch is null || PrivacyContext.Epoch == boundary.Status.Epoch, 409, "This request predates data erasure. Reconnect before continuing.");
    public Task<RetentionPreview> PreviewAsync(RetentionPolicy policy, DateTimeOffset asOf, CancellationToken ct = default)
    {
        lock (boundary.Sync)
        {
            RequireEpoch(); PrivacyData.Validate(policy);
            var commands = PrunableCommands(policy, asOf).ToArray();
            var counts = PrivacyData.Counts(State(), Sessions(), workflows.PrivacySnapshot().Runs, policy, asOf) with
            {
                DeviceCommands = commands.Length,
                DeviceReceipts = commands.Sum(id => boundary.DeviceReceipts.GetValueOrDefault(id)?.Count ?? 0)
            };
            return Task.FromResult(new RetentionPreview(asOf, PrivacyData.PolicyHash(policy), policy, counts));
        }
    }

    private IEnumerable<Guid> PrunableCommands(RetentionPolicy policy, DateTimeOffset asOf)
    {
        var cutoff = asOf.AddDays(-policy.ArtifactDays);
        return boundary.DeviceCommands.Values.Where(x => x.ExpiresAt <= asOf && x.CreatedAt < cutoff).Select(x => x.Id);
    }
    public Task<PrivacyStatus> EraseAsync(EraseCoreRequest request, string fingerprint, DateTimeOffset now, CancellationToken ct = default)
    {
        lock(boundary.Sync)
        {
            Guard.Require(request.RequestId != Guid.Empty && request.Confirmation == PrivacyData.ErasePhrase, 400, "Type the exact erasure confirmation and supply a request ID.");
            Guard.Require(fingerprint is { Length: 64 }, 401, "Authenticated key identity is required for erasure.");
            if (boundary.Status.Erased && boundary.Status.ErasureRequestId == request.RequestId) return Task.FromResult(boundary.Status);
            RequireEpoch(); Guard.Require(!boundary.Status.Erased && boundary.Status.Revision == request.ExpectedRevision, 409, "Privacy state changed. Reload before erasing.");
            var state = State(); var snapshot = workflows.PrivacySnapshot();
            var counts = new PrivacyCounts(Sessions().Count, state.TrainingAttempts.Values.Count(x => x.DeletedAt is null), state.CoachingAttempts.Values.Count(x => x.DeletedAt is null),
                state.VoiceTurns.Count, state.ProviderResults.Count, snapshot.Runs.Count(x => x.DeletedAt is null), state.AgentRuns.Values.Count(x => x.DeletedAt is null));
            var removed = counts with
            {
                DeviceGrants = boundary.DeviceGrants.Count, DeviceCommands = boundary.DeviceCommands.Count,
                DeviceReceipts = boundary.DeviceReceipts.Values.Sum(x => x.Count), ContextMemories = state.ContextMemories.Count
            };
            runtime.ReplacePrivacyData(PrivacyData.EmptyAfterErase(state)); sessions.ReplacePrivacyData([]); workflows.ReplacePrivacyData([], []); boundary.Jobs.Clear(); deviceGrants.Clear();
            boundary.DeviceCommands.Clear();
            boundary.DeviceReceipts.Clear();
            boundary.Status = new(boundary.Status.Epoch + 1, boundary.Status.Revision + 1, true, now, request.RequestId); boundary.ErasureKeyFingerprint = fingerprint;
            boundary.Jobs[request.RequestId] = new(request.RequestId, "erasure", PrivacyData.Hash("erasure:" + boundary.Status.Epoch), now, removed);
            return Task.FromResult(boundary.Status);
        }
    }
    public Task<PrivacyStatus> ReopenAsync(ReopenCoreRequest request, string fingerprint, CancellationToken ct = default)
    {
        lock(boundary.Sync)
        {
            RequireEpoch(); Guard.Require(request.Confirmation == PrivacyData.ReopenPhrase, 400, "Type the exact reopen confirmation.");
            Guard.Require(boundary.Status.Erased && request.ExpectedRevision == boundary.Status.Revision, 409, "Core is not awaiting reopen or its state changed.");
            Guard.Require(fingerprint.Length == 64 && fingerprint != boundary.ErasureKeyFingerprint, 409, "Rotate Jarvis.ApiKey in appsettings.json, reconnect with the new key, then reopen.");
            boundary.Status = boundary.Status with { Epoch = boundary.Status.Epoch + 1, Revision = boundary.Status.Revision + 1, Erased = false }; return Task.FromResult(boundary.Status);
        }
    }
    public Task<PrivacyJob> RetainAsync(ApplyRetentionRequest request, RetentionPolicy policy, DateTimeOffset now, CancellationToken ct = default)
    {
        request = request with { AsOf = request.AsOf.ToUniversalTime() };
        lock(boundary.Sync)
        {
            PrivacyContext.RequireWrite(boundary.Status); PrivacyData.Validate(policy);
            Guard.Require(request.RequestId != Guid.Empty && request.Confirmation == PrivacyData.RetentionPhrase && request.PolicyFingerprint == PrivacyData.PolicyHash(policy), 400, "Retention confirmation or policy fingerprint is invalid.");
            var fingerprint = PrivacyData.Hash(request.PolicyFingerprint + ":" + request.AsOf.ToUniversalTime().ToString("O"));
            if (boundary.Jobs.TryGetValue(request.RequestId, out var prior)) { Guard.Require(prior.Kind == "retention" && prior.Fingerprint == fingerprint, 409, "Privacy job ID was reused for different input."); return Task.FromResult(prior); }
            Guard.Require(request.AsOf <= now && request.AsOf >= now.AddDays(-1), 400, "Retention preview is stale.");
            var state = State(); var list = Sessions(); var snapshot = workflows.PrivacySnapshot(); var selection = PrivacyData.Select(state, list, snapshot.Runs, policy, request.AsOf);
            var voice = PrivacyData.RetainRuntime(state, policy, request.AsOf, selection);
            var kept = list.Where(x => !selection.Sessions.Contains(x.Id)).Select(session =>
            {
                var messages = session.Messages ?? [];
                var retained = messages.Where(message => !voice.Contains(message.RequestId)).ToArray();
                return retained.Length == messages.Count ? session : session with { Messages = retained, VoiceSummary = null };
            }).ToList();
            var events = snapshot.Events;
            var runs = snapshot.Runs.Select(x => selection.Workflows.Contains(x.Id) ? PrivacyData.PurgeWorkflow(x, request.AsOf) : x).ToList();
            foreach (var run in runs.Where(x => selection.Workflows.Contains(x.Id))) events.Add(new(Guid.NewGuid(), run.Id, run.Revision, run.Status, run.UpdatedAt));
            runtime.ReplacePrivacyData(state); sessions.ReplacePrivacyData(kept); workflows.ReplacePrivacyData(runs, events);
            var prunedGrants = deviceGrants.PruneCore(request.AsOf);
            var prunedCommands = PrunableCommands(policy, request.AsOf).ToArray();
            var prunedReceipts = 0;
            foreach (var id in prunedCommands)
            {
                boundary.DeviceCommands.Remove(id);
                if (boundary.DeviceReceipts.Remove(id, out var receipts)) prunedReceipts += receipts.Count;
            }
            var removed = selection.Counts with { DeviceGrants = prunedGrants, DeviceCommands = prunedCommands.Length, DeviceReceipts = prunedReceipts };
            var job = new PrivacyJob(request.RequestId, "retention", fingerprint, now, removed); boundary.Jobs.Add(job.Id, job); return Task.FromResult(job);
        }
    }
}
