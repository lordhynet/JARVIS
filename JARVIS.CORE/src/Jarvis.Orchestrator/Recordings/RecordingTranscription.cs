using Jarvis.Contracts;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Orchestrator.Recordings;

public sealed record RecordingTranscriptionPrice(string Model, decimal PerMinute, BudgetLimits Limits);
public interface IRecordingTranscriber
{
    RecordingTranscriptionPrice Pricing();
    Task<string> TranscribeAsync(byte[] canonicalWave, RecordingTranscriptionPrice price, CancellationToken ct);
}
public sealed record RecordingTranscriptPiece(Guid Id, string Track, int Sequence, string Sha256, int Samples,
    decimal MaximumUsd, decimal MaximumGbp, string Status = RecordingTranscriptStates.Pending,
    Guid? ReservationId = null, DateTimeOffset? StartedAt = null, Guid? OperationId = null);
public sealed record RecordingTranscriptJob(Guid Id, string Model, decimal PerMinute, decimal MaximumUsd, decimal MaximumGbp,
    DateTimeOffset ExpiresAt, IReadOnlyList<RecordingTranscriptPiece> Pieces,
    string Status = RecordingTranscriptStates.Quoted, int Revision = 1, DateTimeOffset? ConfirmedAt = null,
    string? ErrorCode = null, IReadOnlyDictionary<Guid, Guid>? Operations = null);
