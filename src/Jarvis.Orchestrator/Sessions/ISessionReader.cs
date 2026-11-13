using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Sessions;

/// <summary>Read-side port. Query handlers cannot add or update sessions through this interface.</summary>
public interface ISessionReader
{
    Task<ConversationSession?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationSession>> ListAsync(CancellationToken ct = default);
}
