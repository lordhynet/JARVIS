using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;

namespace Jarvis.Orchestrator.Workflows;

public interface IWorkflowStepExecutor
{
    LegacyWorkflowDescriptor Descriptor { get; }
    Task ExecuteAsync(WorkflowRun run, CancellationToken ct);
}
