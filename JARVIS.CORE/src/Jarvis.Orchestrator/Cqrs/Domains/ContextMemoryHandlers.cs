using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;

namespace Jarvis.Orchestrator.Cqrs.Domains;

public sealed record SaveDomainMemory(string Key, SaveContextMemoryRequest Request) : ICommand<ContextMemoryItem>;
public sealed record DeleteDomainMemory(string Key, Guid Id) : ICommand<bool>;
public sealed record ListDomainMemory(string Key) : IQuery<IReadOnlyList<ContextMemoryItem>>;
public sealed class ContextMemoryCommands(ContextMemoryService memory) : ICommandHandler<SaveDomainMemory, ContextMemoryItem>, ICommandHandler<DeleteDomainMemory, bool>
{
    public Task<ContextMemoryItem> HandleAsync(SaveDomainMemory command, CancellationToken ct = default) => memory.SaveAsync(command.Key, command.Request, ct);
    public Task<bool> HandleAsync(DeleteDomainMemory command, CancellationToken ct = default) => memory.DeleteAsync(command.Key, command.Id, ct);
}
public sealed class ContextMemoryQueries(ContextMemoryService memory) : IQueryHandler<ListDomainMemory, IReadOnlyList<ContextMemoryItem>>
{
    public Task<IReadOnlyList<ContextMemoryItem>> HandleAsync(ListDomainMemory query, CancellationToken ct = default) => memory.ListAsync(query.Key, ct);
}
