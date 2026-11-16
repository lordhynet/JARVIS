using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Security;

public sealed class BudgetManagementAuthorization(OwnerAuthorization owner)
{
    public BudgetActor Require(HttpContext context) => owner.Require(context, "budget.manage");
}
