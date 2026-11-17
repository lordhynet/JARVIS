using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Api.Integrations;

public sealed class LegacyPracticePlanExecutor(PracticePlanProcessor processor, WorkflowService service, IWorkflowCatalog catalog) : IWorkflowStepExecutor
{
    public LegacyWorkflowDescriptor Descriptor => catalog.GetLegacy("practice-plan");
    public async Task ExecuteAsync(WorkflowRun run, CancellationToken ct) =>
        await service.CompleteAsync(run.Id, run.ClaimToken!.Value, await processor.BuildAsync(ct), ct);
}
public sealed class LegacyInterviewBriefExecutor(PaidAiProvider provider, WorkflowService service, IWorkflowCatalog catalog) : IWorkflowStepExecutor
{
    public LegacyWorkflowDescriptor Descriptor => catalog.GetLegacy("interview-brief");
    public async Task ExecuteAsync(WorkflowRun run, CancellationToken ct) =>
        await service.CompleteBriefAsync(run.Id, run.ClaimToken!.Value, await provider.GenerateAsync(run, ct), ct);
}
