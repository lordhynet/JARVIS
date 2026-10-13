using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Api.Endpoints;

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions").WithTags("Sessions");

        group.MapGet("/", async (SessionService sessions, CancellationToken ct) =>
            Results.Ok(await sessions.ListAsync(ct)));

        group.MapPost("/", async (CreateSessionRequest request, SessionService sessions, CancellationToken ct) =>
        {
            try
            {
                var session = await sessions.CreateAsync(request, ct);
                return Results.Created($"/api/sessions/{session.Id}", session);
            }
            catch (SessionValidationException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] });
            }
        });

        group.MapGet("/{id:guid}", async (Guid id, SessionService sessions, CancellationToken ct) =>
            await sessions.GetAsync(id, ct) is { } session ? Results.Ok(session) : Results.NotFound());

        group.MapPost("/{id:guid}/checkpoints", async (Guid id, AddCheckpointRequest request, SessionService sessions, CancellationToken ct) =>
        {
            try
            {
                var updated = await sessions.CheckpointAsync(id, request, ct);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (SessionValidationException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] });
            }
        });

        return app;
    }
}
