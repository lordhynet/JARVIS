using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Cqrs.Integrations;

public interface IIntegrationReader
{
    IReadOnlyList<IntegrationStatus> Status();
    Task<IReadOnlyList<TransitLine>> TransitAsync(string? lines, CancellationToken ct);
    Task<IReadOnlyList<CalendarEntry>> CalendarAsync(string provider, DateTimeOffset start, DateTimeOffset end, CancellationToken ct);
}

public interface ITranscriptionProvider
{
    Task<string> TranscribeAsync(Stream audio, string filename, CancellationToken ct);
}

public sealed record StorageReadiness(bool Ready, string Storage);
public interface IStorageReadiness
{
    Task<StorageReadiness> CheckAsync(CancellationToken ct);
}

public sealed class InMemoryReadiness : IStorageReadiness
{
    public Task<StorageReadiness> CheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new StorageReadiness(true, "InMemory"));
    }
}
