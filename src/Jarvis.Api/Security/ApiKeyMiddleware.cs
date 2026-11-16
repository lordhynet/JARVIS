using System.Security.Cryptography;
using System.Text;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Security;

/// <summary>
/// Protected requests authenticate as a legacy browser or a server-scoped companion.
/// Sensitive administrative operations additionally require the separate owner header.
/// The legacy key is retained for migration and must not be provisioned to new phones.
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IConfiguration config, ILogger<ApiKeyMiddleware> logger)
{
    public const string HeaderName = "X-Jarvis-Key";
    public const string ConfigKey = "Jarvis:ApiKey";
    public const string FingerprintItem = "Jarvis.AuthFingerprint";
    public const string ActorItem = "Jarvis.Actor";

    // SignalR WebSocket clients cannot set custom headers, so they send the key as access_token.
    private const string QueryName = "access_token";

    public async Task InvokeAsync(HttpContext context, DeviceCredentials credentials, OwnerAuthorization owner, IPrivacyReader privacy, HubTickets hubTickets)
    {
        if (!IsProtected(context.Request.Path))
        {
            await next(context);
            return;
        }

        var expected = config[ConfigKey];
        if (string.IsNullOrWhiteSpace(expected))
        {
            logger.LogError("No API key configured ({ConfigKey}). Refusing protected request.", ConfigKey);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "JARVIS API key is not configured. See README step 3." });
            return;
        }

        var supplied = context.Request.Headers[HeaderName].FirstOrDefault() ?? BearerToken(context.Request);
        if (supplied is null && context.Request.Path.StartsWithSegments("/hubs") && context.Request.Query[QueryName].Count > 0)
        {
            Guard.Require(context.WebSockets.IsWebSocketRequest && context.Request.Path.Equals(new PathString("/hubs/status")) &&
                context.Request.Query[QueryName].Count == 1 && context.Request.Query[QueryName][0]!.StartsWith(HubTickets.Prefix, StringComparison.Ordinal),
                401, "Use a short-lived hub ticket, never a Core key in the URL.");
            context.Items[FingerprintItem] = await hubTickets.ConsumeAsync(context.Request.Query[QueryName][0]!, context.RequestAborted);
            context.Items[ActorItem] = new CoreActor("hub-ticket", null);
            await next(context); return;
        }

        var actor = supplied is { Length: <= 1024 } && supplied.StartsWith(DeviceCredentials.Prefix, StringComparison.Ordinal)
            ? await credentials.AuthenticateAsync(supplied, context.RequestAborted) : null;
        var legacy = supplied is { Length: <= 1024 } && FixedTimeEquals(supplied, expected);
        if (actor is null && !legacy)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            if (supplied?.StartsWith(DeviceCredentials.Prefix, StringComparison.Ordinal) == true)
            {
                var status = await privacy.StatusAsync(context.RequestAborted);
                if (status.Erased)
                {
                    context.Response.Headers["X-Jarvis-Data-Epoch"] = status.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    await context.Response.WriteAsJsonAsync(new { error = "Core data has been erased. Reconnect after the owner reopens Core.", coreErased = true });
                    return;
                }
            }
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid API key." });
            return;
        }

        context.Items[FingerprintItem] = Jarvis.Orchestrator.Runtime.PrivacyData.Hash(supplied!);
        context.Items[ActorItem] = actor ?? new CoreActor("legacy-owner-browser", null);
        if (actor is not null && !CompanionRoute(context.Request, actor.DeviceId!))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "This device credential is limited to its own audio companion operations." });
            return;
        }
        if (OwnerScope(context.Request) is { } scope) owner.Require(context, scope);
        using var credentialScope = DeviceCredentialContext.Enter((CoreActor)context.Items[ActorItem]!);
        using var invocation = CoreInvocationContext.Enter(actor is null ? InvocationOrigin.ManualClient : InvocationOrigin.ExternalContent);
        await next(context);
    }

    public static void RequireDevice(HttpContext context, string deviceId)
    {
        if (context.Items[ActorItem] is CoreActor { DeviceId: { } bound })
            Guard.Require(bound == deviceId, 403, "The requested device does not match the authenticated companion.");
    }
    private static bool CompanionRoute(HttpRequest request, string deviceId)
    {
        var path = request.Path.Value?.TrimEnd('/');
        return request.Method == "GET" && path is "/api/privacy/status" or "/api/voice/status" or "/api/devices" or "/api/device-capabilities" ||
            request.Method == "POST" && (path is "/api/voice/prepare" or "/api/voice/tickets" or "/api/voice/local/release" or "/api/voice/wake-candidates" || path == $"/api/devices/{deviceId}/heartbeat");
    }
    private static string? OwnerScope(HttpRequest request)
    {
        var path = request.Path.Value?.TrimEnd('/').ToLowerInvariant() ?? "";
        if (request.Method == "GET" && path == "/api/privacy/export" || request.Method == "POST" &&
            path is "/api/privacy/erase" or "/api/privacy/reopen" or "/api/privacy/retention/apply") return "privacy.manage";
        if (request.Method == "POST" && path.StartsWith("/api/devices/", StringComparison.Ordinal) && path.EndsWith("/verify", StringComparison.Ordinal)) return "device.verify";
        if (path.StartsWith("/api/device-grants", StringComparison.Ordinal) || path.StartsWith("/api/devices/", StringComparison.Ordinal) && path.Contains("/grants", StringComparison.Ordinal)) return "device.grants";
        if (request.Method is "POST" or "DELETE" && path.StartsWith("/api/devices/", StringComparison.Ordinal) && path.EndsWith("/credential", StringComparison.Ordinal)) return "device.manage";
        if (request.Method == "POST" && path.StartsWith("/api/tools/", StringComparison.Ordinal) && path.EndsWith("/confirm", StringComparison.Ordinal)) return "tool.confirm";
        if (request.Method == "POST" && System.Text.RegularExpressions.Regex.IsMatch(path, "^/api/domains/[^/]+/(activate|disable|rollback)$")) return "domain.manage";
        return null;
    }

    // The .NET SignalR client sends AccessTokenProvider values as "Authorization: Bearer <key>".
    private static string? BearerToken(HttpRequest request)
    {
        var auth = request.Headers.Authorization.FirstOrDefault();
        return auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..].Trim()
            : null;
    }

    private static bool IsProtected(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs");

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
