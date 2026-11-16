namespace Jarvis.Orchestrator.Runtime;

// Created by a trusted host adapter, never bound from a caller's JSON or device-id assertion.
public sealed record BudgetActor(string SubjectId, IReadOnlyList<string> Scopes);
public static class BudgetAuthorization
{
    public static void RequireOwner(BudgetActor actor) => Guard.Require(
        !string.IsNullOrWhiteSpace(actor.SubjectId) && actor.Scopes.Contains("budget.manage"), 403,
        "Owner budget-management authorization is required.");
}
