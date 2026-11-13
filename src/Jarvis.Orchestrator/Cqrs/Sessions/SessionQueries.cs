using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Orchestrator.Cqrs.Sessions;

public sealed record GetSession(Guid SessionId) : IQuery<ConversationSession?>;
public sealed record ListSessions : IQuery<IReadOnlyList<ConversationSession>>;

public sealed class SessionQueryHandlers(ISessionReader reader) :
    IQueryHandler<GetSession, ConversationSession?>,
    IQueryHandler<ListSessions, IReadOnlyList<ConversationSession>>
{
    public Task<ConversationSession?> HandleAsync(GetSession query, CancellationToken ct = default) => reader.GetAsync(query.SessionId, ct);
    public Task<IReadOnlyList<ConversationSession>> HandleAsync(ListSessions query, CancellationToken ct = default) => reader.ListAsync(ct);
}
