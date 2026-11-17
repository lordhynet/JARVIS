using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Sessions;

/// <summary>
/// Persistence boundary for sessions. Milestone 1 uses <see cref="InMemorySessionStore"/>;
/// Milestone 2 swaps in PostgreSQL behind the same interface.
/// </summary>
public interface ISessionStore : ISessionReader
{
    Task<ConversationSession> AddAsync(ConversationSession session, CancellationToken ct = default);
    Task<ConversationSession?> UpdateAsync(Guid id,
        Func<ConversationSession, ConversationSession> update, CancellationToken ct = default);

    /// <summary>Appends a checkpoint and returns the updated session, or null if the session does not exist.</summary>
    Task<ConversationSession?> AppendCheckpointAsync(
        Guid sessionId,
        Func<ConversationSession, SessionCheckpoint> createCheckpoint,
        DateTimeOffset now,
        CancellationToken ct = default);
}
