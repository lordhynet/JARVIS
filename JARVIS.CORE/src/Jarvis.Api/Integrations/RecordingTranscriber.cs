using Jarvis.Orchestrator.Recordings;

namespace Jarvis.Api.Integrations;

// The recorder orchestrator owns quote confirmation, the existing UsageBudget reservation
// and dispatch/reconciliation fencing. This adapter reuses the configured provider in memory.
public sealed class RecordingTranscriber(ManagedAi ai, OpenAiProvider provider) : IRecordingTranscriber
{
    public RecordingTranscriptionPrice Pricing() { var rate = ai.TranscriptionRates(); return new(rate.Model, rate.PerMinute, ai.Limits); }
    public Task<string> TranscribeAsync(byte[] canonicalWave, RecordingTranscriptionPrice price, CancellationToken ct) => provider.TranscribeRecordingPieceAsync(canonicalWave, price, ct);
}
