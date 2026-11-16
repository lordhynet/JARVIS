using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Intelligence;

public interface ITextReasoningProvider
{
    Task<TextReasoningResult> ReasonAsync(TextReasoningTask task, CancellationToken ct);
}
