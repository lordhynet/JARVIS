using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Privacy;

public sealed class PrivacyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IPrivacyReader reader)
    {
        if (!context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/hubs")) { await next(context); return; }
        var status = await reader.StatusAsync(context.RequestAborted);
        context.Response.Headers["X-Jarvis-Data-Epoch"] = status.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var suppliedEpoch = context.Request.Headers["X-Jarvis-Data-Epoch"];
        if (!context.Request.Path.StartsWithSegments(CoreRoutes.Privacy) && suppliedEpoch.Count > 0 &&
            (suppliedEpoch.Count != 1 || !long.TryParse(suppliedEpoch[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var epoch) || epoch != status.Epoch))
        {
            context.Response.StatusCode = 409;
            await context.Response.WriteAsJsonAsync(new { error = "Core data generation changed. Clear local cached data and reconnect.", coreEpochChanged = true }); return;
        }
        if (status.Erased && !context.Request.Path.StartsWithSegments(CoreRoutes.Privacy))
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(new { error = "Core data has been erased. Rotate the Core API key and reopen from Privacy controls.", coreErased = true }); return;
        }
        using var scope = PrivacyContext.Enter(status.Epoch);
        await next(context);
    }
}
