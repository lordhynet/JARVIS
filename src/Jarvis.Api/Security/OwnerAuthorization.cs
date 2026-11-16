using System.Security.Cryptography;
using System.Text;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Security;

public sealed class OwnerAuthorization(IConfiguration config)
{
    public BudgetActor Require(HttpContext context, string scope)
    {
        var expected = config["BudgetManagement:OwnerKey"]; var subject = config["BudgetManagement:SubjectId"];
        Guard.Require(expected is { Length: >= 32 and <= 1024 } && subject is { Length: > 0 and <= 80 }, 503,
            "Separate owner authorization is not configured. Configure the owner management credential on Core.");
        Guard.Require(expected != config[ApiKeyMiddleware.ConfigKey], 503, "Owner management requires a separate credential from the ordinary Core key.");
        var supplied = context.Request.Headers[CoreHeaders.OwnerKey];
        Guard.Require(supplied.Count == 1 && supplied[0] is { Length: <= 1024 } value && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)), SHA256.HashData(Encoding.UTF8.GetBytes(expected))),
            403, "Separate owner authorization is required for this operation.");
        return new(subject, [scope]);
    }
}
