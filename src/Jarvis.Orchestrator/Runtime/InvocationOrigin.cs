namespace Jarvis.Orchestrator.Runtime;

public enum InvocationOrigin { ManualClient, Agent, Voice, ExternalContent }
public static class CoreInvocationContext
{
    private static readonly AsyncLocal<InvocationOrigin?> Current = new();
    public static InvocationOrigin Origin => Current.Value ?? InvocationOrigin.ManualClient;
    public static IDisposable Enter(InvocationOrigin origin) { var before = Current.Value; Current.Value = origin; return new Scope(() => Current.Value = before); }
    private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }
}
