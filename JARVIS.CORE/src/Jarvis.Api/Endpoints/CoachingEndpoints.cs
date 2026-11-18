using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Coaching;

namespace Jarvis.Api.Endpoints;

public static class CoachingEndpoints
{
    public static void MapCoachingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/modules", () => ProductModules.All).RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        var group = app.MapGroup("/api/coaching").WithTags("Coaching").RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        group.MapGet("/exercises", () => CoachingCatalog.Exercises);
        group.MapGet("/delivery", (Guid? sessionId, IQueryHandler<GetSpeechDelivery, IReadOnlyList<SpeechDeliverySession>> handler, CancellationToken ct) => handler.HandleAsync(new(sessionId), ct));
        group.MapGet("/ai-status", (Jarvis.Api.Integrations.ManagedAi ai) => ai.Status("Coach"));
        group.MapPost("/attempts/{id:guid}/ai-assessments", (Guid id, AiAssessmentRequest request, ICommandHandler<RequestCoachingAiAssessment, CoachingAttemptView> handler, CancellationToken ct) => handler.HandleAsync(new(id, request), ct)).RequireRateLimiting("cloud");
        app.MapPost("/api/training/attempts/{id:guid}/ai-assessments", (Guid id, AiAssessmentRequest request, ICommandHandler<RequestTechnicalAiAssessment, TrainingAttempt> handler, CancellationToken ct) => handler.HandleAsync(new(id, request), ct)).RequireRateLimiting("cloud").RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        group.MapGet("/prompt", (string exerciseId, Guid? storyId, int? storyVersion, int? claimIndex, IQueryHandler<GetCoachingPrompt, CoachingPrompt> h, CancellationToken ct) => h.HandleAsync(new(exerciseId, storyId, storyVersion, claimIndex), ct));
        group.MapPost("/attempts", (SaveCoachingAnswerRequest request, ICommandHandler<SaveCoaching, CoachingAttemptView> h, CancellationToken ct) => h.HandleAsync(new(request), ct));
        group.MapGet("/attempts/{id:guid}", (Guid id, IQueryHandler<GetCoaching, CoachingAttemptView> h, CancellationToken ct) => h.HandleAsync(new(id), ct));
        group.MapPatch("/attempts/{id:guid}", (Guid id, CorrectCoachingAnswerRequest request, ICommandHandler<CorrectCoaching, CoachingAttemptView> h, CancellationToken ct) => h.HandleAsync(new(id, request), ct));
        group.MapDelete("/attempts/{id:guid}", (Guid id, int expectedRevision, ICommandHandler<DeleteCoaching, bool> h, CancellationToken ct) => h.HandleAsync(new(id, expectedRevision), ct));
        group.MapGet("/sessions/{id:guid}/attempts", (Guid id, IQueryHandler<ListCoaching, IReadOnlyList<CoachingAttemptSummary>> h, CancellationToken ct) => h.HandleAsync(new(id), ct));
        app.MapGet("/api/sessions/{id:guid}/review", (Guid id, IQueryHandler<GetSessionReview, SessionReview> h, CancellationToken ct) => h.HandleAsync(new(id), ct)).RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        app.MapGet("/api/programme", (IQueryHandler<GetProgramme, ProgrammeView> h, CancellationToken ct) => h.HandleAsync(new(), ct)).RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        app.MapPost("/api/programme", (StartProgrammeRequest request, ICommandHandler<StartProgramme, ImprovementProgramme> h, CancellationToken ct) => h.HandleAsync(new(request), ct)).RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        app.MapPost("/api/programme/{id:guid}/check-ins", (Guid id, ProgrammeCheckInRequest request, ICommandHandler<Jarvis.Orchestrator.Cqrs.Coaching.ProgrammeCheckIn, ImprovementProgramme> h, CancellationToken ct) => h.HandleAsync(new(id, request), ct)).RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        app.MapDelete("/api/programme/{id:guid}", (Guid id, int expectedRevision, ICommandHandler<DeleteProgramme, bool> h, CancellationToken ct) => h.HandleAsync(new(id, expectedRevision), ct)).RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
    }
}
