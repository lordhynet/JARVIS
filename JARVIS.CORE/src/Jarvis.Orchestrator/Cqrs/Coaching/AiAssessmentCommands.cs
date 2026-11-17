using Jarvis.Contracts;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Orchestrator.Cqrs.Coaching;

/// <summary>Application port implemented by the API's budgeted provider adapter.</summary>
public interface IAiAssessmentService
{
    Task<CoachingAttemptView> CoachingAsync(Guid id, AiAssessmentRequest request, CancellationToken ct);
    Task<TrainingAttempt> TechnicalAsync(Guid id, AiAssessmentRequest request, CancellationToken ct);
    Task<AiCoachAssessment> ImportedAsync(Guid id, AiAssessmentRequest request, CancellationToken ct);
}

public sealed record RequestCoachingAiAssessment(Guid Id, AiAssessmentRequest Request) : ICommand<CoachingAttemptView>;
public sealed record RequestTechnicalAiAssessment(Guid Id, AiAssessmentRequest Request) : ICommand<TrainingAttempt>;
public sealed record RequestImportAiAssessment(Guid Id, AiAssessmentRequest Request) : ICommand<AiCoachAssessment>;

public sealed class AiAssessmentCommandHandlers(IAiAssessmentService assessments) :
    ICommandHandler<RequestCoachingAiAssessment, CoachingAttemptView>,
    ICommandHandler<RequestTechnicalAiAssessment, TrainingAttempt>,
    ICommandHandler<RequestImportAiAssessment, AiCoachAssessment>
{
    public Task<CoachingAttemptView> HandleAsync(RequestCoachingAiAssessment command, CancellationToken ct = default) => assessments.CoachingAsync(command.Id, command.Request, ct);
    public Task<TrainingAttempt> HandleAsync(RequestTechnicalAiAssessment command, CancellationToken ct = default) => assessments.TechnicalAsync(command.Id, command.Request, ct);
    public Task<AiCoachAssessment> HandleAsync(RequestImportAiAssessment command, CancellationToken ct = default) => assessments.ImportedAsync(command.Id, command.Request, ct);
}
