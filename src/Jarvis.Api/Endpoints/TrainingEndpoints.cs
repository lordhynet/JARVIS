using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Training;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Api.Endpoints;

public static class TrainingEndpoints
{
    public static void MapTrainingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/training").WithTags("Training");
        group.MapGet("/questions", () => QuestionBank.All);
        group.MapGet("/modes", () => DrillCatalog.Modes);
        group.MapPost("/drills", (StartTrainingDrillRequest r, ICommandHandler<StartTrainingDrill, TrainingDrillView> h, CancellationToken ct) => h.HandleAsync(new(r), ct));
        group.MapGet("/drills/{id:guid}", (Guid id, IQueryHandler<GetTrainingDrill, TrainingDrillView> h, CancellationToken ct) => h.HandleAsync(new(id), ct));
        group.MapGet("/sessions/{id:guid}/drills", (Guid id, IQueryHandler<ListTrainingDrills, IReadOnlyList<TrainingDrillView>> h, CancellationToken ct) => h.HandleAsync(new(id), ct));
        group.MapPost("/drills/{id:guid}/cancel", (Guid id, CancelTrainingDrillRequest r, ICommandHandler<CancelTrainingDrill, TrainingDrillView> h, CancellationToken ct) => h.HandleAsync(new(id, r.ExpectedRevision), ct));
        group.MapGet("/learning", (IQueryHandler<ListTrainingCards, IReadOnlyList<LearningItem>> h, CancellationToken ct) => h.HandleAsync(new(), ct));
        group.MapGet("/dashboard", (string? timeZone, IQueryHandler<GetTrainingDashboard, TrainingDashboard> h, CancellationToken ct) => h.HandleAsync(new(timeZone ?? "Europe/London"), ct));
        group.MapGet("/sessions/{id:guid}/attempts", (Guid id, IQueryHandler<ListTraining, IReadOnlyList<TrainingAttemptSummary>> h, CancellationToken ct) => h.HandleAsync(new(id), ct));
        group.MapGet("/attempts/{id:guid}", (Guid id, IQueryHandler<GetTraining, TrainingAttempt> h, CancellationToken ct) => h.HandleAsync(new(id), ct));
        group.MapPost("/attempts", (SubmitTrainingAnswerRequest request, ICommandHandler<SubmitTraining, TrainingAttempt> h, CancellationToken ct) => h.HandleAsync(new(request), ct));
        group.MapPost("/attempts/{id:guid}/assessments", (Guid id, SelfAssessmentRequest request, ICommandHandler<AssessTraining, TrainingAttempt> h, CancellationToken ct) => h.HandleAsync(new(id, request), ct));
        group.MapPatch("/attempts/{id:guid}/answer", (Guid id, CorrectTrainingAnswerRequest request, ICommandHandler<CorrectTraining, TrainingAttempt> h, CancellationToken ct) => h.HandleAsync(new(id, request), ct));
        group.MapDelete("/attempts/{id:guid}", (Guid id, int expectedRevision, ICommandHandler<DeleteTraining, bool> h, CancellationToken ct) => h.HandleAsync(new(id, expectedRevision), ct));
    }
}
