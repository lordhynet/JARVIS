using System.Security.Cryptography;
using System.Text;

namespace Jarvis.Api.Security;

/// <summary>
/// Minimal security baseline (handover Section 12): every /api and /hubs request
/// must present the shared key. Fails closed if no key is configured.
/// The key lives in .NET user-secrets, never in appsettings.json or Git.
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IConfiguration config, ILogger<ApiKeyMiddleware> logger)
{
    public const string HeaderName = "X-Jarvis-Key";
    public const string ConfigKey = "Jarvis:ApiKey";

    // SignalR WebSocket clients cannot set custom headers, so they send the key as access_token.
    private const string QueryName = "access_token";

    public async Task InvokeAsync(HttpContext context)
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

        var supplied = context.Request.Headers[HeaderName].FirstOrDefault()
                       ?? BearerToken(context.Request)
                       ?? (context.Request.Path.StartsWithSegments("/hubs")
                           ? context.Request.Query[QueryName].FirstOrDefault() : null);

        if (supplied is null || !FixedTimeEquals(supplied, expected))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid API key." });
            return;
        }

        await next(context);
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
