using System.Security.Cryptography;
using System.Text;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Security;

public sealed class BudgetManagementAuthorization(OwnerAuthorization owner)
{
    public const string Header = "X-Jarvis-Owner-Key";
    public BudgetActor Require(HttpContext context) => owner.Require(context, "budget.manage");
}
