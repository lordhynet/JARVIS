namespace Jarvis.Orchestrator.Training;

// Generated practice questions are OFF unless the owner explicitly enables them after review. While off, no context manifest
// is recorded and no generated question can be created, so ordinary realtime sessions behave exactly as before.
public sealed record PracticeGenerationOptions(bool Enabled)
{
    public const string ConfigKey = "Practice:GeneratedQuestions:Enabled";
    public static readonly PracticeGenerationOptions Disabled = new(false);
}
