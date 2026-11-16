using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Orchestrator.Cqrs.Sessions;

public sealed record CreateSession(CreateSessionRequest Request) : ICommand<ConversationSession>;
public sealed record AddCheckpoint(Guid SessionId, AddCheckpointRequest Request) : ICommand<ConversationSession?>;
public sealed record TransitionSession(Guid SessionId, SessionStatus Status) : ICommand<ConversationSession>;
public sealed record ClaimSession(Guid SessionId, string DeviceId) : ICommand<ConversationSession>;
public sealed record StartHandoff(Guid SessionId, StartHandoffRequest Request) : ICommand<ConversationSession>;
public sealed record AdvanceHandoff(Guid SessionId, string Action, HandoffActionRequest Request) : ICommand<ConversationSession>;
public sealed record SendMessage(Guid SessionId, ChatRequest Request) : ICommand<ConversationSession>;
public sealed record ExpireHandoffs : ICommand<CommandCompleted>;

// Handlers coordinate a use case; state machines, validation and transaction boundaries
// remain in the write-side services, which can share rules without duplicating them.
public sealed class SessionCommandHandlers(SessionService sessions, SessionLifecycleService lifecycle, ConversationService conversation) :
    ICommandHandler<CreateSession, ConversationSession>,
    ICommandHandler<AddCheckpoint, ConversationSession?>,
    ICommandHandler<TransitionSession, ConversationSession>,
    ICommandHandler<ClaimSession, ConversationSession>,
    ICommandHandler<StartHandoff, ConversationSession>,
    ICommandHandler<AdvanceHandoff, ConversationSession>,
    ICommandHandler<SendMessage, ConversationSession>,
    ICommandHandler<ExpireHandoffs, CommandCompleted>
{
    public Task<ConversationSession> HandleAsync(CreateSession command, CancellationToken ct = default) => sessions.CreateAsync(command.Request, ct);
    public Task<ConversationSession?> HandleAsync(AddCheckpoint command, CancellationToken ct = default) => sessions.CheckpointAsync(command.SessionId, command.Request, ct);
    public Task<ConversationSession> HandleAsync(TransitionSession command, CancellationToken ct = default) => lifecycle.TransitionAsync(command.SessionId, command.Status, ct);
    public Task<ConversationSession> HandleAsync(ClaimSession command, CancellationToken ct = default) => lifecycle.ClaimAsync(command.SessionId, command.DeviceId, ct);
    public Task<ConversationSession> HandleAsync(StartHandoff command, CancellationToken ct = default) => lifecycle.StartAsync(command.SessionId, command.Request, ct);
    public Task<ConversationSession> HandleAsync(AdvanceHandoff command, CancellationToken ct = default) => lifecycle.AdvanceAsync(command.SessionId, command.Action, command.Request, ct);
    public Task<ConversationSession> HandleAsync(SendMessage command, CancellationToken ct = default) => conversation.SendAsync(command.SessionId, command.Request, ct);
    public async Task<CommandCompleted> HandleAsync(ExpireHandoffs command, CancellationToken ct = default)
    {
        await lifecycle.ExpireAsync(ct);
        return new();
    }
}
