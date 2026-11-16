using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Runtime;

public sealed class RuntimeState
{
    // In-memory registry only. PostgreSQL keeps definitions in the dedicated relational tables.
    public Jarvis.Orchestrator.Domains.DomainRegistryState DomainRegistry { get; set; } = new();
    public Dictionary<Guid, ProviderAttempt> ProviderAttempts { get; set; } = [];
    public HashSet<Guid> RetiredReasoningScopes { get; set; } = [];
    public Dictionary<string, HubAccessTicket> HubTickets { get; set; } = [];
    public Dictionary<string, DeviceCredential> DeviceCredentials { get; set; } = [];
    public Dictionary<Guid, VoiceCoaching> VoiceCoachings { get; set; } = [];
    public string? BudgetScopeId { get; set; }
    public long BudgetEpoch { get; set; }
    public int BudgetRevision { get; set; }
    public Dictionary<Guid, GbpCommitment> GbpCommitments { get; set; } = [];
    public Dictionary<Guid, BudgetApproval> BudgetApprovals { get; set; } = [];
    public Dictionary<Guid, BudgetManagementReceipt> BudgetReceipts { get; set; } = [];
    public Dictionary<Guid, VoiceRecall> VoiceRecalls { get; set; } = [];
    public Dictionary<Guid, BrowserPushSubscription> PushSubscriptions { get; set; } = [];
    public Dictionary<Guid, PushDelivery> PushDeliveries { get; set; } = [];
    public Dictionary<Guid, ImportedDocument> ImportedDocuments { get; set; } = [];
    public Dictionary<Guid, SourceClaim> SourceClaims { get; set; } = [];
    public Dictionary<Guid, PersonalGoal> Goals { get; set; } = [];
    public Dictionary<Guid, DurableAgentTrigger> AgentTriggers { get; set; } = [];
    public Dictionary<Guid, UsageReconciliation> UsageReconciliations { get; set; } = [];
    public HashSet<Guid> RetiredSessions { get; set; } = [];
    public Dictionary<Guid, TrainingDrill> TrainingDrills { get; set; } = [];
    public Dictionary<Guid, UsageReservation> UsageReservations { get; set; } = [];
    public Dictionary<Guid, ProviderResult> ProviderResults { get; set; } = [];
    public AgentControls AgentControls { get; set; } = new();
    public Dictionary<Guid, AgentSchedule> AgentSchedules { get; set; } = [];
    public Dictionary<Guid, AgentRun> AgentRuns { get; set; } = [];
    public Dictionary<Guid, AgentTask> AgentTasks { get; set; } = [];
    public Dictionary<Guid, AgentNotification> AgentOutbox { get; set; } = [];
    public Dictionary<Guid, AiCoachAssessment> AiCoachAssessments { get; set; } = [];
    public Dictionary<Guid, AiCoachAssessmentClaim> AiCoachAssessmentClaims { get; set; } = [];
    public AudioLease? AudioLease { get; set; }
    public AudioLease? AudioDrainLease { get; set; }
    public DateTimeOffset? AudioDrainUntil { get; set; }
    public long NextAudioFence { get; set; } = 1;
    public Dictionary<string, VoiceTicketRecord> VoiceTickets { get; set; } = [];
    public Dictionary<string, List<DateTimeOffset>> WakeCandidateAttempts { get; set; } = [];
    public Dictionary<Guid, VoiceTurn> VoiceTurns { get; set; } = [];
    public Dictionary<Guid, CoachingAttempt> CoachingAttempts { get; set; } = [];
    public Dictionary<Guid, TrainingOperation> CoachingOperations { get; set; } = [];
    public Dictionary<Guid, ImprovementProgramme> Programmes { get; set; } = [];
    public Guid? ActiveProgrammeId { get; set; }
    public Dictionary<Guid, TrainingAttempt> TrainingAttempts { get; set; } = [];
    public Dictionary<Guid, TrainingOperation> TrainingOperations { get; set; } = [];
    public Dictionary<Guid, LearningItem> TrainingBaselines { get; set; } = [];
    public Dictionary<Guid, HashSet<Guid>> TrainingBaselineReviews { get; set; } = [];
    public Dictionary<Guid, long> TrainingManualSequences { get; set; } = [];
    public long NextTrainingSequence { get; set; } = 1;
    public Dictionary<Guid, Story> Stories { get; set; } = [];
    public Dictionary<string, RegisteredDevice> Devices { get; set; } = [];
    public Dictionary<Guid, LearningItem> Learning { get; set; } = [];
    public Dictionary<Guid, LearningReview> Reviews { get; set; } = [];
    public Dictionary<Guid, AuditedTool> Tools { get; set; } = [];
}

public sealed record AiCoachAssessmentClaim(Guid RequestId, Guid AttemptId, Guid ClaimId,
    int ExpectedRevision, int TextRevision, DateTimeOffset ExpiresAt);

public sealed record TrainingOperation(string Kind, Guid AttemptId, string Fingerprint);
public sealed record HubAccessTicket(string KeyFingerprint, long Epoch, DateTimeOffset ExpiresAt);

// Callbacks are synchronous, pure state changes; no network I/O while holding a lock.
public interface IRuntimeReader
{
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
    Task<T> UpdateAsync<T>(Func<RuntimeState, T> update, CancellationToken ct = default);
    Task<bool> ReleaseAudioAsync(AudioLease lease, CancellationToken ct = default) => UpdateAsync(state =>
    {
        var before = RuntimeAudioLeaseState.From(state);
        var after = before.Release(lease);
        after.Apply(state);
        return before != after;
    }, ct);
    Task<UsageReservation> SettleUsageAsync(Guid id, decimal actual, int input, int output, CancellationToken ct = default) =>
        UpdateAsync(s => Jarvis.Orchestrator.Workflows.UsageAccounting.Settle(s, id, actual, input, output), ct);
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
}

public sealed class DomainException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class Guard
{
    public static string Text(string? value, string name, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max)
            throw new DomainException(400, $"{name} must contain 1–{max} characters.");
        return value.Trim();
    }
    public static void Require([DoesNotReturnIf(false)] bool condition, int status, string message)
    {
        if (!condition) throw new DomainException(status, message);
    }
}
