using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Runtime;

public sealed record PendingFxMove(DateOnly PublishedOn, decimal Rate);
public sealed record FxRefreshState(BudgetFxQuote? Quote = null, decimal? UnadjustedRate = null,
    DateTimeOffset? LastAttemptAt = null, string? LastError = null, PendingFxMove? PendingMove = null, int ConsecutiveFailures = 0);

public sealed class RuntimeState
{
    private Action? trainingLoader;
    [System.Text.Json.Serialization.JsonIgnore] public bool AreTrainingPropertiesLoaded => trainingLoader is null;
    public void DeferTrainingProperties(Action loader) => trainingLoader = loader;
    public void LoadTrainingProperties() {
        if (trainingLoader is not { } load) return;
        trainingLoader = null;
        try { load(); } catch { trainingLoader = load; throw; }
    }
    public void CloseTrainingProperties() {
        if (trainingLoader is not null) trainingLoader = () => throw new InvalidOperationException("Materialize training projections inside the store callback.");
    }
    private Action? learningLoader;
    [System.Text.Json.Serialization.JsonIgnore] public bool AreLearningPropertiesLoaded => learningLoader is null;
    public void DeferLearningProperties(Action loader) => learningLoader = loader;
    public void LoadLearningProperties() {
        if (learningLoader is not { } load) return;
        learningLoader = null;
        try { load(); } catch { learningLoader = load; throw; }
    }
    public void CloseLearningProperties() {
        if (learningLoader is not null) learningLoader = () => throw new InvalidOperationException("Materialize learning projections inside the store callback.");
    }
    private Action? coldLoader;
    [System.Text.Json.Serialization.JsonIgnore] public bool AreColdPropertiesLoaded => coldLoader is null;
    public void DeferColdProperties(Action loader) => coldLoader = loader;
    public void LoadColdProperties() {
        if (coldLoader is not { } load) return;
        coldLoader = null;
        try { load(); } catch { coldLoader = load; throw; }
    }
    public void CloseColdProperties() {
        if (coldLoader is not null) coldLoader = () => throw new InvalidOperationException("Materialize runtime projections inside the store callback.");
    }
    // In-memory registry only. PostgreSQL keeps definitions in the dedicated relational tables.
    public Jarvis.Orchestrator.Domains.DomainRegistryState DomainRegistry { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = new();
    public Dictionary<Guid, ProviderAttempt> ProviderAttempts { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public HashSet<Guid> RetiredReasoningScopes { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<string, HubAccessTicket> HubTickets { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<string, DeviceCredential> DeviceCredentials { get; set; } = [];
    public Dictionary<string, DevicePairingCode> DevicePairingCodes { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, VoiceCoaching> VoiceCoachings { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public string? BudgetScopeId { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } }
    public FxRefreshState? FxRefresh { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } }
    public long BudgetEpoch { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } }
    public int BudgetRevision { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } }
    public Dictionary<Guid, GbpCommitment> GbpCommitments { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, BudgetApproval> BudgetApprovals { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, BudgetManagementReceipt> BudgetReceipts { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, VoiceRecall> VoiceRecalls { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, BrowserPushSubscription> PushSubscriptions { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, PushDelivery> PushDeliveries { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, ImportedDocument> ImportedDocuments { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, SourceClaim> SourceClaims { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, PersonalGoal> Goals { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, DurableAgentTrigger> AgentTriggers { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, UsageReconciliation> UsageReconciliations { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public HashSet<Guid> RetiredSessions { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, TrainingDrill> TrainingDrills { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    // Per-conversation record of personal sources shown to the realtime model (generated-practice lineage). Content-free: IDs and versions only.
    public Dictionary<Guid, Jarvis.Orchestrator.Training.PracticeContextManifest> PracticeContextManifests { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, UsageReservation> UsageReservations { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, ProviderResult> ProviderResults { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public AgentControls AgentControls { get; set; } = new();
    public Dictionary<Guid, AgentSchedule> AgentSchedules { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, AgentRun> AgentRuns { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, AgentTask> AgentTasks { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, AgentToolGrant> AgentToolGrants { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, AgentToolInvocation> AgentToolInvocations { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, AgentActionProposal> AgentActions { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, AgentVoiceReview> AgentVoiceReviews { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, DeviceVoiceAuthority> DeviceVoiceAuthorities { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, ContextMemoryItem> ContextMemories { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, AgentNotification> AgentOutbox { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, AiCoachAssessment> AiCoachAssessments { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, AiCoachAssessmentClaim> AiCoachAssessmentClaims { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public AudioLease? AudioLease { get; set; }
    public AudioLease? AudioDrainLease { get; set; }
    public DateTimeOffset? AudioDrainUntil { get; set; }
    public long NextAudioFence { get; set; } = 1;
    public Dictionary<string, VoiceTicketRecord> VoiceTickets { get; set; } = [];
    public Dictionary<string, List<DateTimeOffset>> WakeCandidateAttempts { get; set; } = [];
    private Dictionary<Guid, VoiceTurn>? voiceTurns = [];
    private Func<Dictionary<Guid, VoiceTurn>>? voiceLoader;
    public Dictionary<Guid, VoiceTurn> VoiceTurns {
        get { if (voiceTurns is null) { voiceTurns = voiceLoader!(); voiceLoader = null; } return voiceTurns; }
        set { voiceTurns = value; voiceLoader = null; }
    }
    [System.Text.Json.Serialization.JsonIgnore] public bool AreVoiceTurnsLoaded => voiceTurns is not null;
    // Persistence-only hydration inside the existing transaction; callbacks remain synchronous.
    public void DeferVoiceTurns(Func<Dictionary<Guid, VoiceTurn>> loader) { voiceTurns = null; voiceLoader = loader; }
    public void CloseVoiceTurns() {
        if (voiceTurns is null) voiceLoader = () => throw new InvalidOperationException("Materialize runtime projections inside the store callback.");
    }
    public Dictionary<Guid, CoachingAttempt> CoachingAttempts { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, TrainingOperation> CoachingOperations { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<Guid, ImprovementProgramme> Programmes { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Guid? ActiveProgrammeId { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } }
    public Dictionary<Guid, TrainingAttempt> TrainingAttempts { get { LoadTrainingProperties(); return field; } set { LoadTrainingProperties(); field = value; } } = [];
    public Dictionary<Guid, TrainingOperation> TrainingOperations { get { LoadTrainingProperties(); return field; } set { LoadTrainingProperties(); field = value; } } = [];
    public Dictionary<Guid, LearningItem> TrainingBaselines { get { LoadLearningProperties(); return field; } set { LoadLearningProperties(); field = value; } } = [];
    public Dictionary<Guid, HashSet<Guid>> TrainingBaselineReviews { get { LoadLearningProperties(); return field; } set { LoadLearningProperties(); field = value; } } = [];
    public Dictionary<Guid, long> TrainingManualSequences { get { LoadLearningProperties(); return field; } set { LoadLearningProperties(); field = value; } } = [];
    public long NextTrainingSequence { get { LoadLearningProperties(); return field; } set { LoadLearningProperties(); field = value; } } = 1;
    public Dictionary<Guid, Story> Stories { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
    public Dictionary<string, RegisteredDevice> Devices { get; set; } = [];
    public Dictionary<Guid, LearningItem> Learning { get { LoadLearningProperties(); return field; } set { LoadLearningProperties(); field = value; } } = [];
    public Dictionary<Guid, LearningReview> Reviews { get { LoadLearningProperties(); return field; } set { LoadLearningProperties(); field = value; } } = [];
    public Dictionary<Guid, LearningCreationReceipt> CreationReceipts { get { LoadLearningProperties(); return field; } set { LoadLearningProperties(); field = value; } } = [];
    public Dictionary<Guid, AuditedTool> Tools { get { LoadColdProperties(); return field; } set { LoadColdProperties(); field = value; } } = [];
}

public sealed record AiCoachAssessmentClaim(Guid RequestId, Guid AttemptId, Guid ClaimId,
    int ExpectedRevision, int TextRevision, DateTimeOffset ExpiresAt);

public sealed record TrainingOperation(string Kind, Guid AttemptId, string Fingerprint);
public sealed record HubAccessTicket(string KeyFingerprint, long Epoch, DateTimeOffset ExpiresAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] CoreActor? Actor = null);

// Callbacks are synchronous, pure state changes; no network I/O while holding a lock.
public interface IRuntimeReader
{
    Task<IReadOnlyList<ReviewEvent>> ReadLearningHistoryAsync(Guid id, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<ReviewEvent>>(s => s.Reviews.Values.Where(r => r.Event.LearningItemId == id)
            .Select(r => r.Event).OrderByDescending(r => r.ReviewedAt).ToArray(), ct);
    Task<LearningItem?> ReadLearningItemAsync(Guid id, CancellationToken ct = default) => ReadAsync(s => s.Learning.GetValueOrDefault(id), ct);
    Task<IReadOnlyList<LearningItem>> ReadLearningItemsAsync(bool dueOnly, DateTimeOffset cutoff, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<LearningItem>>(s => s.Learning.Values.Where(i => !dueOnly || i.DueAt <= cutoff).OrderBy(i => i.DueAt).ToArray(), ct);
    Task<LearningDuePage> ReadDueLearningPageAsync(DateTimeOffset cutoff, int size = 50, LearningDueCursor? after = null, CancellationToken ct = default) =>
        ReadAsync(s => LearningReadProjection.Page(s, cutoff, size, after, 0), ct);
    Task<AudioLease?> ReadAudioLeaseAsync(CancellationToken ct = default) => ReadAsync(s => s.AudioLease, ct);
    Task<AudioLeaseState> ReadAudioLeaseStateAsync(CancellationToken ct = default) =>
        ReadAsync(s => new AudioLeaseState(s.AudioLease, s.AudioDrainUntil), ct);
    Task<RegisteredDevice?> ReadRegisteredDeviceAsync(string id, CancellationToken ct = default) =>
        ReadAsync(s => s.Devices.GetValueOrDefault(id), ct);
    Task<bool> ReadPaidAiPausedAsync(CancellationToken ct = default) => ReadAsync(s => s.AgentControls.EffectivePaidAiPaused, ct);
    Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default);
}

public readonly record struct AudioLeaseState(AudioLease? Lease, DateTimeOffset? DrainUntil)
{
    public bool IsAvailable(DateTimeOffset now) =>
        (Lease is null || Lease.ExpiresAt <= now) && (DrainUntil is null || DrainUntil <= now);
}

public interface IRuntimeStore : IRuntimeReader
{
    // Runtime state and generated practice question rows changed in one canonical transaction (shared privacy/root lock).
    // No permissive default: a store that cannot provide the shared transaction refuses.
    Task<T> UpdateWithGeneratedQuestionsAsync<T>(Jarvis.Orchestrator.Training.GeneratedQuestionScope scope,
        Func<RuntimeState, Jarvis.Orchestrator.Training.GeneratedQuestionUnit, T> update, CancellationToken ct = default) =>
        throw new DomainException(503, "Generated practice storage is unavailable in this store.");
    Task<bool> DeleteTrainingAttemptAsync(Guid id, int expectedRevision, TimeProvider clock, CancellationToken ct = default)
        => UpdateAsync(s => Jarvis.Orchestrator.Training.TrainingDeletion.Apply(s, id, expectedRevision, clock), ct);
    Task<TrainingAttempt> CorrectTrainingAnswerAsync(Guid id, CorrectTrainingAnswerRequest request, TimeProvider clock, CancellationToken ct = default)
    {
        var normalized = Jarvis.Orchestrator.Training.TrainingCorrection.Normalize(request);
        return UpdateAsync(s => Jarvis.Orchestrator.Training.TrainingCorrection.Apply(s, id, normalized, clock), ct);
    }
    Task<LearningItem> CreateLearningItemAsync(CreateLearningRequest request, TimeProvider clock, CancellationToken ct = default)
    {
        var normalized = LearningService.NormalizeCreate(request);
        var epoch = LearningService.CreationEpoch(normalized);
        return UpdateAsync(s => LearningService.ApplyCreate(s, normalized, clock, epoch), ct);
    }
    Task<LearningItem> ReviewLearningItemAsync(Guid id, ReviewLearningRequest request, TimeProvider clock, CancellationToken ct = default)
    {
        LearningService.ValidateReview(request);
        return UpdateAsync(s => LearningService.Apply(s, id, request, clock.GetUtcNow()), ct);
    }
    Task<T> UpdateAsync<T>(Func<RuntimeState, T> update, CancellationToken ct = default);
    Task<UsageReservation> SettleUsageAsync(Guid id, decimal actual, int input, int output, CancellationToken ct = default,
        DateTimeOffset? completedAt = null) =>
        UpdateAsync(s => Jarvis.Orchestrator.Workflows.UsageAccounting.Settle(s, id, actual, input, output, completedAt), ct);
}

public sealed class InMemoryRuntimeStore(AudioFenceChanges? audioChanges = null) : IRuntimeStore
{
    private readonly object _gate = new();
    private RuntimeState _state = new();
    public void ReplacePrivacyData(RuntimeState state)
    {
        lock (_gate)
        {
            var previousLease = _state.AudioLease;
            _state = Copy(state);
            audioChanges?.LeaseChanged(previousLease, _state.AudioLease);
        }
    }
    private static RuntimeState Copy(RuntimeState state) =>
        JsonSerializer.Deserialize<RuntimeState>(JsonSerializer.Serialize(state))!;

    public Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(read(Copy(_state)));
    }

    public Task<bool> ReadPaidAiPausedAsync(CancellationToken ct = default) => ReadAsync(s => s.AgentControls.EffectivePaidAiPaused, ct);

    public Task<T> UpdateAsync<T>(Func<RuntimeState, T> update, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var previousLease = _state.AudioLease;
            var copy = Copy(_state);
            var result = update(copy);
            _state = Copy(copy);
            audioChanges?.LeaseChanged(previousLease, _state.AudioLease);
            return Task.FromResult(result);
        }
    }

    // Generated rows live under the same lock as runtime state and are copied with it, so a failed callback changes neither.
    private Dictionary<Guid, Jarvis.Orchestrator.Training.GeneratedQuestionRecord> _generated = [];

    public Task<T> UpdateWithGeneratedQuestionsAsync<T>(Jarvis.Orchestrator.Training.GeneratedQuestionScope scope,
        Func<RuntimeState, Jarvis.Orchestrator.Training.GeneratedQuestionUnit, T> update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(update);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var questionIds = scope.QuestionIds.ToHashSet(); var operationIds = scope.OperationIds.ToHashSet();
            var purgeSessions = (scope.PurgeSessions ?? []).ToHashSet();
            var loaded = _generated.Values.Where(r => questionIds.Contains(r.Question.Id) || operationIds.Contains(r.OperationId) || purgeSessions.Contains(r.Question.SessionId))
                .Select(Jarvis.Orchestrator.Training.GeneratedQuestionRecord.Detached).ToArray();
            var created = _generated.Values.Where(r => r.Question.SessionId == scope.SessionId).Select(r => r.Question.CreatedAt).ToArray();
            var unit = new Jarvis.Orchestrator.Training.GeneratedQuestionUnit(scope, loaded, created, _generated.Count);
            var previousLease = _state.AudioLease;
            var copy = Copy(_state);
            var result = update(copy, unit);
            var rows = new Dictionary<Guid, Jarvis.Orchestrator.Training.GeneratedQuestionRecord>(_generated);
            foreach (var (id, write) in unit.Adds)
                rows[id] = new(new(id, write.SessionId, write.Topic, write.OutlineKey, write.OutlineVersion, 1, write.Prompt, write.ReferenceOutline,
                    Array.AsReadOnly(write.FollowUps.ToArray()), write.CreatedAt, null), write.OperationId, write.Fingerprint, write.ProvenanceJson, write.LineageJson);

            foreach (var (id, at) in unit.Purges)
                if (rows.TryGetValue(id, out var row) && row.Question.PurgedAt is null)
                    rows[id] = row with { Fingerprint = "", LineageJson = "[]", Question = row.Question with { Prompt = null, ReferenceOutline = null, FollowUps = [], PurgedAt = at } };
            _state = Copy(copy); _generated = rows;
            audioChanges?.LeaseChanged(previousLease, _state.AudioLease);
            return Task.FromResult(result);
        }
    }

    // Retention: unpurged generated questions in conversations being retired, then their content-free tombstones.
    public IReadOnlySet<Guid> GeneratedQuestionsIn(IReadOnlySet<Guid> sessions)
    {
        lock (_gate) return _generated.Values.Where(r => sessions.Contains(r.Question.SessionId) && r.Question.PurgedAt is null).Select(r => r.Question.Id).ToHashSet();
    }
    public void TombstoneGeneratedQuestions(IReadOnlySet<Guid> ids, DateTimeOffset at)
    {
        lock (_gate)
            foreach (var id in ids)
                if (_generated.TryGetValue(id, out var row) && row.Question.PurgedAt is null)
                    _generated[id] = row with { Fingerprint = "", LineageJson = "[]", Question = row.Question with { Prompt = null, ReferenceOutline = null, FollowUps = [], PurgedAt = at } };
    }

    // Whole-Core erasure only (not composite commits or retention): deletes every generated row; the store itself remains.
    public void EraseGeneratedQuestions()
    {
        lock (_gate) _generated = [];
    }

    // Read-only snapshot for the in-memory resolver/reader; never exposes the internal dictionary.
    public IReadOnlyList<Jarvis.Orchestrator.Training.GeneratedQuestionRecord> GeneratedQuestionsSnapshot()
    {
        lock (_gate) return _generated.Values.Select(Jarvis.Orchestrator.Training.GeneratedQuestionRecord.Detached).ToArray();
    }
}

public sealed class DomainException(int statusCode, string message, string? code = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
    public TimeSpan? RetryAfter { get; init; }
}

public static class Guard
{
    public static string Text(string? value, string name, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max)
            throw new DomainException(400, $"{name} must contain 1–{max} characters.");
        return value.Trim();
    }
    public static void Require([DoesNotReturnIf(false)] bool condition, int status, string message, string? code = null)
    {
        if (!condition) throw new DomainException(status, message, code);
    }
}
