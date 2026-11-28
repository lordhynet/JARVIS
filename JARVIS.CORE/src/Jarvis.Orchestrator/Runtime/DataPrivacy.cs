using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Training;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Orchestrator.Runtime;

public interface IPrivacyReader
{
    Task<PrivacyStatus> StatusAsync(CancellationToken ct = default);
    Task<CoreDataExport> ExportAsync(DateTimeOffset now, CancellationToken ct = default);
    Task<RetentionPreview> PreviewAsync(RetentionPolicy policy, DateTimeOffset asOf, CancellationToken ct = default);
    Task<IReadOnlyList<PrivacyJob>> JobsAsync(CancellationToken ct = default);
}
public interface IPrivacyStore : IPrivacyReader
{
    Task<PrivacyStatus> EraseAsync(EraseCoreRequest request, string keyFingerprint, DateTimeOffset now, CancellationToken ct = default);
    Task<PrivacyStatus> ReopenAsync(ReopenCoreRequest request, string keyFingerprint, CancellationToken ct = default);
    Task<PrivacyJob> RetainAsync(ApplyRetentionRequest request, RetentionPolicy policy, DateTimeOffset now, CancellationToken ct = default);
}

// Captured at authenticated request/work start and checked again at every persistence boundary.
public static class PrivacyContext
{
    private static readonly AsyncLocal<long?> Current = new();
    public static long? Epoch => Current.Value;
    public static IDisposable Enter(long epoch) { var old = Current.Value; Current.Value = epoch; return new Scope(() => Current.Value = old); }
    private sealed class Scope(Action close) : IDisposable { public void Dispose() => close(); }
    public static void RequireWrite(PrivacyStatus status)
    {
        Guard.Require(!status.Erased, 503, "Core data has been erased. Rotate the Core API key and explicitly reopen before writing new data.");
        Guard.Require(Epoch is null || Epoch == status.Epoch, 409, "This operation started before a data-erasure boundary and cannot publish. Start a new request.");
    }
}

