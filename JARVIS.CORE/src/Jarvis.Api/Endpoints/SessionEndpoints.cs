using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Sessions;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Api.Endpoints;

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions").WithTags("Sessions").RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        group.MapGet("/", async (IQueryHandler<ListSessions, IReadOnlyList<ConversationSession>> handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(new ListSessions(), ct)));
        group.MapGet("/{id:guid}", async (Guid id, IQueryHandler<GetSession, ConversationSession?> handler, CancellationToken ct) =>
            await handler.HandleAsync(new GetSession(id), ct) is { } session ? Results.Ok(session) : Results.NotFound());
        group.MapPost("/", async (CreateSessionRequest request, ICommandHandler<CreateSession, ConversationSession> handler, CancellationToken ct) =>
        {
            try
            {
                var session = await handler.HandleAsync(new CreateSession(request), ct);
                return Results.Created($"/api/sessions/{session.Id}", session);
            }
            catch (SessionValidationException ex) { return Validation(ex); }
        });
        group.MapPost("/{id:guid}/checkpoints", async (Guid id, AddCheckpointRequest request,
            ICommandHandler<AddCheckpoint, ConversationSession?> handler, CancellationToken ct) =>
        {
            try
            {
                var session = await handler.HandleAsync(new AddCheckpoint(id, request), ct);
                return session is null ? Results.NotFound() : Results.Ok(session);
            }
            catch (SessionValidationException ex) { return Validation(ex); }
        });
        group.MapPost("/{id:guid}/status", (Guid id, SessionTransitionRequest request,
            ICommandHandler<TransitionSession, ConversationSession> handler, CancellationToken ct) =>
            handler.HandleAsync(new TransitionSession(id, request.Status), ct));
        group.MapPost("/{id:guid}/claim", (Guid id, ClaimRequest request,
            ICommandHandler<ClaimSession, ConversationSession> handler, CancellationToken ct) =>
            handler.HandleAsync(new ClaimSession(id, request.DeviceId), ct));
        group.MapPost("/{id:guid}/handoff", (Guid id, StartHandoffRequest request,
            ICommandHandler<StartHandoff, ConversationSession> handler, CancellationToken ct) =>
            handler.HandleAsync(new StartHandoff(id, request), ct));
        group.MapPost("/{id:guid}/handoff/{action}", (Guid id, string action, HandoffActionRequest request,
            ICommandHandler<AdvanceHandoff, ConversationSession> handler, CancellationToken ct) =>
            handler.HandleAsync(new AdvanceHandoff(id, action, request), ct));
        group.MapPost("/{id:guid}/messages", (Guid id, ChatRequest request,
            ICommandHandler<SendMessage, ConversationSession> handler, CancellationToken ct) =>
            handler.HandleAsync(new SendMessage(id, request), ct)).RequireRateLimiting("cloud");
        return app;
    }

    private static IResult Validation(SessionValidationException ex) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] });
    public sealed record ClaimRequest(string DeviceId);
}
