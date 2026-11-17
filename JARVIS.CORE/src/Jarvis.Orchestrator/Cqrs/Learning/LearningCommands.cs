using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Learning;

public sealed record CreateLearningItem(CreateLearningRequest Request) : ICommand<LearningItem>;
public sealed record ReviewLearningItem(Guid ItemId, ReviewLearningRequest Request) : ICommand<LearningItem>;

public sealed class LearningCommandHandlers(LearningService learning) :
    ICommandHandler<CreateLearningItem, LearningItem>, ICommandHandler<ReviewLearningItem, LearningItem>
{
    public Task<LearningItem> HandleAsync(CreateLearningItem command, CancellationToken ct = default) => learning.CreateAsync(command.Request, ct);
    public Task<LearningItem> HandleAsync(ReviewLearningItem command, CancellationToken ct = default) => learning.ReviewAsync(command.ItemId, command.Request, ct);
}
