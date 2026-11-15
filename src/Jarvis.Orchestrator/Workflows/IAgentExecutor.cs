using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Workflows;

/// <summary>Application-owned port for executing one claimed agent task.</summary>
public interface IAgentExecutor
{
    Task<string> ExecuteAsync(AgentTask task, CancellationToken ct);
}
