using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Tools;

public sealed record ListToolActions : IQuery<IReadOnlyList<AuditedTool>>;

public sealed class ToolQueryHandler(IRuntimeReader reader) : IQueryHandler<ListToolActions, IReadOnlyList<AuditedTool>>
{
    public Task<IReadOnlyList<AuditedTool>> HandleAsync(ListToolActions query, CancellationToken ct = default) =>
        reader.ReadAsync<IReadOnlyList<AuditedTool>>(s => s.Tools.Values.OrderByDescending(t => t.Invocation.RequestedAt).Take(100).ToList(), ct);
}
