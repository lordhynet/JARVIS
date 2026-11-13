using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Orchestrator.Runtime;

public interface IConversationProvider
{
    Task<string> ReplyAsync(ConversationSession session, string text, CancellationToken ct);
}

public sealed class ConversationService(ISessionStore sessions, IConversationProvider provider, TimeProvider clock)
{
    public async Task<ConversationSession> SendAsync(Guid id, ChatRequest request, CancellationToken ct)
    {
        Guard.Require(request.RequestId != Guid.Empty, 400, "RequestId is required.");
        var text = Guard.Text(request.Text, "Text", 8000);
        var session = await sessions.GetAsync(id, ct);
        Guard.Require(session is not null, 404, "Session not found.");
        Guard.Require(session!.Status == SessionStatus.Active, 409, "Resume the session before sending messages.");
        if (Existing(session, request.RequestId, text)) return session;
        var previousCount = session.Messages?.Count ?? 0;
        Guard.Require(previousCount < 2000, 409, "Session transcript is full. Checkpoint and start a new session.");
        var reply = await provider.ReplyAsync(session, text, ct);
        var updated = await sessions.UpdateAsync(id, current =>
        {
            if (Existing(current, request.RequestId, text)) return current;
            Guard.Require(current.Status == SessionStatus.Active, 409, "Session stopped while the model was responding.");
            Guard.Require((current.Messages?.Count ?? 0) == previousCount, 409, "Another message arrived. Send again with a new RequestId.");
            var now = clock.GetUtcNow();
            return current with { UpdatedAt = now, Messages = [.. current.Messages ?? [],
                new(request.RequestId, "user", text, now), new(request.RequestId, "assistant", reply, now)] };
        }, ct);
        return updated!;
    }

    private static bool Existing(ConversationSession session, Guid id, string text)
    {
        var message = session.Messages?.FirstOrDefault(m => m.RequestId == id && m.Role == "user");
        if (message is null) return false;
        Guard.Require(message.Text == text, 409, "RequestId was used for a different message.");
        return true;
    }
}
