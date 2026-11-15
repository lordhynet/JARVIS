namespace Jarvis.Orchestrator.Runtime;

public static class ReasoningLifecycle
{
    public static void Retire(RuntimeState state, Guid scope)
    {
        state.RetiredReasoningScopes.Add(scope);
        foreach (var cached in state.ProviderResults.Values.Where(x => x.ScopeId == scope).ToList()) state.ProviderResults.Remove(cached.Id);
        foreach (var attempt in state.ProviderAttempts.Values.Where(x => x.ScopeId == scope).ToList())
            state.ProviderAttempts[attempt.Id] = attempt with { Fingerprint = "", SourceDeleted = true };
        // Keep content-free route/charge metadata; the existing financial ledger is authoritative.
    }
}