public static class PrivacyData
{
    public const string ErasePhrase = "ERASE MY CORE DATA";
    public const string ReopenPhrase = "START A NEW CORE";
    public const string RetentionPhrase = "APPLY RETENTION";
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string PolicyHash(RetentionPolicy policy) => Hash(JsonSerializer.Serialize(policy));
    public static void Validate(RetentionPolicy policy) => Guard.Require(new[] { policy.SessionDays, policy.AnswerDays, policy.VoiceDays,
        policy.ProviderCacheDays, policy.ArtifactDays, policy.CreationReceiptDays }.All(x => x is >= 1 and <= 3650) && policy.SweepMinutes is >= 1 and <= 1440,
        400, "Retention days must be 1–3650 and sweep minutes 1–1440.");
    public static CoreDataExport Export(PrivacyStatus status, RuntimeState state, IReadOnlyList<ConversationSession> sessions,
        IReadOnlyList<WorkflowRun> workflows, IReadOnlyList<WorkflowEvent> events, IReadOnlyList<PrivacyJob> jobs, DateTimeOffset now, DomainRegistryExport? domains = null) => new CoreDataExport(1, now, status,
        sessions, state.Stories.Values.ToList(), state.Learning.Values.ToList(), state.Reviews.Values.ToList(),
        state.TrainingAttempts.Values.Where(x => x.DeletedAt is null).ToList(), state.TrainingDrills.Values.ToList(),
        state.CoachingAttempts.Values.Where(x => x.DeletedAt is null).ToList(), state.AiCoachAssessments.Values.ToList(),
        state.Programmes.Values.ToList(), state.Devices.Values.ToList(), state.VoiceTurns.Values.ToList(), state.Tools.Values.ToList(),
        workflows.Where(x => x.DeletedAt is null).Select(WorkflowRunView.From).ToList(), events,
        state.AgentRuns.Values.Where(x => x.DeletedAt is null).ToList(), state.AgentTasks.Values.Where(x => state.AgentRuns.GetValueOrDefault(x.RunId)?.DeletedAt is null)
            .Select(x => new AgentTaskExport(x.Id, x.RunId, x.Role, x.Status, x.Input, x.Output, x.ErrorCode, x.SourceRefs ?? [])).ToList(),
        state.AgentSchedules.Values.ToList(), state.AgentControls, state.AgentOutbox.Values.ToList(), state.ProviderResults.Values.ToList(),
        state.UsageReservations.Values.ToList(), jobs, state.Goals.Values.Where(x => x.Status != GoalStatus.Deleted).ToList(),
        state.AgentTriggers.Values.ToList(), state.UsageReconciliations.Values.ToList(), state.ImportedDocuments.Values.Where(x => x.DeletedAt is null).ToList(), state.SourceClaims.Values.ToList(), state.VoiceRecalls.Values.ToList(),
        state.GbpCommitments.Values.ToList(), state.BudgetApprovals.Values.ToList(), state.BudgetReceipts.Values.ToList(), state.VoiceCoachings.Values.ToList(), state.ProviderAttempts.Values.ToList(), domains ?? state.DomainRegistry.Export(),
        state.AgentToolGrants.Values.ToArray(), state.AgentToolInvocations.Values.ToArray(), state.AgentActions.Values.ToArray(), state.DeviceVoiceAuthorities, state.ContextMemories.Values.ToArray()) with {
            CreationReceipts = state.CreationReceipts.Count == 0 ? null : state.CreationReceipts.Values.OrderBy(x => x.RequestId).Select(x => x.Export()).ToArray() };
    public static RuntimeState EmptyAfterErase(RuntimeState old) => new()
    {
        UsageReservations = old.UsageReservations, NextAudioFence = checked(old.NextAudioFence + 1), AgentControls = new(Paused: true, PaidAiPaused: true, AgentsPaused: true),
        AudioDrainUntil = old.AudioLease?.ExpiresAt ?? old.AudioDrainUntil,
        BudgetScopeId = old.BudgetScopeId, BudgetEpoch = checked(old.BudgetEpoch + 1), BudgetRevision = checked(old.BudgetRevision + 1),
        GbpCommitments = old.GbpCommitments,
        FxRefresh = old.FxRefresh,
        BudgetApprovals = old.BudgetApprovals.ToDictionary(x => x.Key, x => x.Value with { Revoked = true, ActorId = "" }),
        BudgetReceipts = old.BudgetReceipts.ToDictionary(x => x.Key, x => x.Value with { ActorId = "", Evidence = "" }),
        UsageReconciliations = old.UsageReconciliations.ToDictionary(x => x.Key, x => x.Value with { Evidence = "" })
    };
    public static DateTimeOffset AnswerAt(TrainingAttempt answer) => new[] { answer.SubmittedAt,
        answer.Answers.LastOrDefault()?.At ?? answer.SubmittedAt, answer.Assessments.LastOrDefault()?.At ?? answer.SubmittedAt }.Max();
    public static DateTimeOffset CoachAt(RuntimeState state, CoachingAttempt answer) => new[] { answer.CreatedAt,
        answer.Answers.LastOrDefault()?.At ?? answer.CreatedAt,
        answer.AiAssessmentId is Guid id ? state.AiCoachAssessments.GetValueOrDefault(id)?.At ?? answer.CreatedAt : answer.CreatedAt }.Max();
    public static bool Finished(WorkflowRun run) => run.Status is WorkflowStatus.Succeeded or WorkflowStatus.Cancelled or WorkflowStatus.Expired or WorkflowStatus.Failed;
    public sealed record Selection(HashSet<Guid> Sessions, HashSet<Guid> Training, HashSet<Guid> Coaching, HashSet<Guid> Voice,
        HashSet<Guid> Providers, HashSet<Guid> Workflows, HashSet<Guid> Agents, HashSet<Guid> Imports, HashSet<Guid> Memories, HashSet<Guid> CreationReceipts)
    {
        public PrivacyCounts Counts => new(Sessions.Count, Training.Count, Coaching.Count, Voice.Count, Providers.Count, Workflows.Count, Agents.Count, Imports.Count, ContextMemories: Memories.Count, CreationReceipts: CreationReceipts.Count);
    }
    public static Selection Select(RuntimeState state, IReadOnlyList<ConversationSession> sessions, IReadOnlyList<WorkflowRun> workflows, RetentionPolicy p, DateTimeOffset at)
    {
        var deadSessions = sessions.Where(x => x.Status == SessionStatus.Ended && x.UpdatedAt < at.AddDays(-p.SessionDays)).Select(x => x.Id).ToHashSet();
        var training = state.TrainingAttempts.Values.Where(x => x.DeletedAt is null && (AnswerAt(x) < at.AddDays(-p.AnswerDays) || deadSessions.Contains(x.SessionId))).Select(x => x.Id).ToHashSet();
        var coaching = state.CoachingAttempts.Values.Where(x => x.DeletedAt is null && (CoachAt(state, x) < at.AddDays(-p.AnswerDays) || deadSessions.Contains(x.SessionId))).Select(x => x.Id).ToHashSet();
        var agents = state.AgentRuns.Values.Where(x => x.DeletedAt is null && x.Status is not (AgentRunStatus.Queued or AgentRunStatus.Running) && x.CreatedAt < at.AddDays(-p.ArtifactDays)).Select(x => x.Id).ToHashSet();
        var taskIds = state.AgentTasks.Values.Where(x => agents.Contains(x.RunId)).Select(x => x.Id).ToHashSet();
        return new(deadSessions, training, coaching,
            state.VoiceTurns.Values.Where(x => x.At < at.AddDays(-p.VoiceDays) || deadSessions.Contains(x.SessionId)).Select(x => x.Id).ToHashSet(),
            state.ProviderResults.Values.Where(x => x.At < at.AddDays(-p.ProviderCacheDays) || training.Contains(x.ScopeId) || coaching.Contains(x.ScopeId) || deadSessions.Contains(x.ScopeId) || taskIds.Contains(x.ScopeId)).Select(x => x.Id).ToHashSet(),
            workflows.Where(x => x.DeletedAt is null && Finished(x) && x.UpdatedAt < at.AddDays(-p.ArtifactDays)).Select(x => x.Id).ToHashSet(), agents, state.ImportedDocuments.Values.Where(x => x.DeletedAt is null && x.Kind == "interview" && x.ImportedAt < at.AddDays(-p.ArtifactDays)).Select(x => x.Id).ToHashSet(),
            state.ContextMemories.Values.Where(x => x.ExpiresAt <= at || x.SessionId is Guid session && deadSessions.Contains(session) ||
                x.Source.Kind == "voice" && state.VoiceTurns.TryGetValue(x.Source.Id, out var turn) && turn.At < at.AddDays(-p.VoiceDays) ||
                x.Source.Kind == "import" && state.ImportedDocuments.TryGetValue(x.Source.Id, out var document) && document.Kind == "interview" && document.ImportedAt < at.AddDays(-p.ArtifactDays))
                .Select(x => x.Id).ToHashSet(),
            state.CreationReceipts.Values.Where(x => x.Status == LearningCreationReceipt.Created && x.Result is { } result &&
                result.CreatedAt < at.AddDays(-p.CreationReceiptDays)).Select(x => x.RequestId).ToHashSet());
    }
    public static PrivacyCounts Counts(RuntimeState s, IReadOnlyList<ConversationSession> sessions, IReadOnlyList<WorkflowRun> workflows, RetentionPolicy p, DateTimeOffset at) => Select(s, sessions, workflows, p, at).Counts;
    public static HashSet<Guid> RetainRuntime(RuntimeState state, RetentionPolicy p, DateTimeOffset at, Selection selection)
    {
        var privatePractices = state.VoiceCoachings.Values.Where(x => x.SourceRecordingId is not null).ToArray();
        foreach (var id in selection.Imports) ImportService.Purge(state, state.ImportedDocuments[id], at);
        // Source purges still need original receipt lineage before age retirement removes it.
        foreach (var id in selection.CreationReceipts) state.CreationReceipts[id] = state.CreationReceipts[id].Retire(at);
        var affected = new HashSet<Guid>();
        foreach (var answer in state.TrainingAttempts.Values.Where(x => x.DeletedAt is null && selection.Training.Contains(x.Id)).ToList())
        {
            state.TrainingAttempts[answer.Id] = answer with { Answers = [], Assessments = [], CanonicalAssessmentId = null, DeletedAt = at, Revision = answer.Revision + 1 };
            ReasoningLifecycle.Retire(state, answer.Id);
            affected.Add(answer.QuestionId);
        }
        foreach (var id in affected) if (state.TrainingBaselines.ContainsKey(id)) TrainingProjection.Rebuild(state, id);
        foreach (var answer in state.CoachingAttempts.Values.Where(x => selection.Coaching.Contains(x.Id)).ToList()) CoachingService.Purge(state, answer, at);
        var voiceIds = selection.Voice;
        foreach (var coaching in state.VoiceCoachings.Values.Where(x => selection.Sessions.Contains(x.SessionId) ||
            x.AttemptId is Guid attempt && selection.Coaching.Contains(attempt) ||
            x.CreatedAt < at.AddDays(-p.VoiceDays) || voiceIds.Contains(x.StartUtteranceId) ||
            x.AnswerUtteranceId is Guid utterance && voiceIds.Contains(utterance) ||
            x.CorrectionStartUtteranceId is Guid correction && voiceIds.Contains(correction) ||
            (x.ScenarioAnswers ?? []).Any(voiceIds.Contains) || (x.ScenarioAttempts ?? []).Any(selection.Coaching.Contains)).ToList()) state.VoiceCoachings.Remove(coaching.Id);
        foreach (var practice in privatePractices.Where(x => !state.VoiceCoachings.ContainsKey(x.Id)))
            Jarvis.Orchestrator.Recordings.RecordedPracticePrivacy.PurgeSession(state, practice, at);
        foreach (var id in selection.Memories) { AgentRuntime.InvalidateSource(state, "memory", id); state.ContextMemories.Remove(id); }
        foreach (var id in voiceIds) { AgentRuntime.InvalidateSource(state, "voice", id); state.VoiceTurns.Remove(id); }
        foreach (var id in selection.Providers) {
            state.ProviderResults.Remove(id);
            if (state.ProviderAttempts.TryGetValue(id, out var attempt)) state.ProviderAttempts[id] = attempt with { Fingerprint = "" };
        }
        foreach (var id in state.DeviceVoiceAuthorities.Where(p => selection.Sessions.Contains(p.Value.SessionId) || voiceIds.Contains(p.Value.SourceId)).Select(p => p.Key).ToArray()) state.DeviceVoiceAuthorities.Remove(id);
        foreach (var id in selection.Sessions) { state.RetiredSessions.Add(id); state.AgentVoiceReviews.Remove(id); state.PracticeContextManifests.Remove(id); ReasoningLifecycle.Retire(state, id); }
        foreach (var recall in state.VoiceRecalls.Values.Where(x => selection.Sessions.Contains(x.SessionId) ||
            voiceIds.Contains(x.StartUtteranceId) || x.AnswerUtteranceId is Guid answer && voiceIds.Contains(answer) ||
            x.RatingUtteranceId is Guid rated && voiceIds.Contains(rated)).ToList()) state.VoiceRecalls.Remove(recall.Id);
        foreach (var drill in state.TrainingDrills.Values.Where(x => selection.Sessions.Contains(x.SessionId)).ToList()) state.TrainingDrills[drill.Id] = drill with { Status = "Cancelled", Revision = drill.Revision + 1 };
        foreach (var tool in state.Tools.Values.Where(x => selection.Sessions.Contains(x.Invocation.SessionId)).ToList())
            state.Tools[tool.Invocation.Id] = tool with { Invocation = tool.Invocation with { ArgumentsJson = "{}", Status = ToolInvocationStatus.Rejected, Permission = ToolPermission.Denied }, Result = null, CompletedAt = at };
        foreach (var run in state.AgentRuns.Values.Where(x => selection.Agents.Contains(x.Id)).ToList())
        {
            Jarvis.Orchestrator.Workflows.AgentToolLifecycle.Retire(state, run.Id, at, true);
            state.AgentRuns[run.Id] = run with { DeletedAt = at, Status = AgentRunStatus.Cancelled,
                WorkflowPolicy = run.WorkflowPolicy is { } policy ? policy with { DomainContext = null, PracticeTemplate = null } : null };
            foreach (var id in run.TaskIds)
            {
                var task = state.AgentTasks[id]; state.AgentTasks[id] = task with { Input = "", Output = null, ClaimToken = null, Status = AgentTaskStatus.Cancelled };
                ReasoningLifecycle.Retire(state, id);
            }
            foreach (var notice in state.AgentOutbox.Values.Where(x => x.RunId == run.Id).ToList()) state.AgentOutbox.Remove(notice.Id);
        }
        foreach (var ticket in state.VoiceTickets.Where(x => x.Value.ExpiresAt <= at).Select(x => x.Key).ToList()) state.VoiceTickets.Remove(ticket);
        foreach (var notice in state.AgentOutbox.Values.Where(x => x.Category == NotificationCategories.Travel && x.ExpiresAt <= at).ToArray()) state.AgentOutbox.Remove(notice.Id);
        return voiceIds;
    }
    public static WorkflowRun PurgeWorkflow(WorkflowRun run, DateTimeOffset at) => run with { Result = null, Brief = null, DeletedAt = at,
        ClaimToken = null, WorkerId = null, LeaseExpiresAt = null, AvailableAt = null, Status = WorkflowStatus.Cancelled, Revision = run.Revision + 1, UpdatedAt = at };
}
