using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Runtime;

public sealed class RuntimeState
{
    public Dictionary<string, RegisteredDevice> Devices { get; set; } = [];
    public Dictionary<Guid, LearningItem> Learning { get; set; } = [];
    public Dictionary<Guid, LearningReview> Reviews { get; set; } = [];
    public Dictionary<Guid, AuditedTool> Tools { get; set; } = [];
}

// Callbacks are synchronous, pure state changes; no network I/O while holding a lock.
public interface IRuntimeReader
{
    Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default);
}

public interface IRuntimeStore : IRuntimeReader
{
    Task<T> UpdateAsync<T>(Func<RuntimeState, T> update, CancellationToken ct = default);
}

public sealed class InMemoryRuntimeStore : IRuntimeStore
{
    private readonly object _gate = new();
    private RuntimeState _state = new();
    private static RuntimeState Copy(RuntimeState state) =>
        JsonSerializer.Deserialize<RuntimeState>(JsonSerializer.Serialize(state))!;

    public Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(read(Copy(_state)));
    }

    public Task<T> UpdateAsync<T>(Func<RuntimeState, T> update, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var copy = Copy(_state);
            var result = update(copy);
            _state = Copy(copy);
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
