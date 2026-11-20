using System.Data;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;
using Jarvis.Orchestrator.Recordings;

namespace Jarvis.Persistence;

public sealed class PostgresPrivacyStore(IDbContextFactory<SessionDbContext> factory, AudioFenceChanges audioChanges, IRecordingAudioFiles? recordingFiles = null) : IPrivacyStore
{
    public async Task<PrivacyStatus> StatusAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.PrivacyControls.AsNoTracking().SingleAsync(x => x.Id == 1, ct)).Status();
    }
    private static void RequireEpoch(PrivacyControlRow row) => Guard.Require(PrivacyContext.Epoch is null || PrivacyContext.Epoch == row.Epoch,
        409, "This request predates data erasure. Reconnect before continuing.");
    private static async Task<PrivacyControlRow> Exclusive(SessionDbContext db, CancellationToken ct) =>
        (await db.PrivacyControls.FromSqlRaw("SELECT * FROM \"PrivacyControls\" WHERE \"Id\"=1 FOR UPDATE").ToListAsync(ct)).Single();
    private static async Task<(RuntimeDocument Row, RuntimeState State, PostgresRuntimeHotState Hot)> Runtime(SessionDbContext db, CancellationToken ct)
    {
        var row = await db.RuntimeDocuments.SingleAsync(x => x.Id == 1, ct);
        var state = JsonSerializer.Deserialize<RuntimeState>(row.Json)!;
        var hot = await PostgresRuntimeHotState.LoadAsync(db, state, row.Fingerprint, ct); return (row, state, hot);
    }
    private static PrivacyCounts AllCounts(RuntimeState state, int sessions, IReadOnlyList<WorkflowRun> workflows) => new(sessions,
        state.TrainingAttempts.Values.Count(x => x.DeletedAt is null), state.CoachingAttempts.Values.Count(x => x.DeletedAt is null),
        state.VoiceTurns.Count, state.ProviderResults.Count, workflows.Count(x => x.DeletedAt is null), state.AgentRuns.Values.Count(x => x.DeletedAt is null), ContextMemories: state.ContextMemories.Count);
    public async Task<CoreDataExport> ExportAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var control = await db.PrivacyControls.AsNoTracking().SingleAsync(x => x.Id == 1, ct); RequireEpoch(control);
        Guard.Require(!await db.ForgeCheckIns.AnyAsync(ct) && !await db.ForgeCapacity.AnyAsync(ct),
            503, "Protected Forge export is not available. No partial export was produced.");
        var (_, state, _) = await Runtime(db, ct);
        var sessions = (await db.Sessions.AsNoTracking().Include(x => x.Checkpoints).OrderBy(x => x.Id).ToListAsync(ct)).Select(x => x.ToContract()).ToList();
        var runs = (await db.WorkflowRuns.AsNoTracking().ToListAsync(ct)).Select(x => x.ToContract()).ToList();
        var events = await db.WorkflowEvents.AsNoTracking().OrderBy(x => x.OccurredAt).Select(x => new WorkflowEvent(x.EventId, x.RunId, x.Revision, x.Status, x.OccurredAt)).ToListAsync(ct);
        var jobs = (await db.PrivacyJobs.AsNoTracking().ToListAsync(ct)).Select(x => x.Contract()).ToList();
        var travel = await PostgresTravelStore.Snapshot(db, ct);
        var recordings = await PostgresRecordingStore.Snapshot(db, ct);
        var forge = (await db.ForgeProgrammes.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct)).Select(x => x.Contract()).ToArray();
        var export = PrivacyData.Export(control.Status(), state, sessions, runs, events, jobs, now, (await PostgresDomainRegistryStore.Snapshot(db, ct)).Export()) with {
            TravelRoutes = travel.Routes.Values.ToArray(), TravelLineStates = travel.Lines.Values.ToArray(), InterviewRecordings = recordings.Sessions.Values.Select(r => r.View).ToArray(),
            ForgeProgrammes = forge.Length == 0 ? null : forge };
        await tx.CommitAsync(ct); return export;
    }
    public async Task<RetentionPreview> PreviewAsync(RetentionPolicy policy, DateTimeOffset asOf, CancellationToken ct = default)
    {
        PrivacyData.Validate(policy);
        // Use the same pure eligibility rules as apply, without exposing internal claims or credentials.
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var control = await db.PrivacyControls.AsNoTracking().SingleAsync(x => x.Id == 1, ct); RequireEpoch(control);
        var (_, state, _) = await Runtime(db, ct);
        var sessions = (await db.Sessions.AsNoTracking().Include(x => x.Checkpoints).ToListAsync(ct)).Select(x => x.ToContract()).ToList();
        var runs = (await db.WorkflowRuns.AsNoTracking().ToListAsync(ct)).Select(x => x.ToContract()).ToList();
        var commands = PostgresDeviceCommandStore.Prunable(db, policy, asOf);
        var counts = PrivacyData.Counts(state, sessions, runs, policy, asOf) with
        {
            DeviceCommands = await commands.CountAsync(ct),
            DeviceReceipts = await db.DeviceReceipts.CountAsync(x => commands.Any(command => command.Id == x.CommandId), ct)
        };
        counts = RecordingRetention.Counts(await PostgresRecordingStore.Snapshot(db, ct), counts, asOf);
        var result = new RetentionPreview(asOf, PrivacyData.PolicyHash(policy), policy, counts);
        await tx.CommitAsync(ct); return result;
    }
    public async Task<IReadOnlyList<PrivacyJob>> JobsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var control = await db.PrivacyControls.AsNoTracking().SingleAsync(x => x.Id == 1, ct); RequireEpoch(control);
        var jobs = (await db.PrivacyJobs.AsNoTracking().OrderByDescending(x => x.CompletedAt).Take(100).ToListAsync(ct)).Select(x => x.Contract()).ToList();
        await tx.CommitAsync(ct); return jobs;
    }
    public async Task<PrivacyStatus> EraseAsync(EraseCoreRequest request, string keyFingerprint, DateTimeOffset now, CancellationToken ct = default)
    {
        now = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
        Guard.Require(request.RequestId != Guid.Empty && request.Confirmation == PrivacyData.ErasePhrase, 400, "Type the exact erasure confirmation and supply a request ID.");
        Guard.Require(keyFingerprint is { Length: 64 }, 401, "Authenticated key identity is required for erasure.");
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var control = await Exclusive(db, ct);
        if (control.Erased && control.ErasureRequestId == request.RequestId) return control.Status();
        RequireEpoch(control); Guard.Require(!control.Erased && control.Revision == request.ExpectedRevision, 409, "Privacy state changed. Reload before erasing.");
        var (row, state, hot) = await Runtime(db, ct);
        var runs = (await db.WorkflowRuns.AsNoTracking().ToListAsync(ct)).Select(x => x.ToContract()).ToList();
        var counts = AllCounts(state, await db.Sessions.CountAsync(ct), runs) with
        {
            DeviceGrants = await db.DeviceGrants.CountAsync(ct),
            DeviceCommands = await db.DeviceCommands.CountAsync(ct),
            DeviceReceipts = await db.DeviceReceipts.CountAsync(ct),
            ForgeProgrammes = await db.ForgeProgrammes.CountAsync(ct)
        };
        counts = RecordingRetention.Counts(await PostgresRecordingStore.Snapshot(db, ct), counts);
        // The exclusive privacy gate also excludes a file publisher holding the shared D2 gate.
        // Do not report erasure success if ciphertext deletion fails.
        if (recordingFiles is not null) await recordingFiles.EraseAsync(ct);
        await db.InterviewRecordings.ExecuteDeleteAsync(ct);
        hot.Apply(row, PrivacyData.EmptyAfterErase(state));
        await db.DomainDefinitions.ExecuteUpdateAsync(x => x.SetProperty(d => d.CurrentVersionId, (Guid?)null), ct);
        await db.DomainDefinitions.ExecuteDeleteAsync(ct);
        await db.TravelRoutes.ExecuteDeleteAsync(ct); await db.TravelLineStates.ExecuteDeleteAsync(ct);
        await db.TravelMonitorState.ExecuteUpdateAsync(x => x.SetProperty(t => t.Json, "{}"), ct);
        await db.Sessions.ExecuteDeleteAsync(ct); await db.WorkflowRuns.ExecuteDeleteAsync(ct); await db.DeviceGrants.ExecuteDeleteAsync(ct); await db.PrivacyJobs.ExecuteDeleteAsync(ct);
        await db.DeviceCommands.ExecuteDeleteAsync(ct);
        await db.ForgeCheckIns.ExecuteDeleteAsync(ct); await db.ForgeCapacity.ExecuteDeleteAsync(ct);
        await db.ForgeProgrammes.ExecuteDeleteAsync(ct);
        control.Epoch = checked(control.Epoch + 1); control.Revision++; control.Erased = true; control.ErasedAt = now;
        control.ErasureRequestId = request.RequestId; control.ErasureKeyFingerprint = keyFingerprint;
        db.PrivacyJobs.Add(new() { Id = request.RequestId, Kind = "erasure", Fingerprint = PrivacyData.Hash("erasure:" + control.Epoch), CompletedAt = now, CountsJson = JsonSerializer.Serialize(counts) });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        audioChanges.LeaseChanged(state.AudioLease, null);
        return control.Status();
    }
    public async Task<PrivacyStatus> ReopenAsync(ReopenCoreRequest request, string keyFingerprint, CancellationToken ct = default)
    {
        Guard.Require(request.Confirmation == PrivacyData.ReopenPhrase, 400, "Type the exact reopen confirmation.");
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var control = await Exclusive(db, ct); RequireEpoch(control);
        Guard.Require(control.Erased && control.Revision == request.ExpectedRevision, 409, "Core is not awaiting reopen or its state changed.");
        Guard.Require(keyFingerprint.Length == 64 && keyFingerprint != control.ErasureKeyFingerprint, 409, "Rotate Jarvis.ApiKey in appsettings.json, reconnect with the new key, then reopen.");
        control.Epoch = checked(control.Epoch + 1); control.Revision++; control.Erased = false;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return control.Status();
    }
    public async Task<PrivacyJob> RetainAsync(ApplyRetentionRequest request, RetentionPolicy policy, DateTimeOffset now, CancellationToken ct = default)
    {
        request = request with { AsOf = request.AsOf.ToUniversalTime() };
        PrivacyData.Validate(policy);
        Guard.Require(request.RequestId != Guid.Empty && request.Confirmation == PrivacyData.RetentionPhrase && request.PolicyFingerprint == PrivacyData.PolicyHash(policy),
            400, "Retention confirmation or policy fingerprint is invalid.");
        var fingerprint = PrivacyData.Hash(request.PolicyFingerprint + ":" + request.AsOf.ToUniversalTime().ToString("O"));
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var control = await Exclusive(db, ct); PrivacyContext.RequireWrite(control.Status());
        var prior = await db.PrivacyJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.RequestId, ct);
        if (prior is not null) { Guard.Require(prior.Kind == "retention" && prior.Fingerprint == fingerprint, 409, "Privacy job ID was reused for different input."); return prior.Contract(); }
        Guard.Require(request.AsOf <= now && request.AsOf >= now.AddDays(-1), 400, "Retention preview is stale. Reload it before applying.");
        var (row, state, hot) = await Runtime(db, ct);
        var sessionRows = await db.Sessions.Include(x => x.Checkpoints).ToListAsync(ct);
        var workflowRows = await db.WorkflowRuns.ToListAsync(ct);
        var runs = workflowRows.Select(x => x.ToContract()).ToList();
        var counts = PrivacyData.Counts(state, sessionRows.Select(x => x.ToContract()).ToList(), runs, policy, request.AsOf);
        var recordingState = await PostgresRecordingStore.Snapshot(db, ct);
        counts = RecordingRetention.Counts(recordingState, counts, request.AsOf);
        await RecordingRetention.ApplyAsync(recordingState, state, recordingFiles, request.AsOf, ct);
        foreach (var recording in db.InterviewRecordings.Local) recording.Json = JsonSerializer.Serialize(recordingState.Sessions[recording.Id]);
        foreach (var chunk in db.RecordingChunks.Local.ToArray()) if (!recordingState.Chunks.ContainsKey(chunk.Id)) db.RecordingChunks.Remove(chunk);
        var selection = PrivacyData.Select(state, sessionRows.Select(x => x.ToContract()).ToList(), runs, policy, request.AsOf);
        var voiceIds = PrivacyData.RetainRuntime(state, policy, request.AsOf, selection);
        foreach (var session in sessionRows)
        {
            if (session.Status == SessionStatus.Ended && session.UpdatedAt < request.AsOf.AddDays(-policy.SessionDays)) db.Sessions.Remove(session);
            else
            {
                if (state.RetiredSessions.Contains(session.Id)) {
                    session.Title = "Removed private interview practice"; session.Status = SessionStatus.Ended; session.ActiveDeviceId = null;
                    session.CurrentTask = null; session.HandoffJson = null; session.MessagesJson = "[]"; session.PendingVoiceProjectionIdsJson = "[]"; session.VoiceSummaryJson = null;
                    db.Checkpoints.RemoveRange(session.Checkpoints); session.Checkpoints.Clear(); continue;
                }
                var messages = JsonSerializer.Deserialize<List<ChatMessage>>(session.MessagesJson)!;
                if (messages.Any(x => voiceIds.Contains(x.RequestId)))
                {
                    session.MessagesJson = JsonSerializer.Serialize(messages.Where(x => !voiceIds.Contains(x.RequestId)));
                    session.VoiceSummaryJson = null;
                }
            }
        }
        foreach (var workflow in workflowRows)
        {
            var run = workflow.ToContract();
            if (run.DeletedAt is null && PrivacyData.Finished(run) && run.UpdatedAt < request.AsOf.AddDays(-policy.ArtifactDays))
            {
                var purged = PrivacyData.PurgeWorkflow(run, request.AsOf); workflow.Apply(purged);
                db.WorkflowEvents.Add(new() { EventId = Guid.NewGuid(), RunId = purged.Id, Revision = purged.Revision, Status = purged.Status, OccurredAt = purged.UpdatedAt });
            }
        }
        var grantsPruned = await PostgresDeviceGrantStore.Prunable(db, request.AsOf).ExecuteDeleteAsync(ct);
        var commands = PostgresDeviceCommandStore.Prunable(db, policy, request.AsOf);
        var receiptsPruned = await db.DeviceReceipts.CountAsync(x => commands.Any(command => command.Id == x.CommandId), ct);
        var commandsPruned = await commands.ExecuteDeleteAsync(ct);
        hot.Apply(row, state);
        counts = counts with { DeviceGrants = grantsPruned, DeviceCommands = commandsPruned, DeviceReceipts = receiptsPruned };
        var job = new PrivacyJobRow { Id = request.RequestId, Kind = "retention", Fingerprint = fingerprint, CompletedAt = now, CountsJson = JsonSerializer.Serialize(counts) };
        db.PrivacyJobs.Add(job); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return job.Contract();
    }
}
