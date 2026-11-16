using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Integrations;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Endpoints;

public static class IntegrationEndpoints
{
    public static void MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        api.MapGet("/integrations", (IQueryHandler<ListIntegrations, IReadOnlyList<IntegrationStatus>> handler, CancellationToken ct) =>
            handler.HandleAsync(new ListIntegrations(), ct));
        api.MapGet("/transit", (string? lines, IQueryHandler<GetTransit, IReadOnlyList<TransitLine>> handler, CancellationToken ct) =>
            handler.HandleAsync(new GetTransit(lines), ct));
        api.MapGet("/calendar/{provider}", (string provider, DateTimeOffset start, DateTimeOffset end,
            IQueryHandler<GetCalendar, IReadOnlyList<CalendarEntry>> handler, CancellationToken ct) =>
            handler.HandleAsync(new GetCalendar(provider, start, end), ct));
        api.MapGet("/briefing", (IQueryHandler<GetDailyBriefing, DailyBriefing> handler, CancellationToken ct) =>
            handler.HandleAsync(new GetDailyBriefing(), ct));
        api.MapPost("/voice/transcribe", async (HttpRequest request, ICommandHandler<TranscribeAudio, string> handler, CancellationToken ct) =>
        {
            Guard.Require(request.HasFormContentType, 400, "Upload audio using multipart/form-data.");
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("audio");
            Guard.Require(file is not null, 400, "Audio must be between 1 byte and 10 MB.");
            await using var stream = file.OpenReadStream();
            return Results.Ok(new { text = await handler.HandleAsync(new TranscribeAudio(stream, file.FileName, file.Length), ct) });
        }).RequireRateLimiting("cloud");
        app.MapGet("/health/ready", async (IQueryHandler<GetStorageReadiness, StorageReadiness> handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(new GetStorageReadiness(), ct);
            return result.Ready ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
        }).AllowAnonymous();
    }
}
