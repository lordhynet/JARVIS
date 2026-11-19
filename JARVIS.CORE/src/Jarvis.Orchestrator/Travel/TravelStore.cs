using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Travel;

public sealed class TravelState
{
    public Dictionary<Guid, TravelRoute> Routes { get; set; } = [];
    public Dictionary<string, TravelLineState> Lines { get; set; } = [];
    public TravelMonitorState Monitor { get; set; } = new();
}
public interface ITravelStore
{
    Task<T> ReadAsync<T>(Func<TravelState, T> read, CancellationToken ct = default);
    Task<T> UpdateAsync<T>(Func<TravelState, RuntimeState, T> update, CancellationToken ct = default);
}
public interface ITravelSource
{
    Task<IReadOnlyList<TravelLine>> CatalogueAsync(CancellationToken ct);
    Task<IReadOnlyList<TravelStop>> StopsAsync(IReadOnlyList<string> ids, CancellationToken ct);
    Task<IReadOnlyList<TravelStatus>> StatusAsync(IReadOnlyList<TravelLine> lines, CancellationToken ct);
    Task<IReadOnlyList<TravelArrival>> ArrivalsAsync(TravelStop stop, CancellationToken ct);
}
public interface ITravelDeliveryPolicy { bool NotifyWhenAgentsPaused { get; } }
public sealed class MemoryTravelStore(InMemoryRuntimeStore runtime, MemoryPrivacyBoundary boundary) : ITravelStore
{
    private static TravelState Copy(TravelState state) => JsonSerializer.Deserialize<TravelState>(JsonSerializer.Serialize(state))!;
    public Task<T> ReadAsync<T>(Func<TravelState, T> read, CancellationToken ct = default) => boundary.Run(() => {
        ct.ThrowIfCancellationRequested(); return Task.FromResult(read(Copy(boundary.Travel)));
    });
    public Task<T> UpdateAsync<T>(Func<TravelState, RuntimeState, T> update, CancellationToken ct = default) => boundary.Run(async () => {
        var travel = Copy(boundary.Travel); TravelState? saved = null;
        var result = await runtime.UpdateAsync(state => { var result = update(travel, state); saved = Copy(travel); return result; }, ct);
        boundary.Travel = saved!; return result;
    });
}
