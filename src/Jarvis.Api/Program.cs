using System.Reflection;
using System.Text.Json.Serialization;
using Jarvis.Api.Endpoints;
using Jarvis.Api.Hubs;
using Jarvis.Api.Security;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Persistence;
using Microsoft.EntityFrameworkCore;
using Jarvis.Api;
using Jarvis.Api.Integrations;
using Jarvis.Orchestrator.Runtime;
using Microsoft.AspNetCore.RateLimiting;
using Jarvis.Orchestrator.Cqrs.Integrations;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 11_000_000);
builder.Services.AddHttpClient("providers", client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.MaxResponseContentBufferSize = 8_000_000;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddFixedWindowLimiter("cloud", options =>
    {
        options.PermitLimit = 20;
        options.Window = TimeSpan.FromMinutes(1);
        options.QueueLimit = 0;
    });
});

// Enums as readable strings ("MockInterview") in JSON, for both REST and SignalR.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR().AddJsonProtocol(o =>
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);
var storageProvider = builder.Configuration["Jarvis:StorageProvider"] ?? "InMemory";
if (storageProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
{
    var connection = builder.Configuration.GetConnectionString("Jarvis");
    if (string.IsNullOrWhiteSpace(connection))
        throw new InvalidOperationException("Postgres storage requires ConnectionStrings:Jarvis.");
    builder.Services.AddDbContextFactory<SessionDbContext>(o => o.UseNpgsql(connection));
    builder.Services.AddSingleton<ISessionStore, PostgresSessionStore>();
    builder.Services.AddSingleton<IRuntimeStore, PostgresRuntimeStore>();
    builder.Services.AddSingleton<IStorageReadiness, PostgresReadiness>();
}
else if (storageProvider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
    builder.Services.AddSingleton<IRuntimeStore, InMemoryRuntimeStore>();
    builder.Services.AddSingleton<IStorageReadiness, InMemoryReadiness>();
}
else
{
    throw new InvalidOperationException("Jarvis:StorageProvider must be InMemory or Postgres.");
}
builder.Services.AddSingleton<ISessionNotifier, SignalRSessionNotifier>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<DeviceService>();
builder.Services.AddSingleton<SessionLifecycleService>();
builder.Services.AddSingleton<LearningService>();
builder.Services.AddSingleton<ToolService>();
builder.Services.AddSingleton<ProviderGateway>();
builder.Services.AddSingleton<IHomeControl>(s => s.GetRequiredService<ProviderGateway>());
builder.Services.AddSingleton<IIntegrationReader>(s => s.GetRequiredService<ProviderGateway>());
builder.Services.AddSingleton<OpenAiProvider>();
builder.Services.AddSingleton<IConversationProvider>(s => s.GetRequiredService<OpenAiProvider>());
builder.Services.AddSingleton<ITranscriptionProvider>(s => s.GetRequiredService<OpenAiProvider>());
builder.Services.AddSingleton<ConversationService>();
builder.Services.AddJarvisCqrs();
builder.Services.AddHostedService<RuntimeWorker>();

var app = builder.Build();

if (storageProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
{
    await using var db = await app.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>()
        .CreateDbContextAsync();
    await db.Database.MigrateAsync();
}
else
{
    app.Logger.LogWarning("InMemory storage selected. Sessions will be lost when the API stops.");
}

var startedAt = DateTimeOffset.UtcNow;
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

if (string.IsNullOrWhiteSpace(app.Configuration[ApiKeyMiddleware.ConfigKey]))
    app.Logger.LogWarning("No API key set. Protected endpoints will return 503. Run: dotnet user-secrets set \"{Key}\" \"<your key>\"",
        ApiKeyMiddleware.ConfigKey);

app.UseWebSockets();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; media-src 'self' blob:; object-src 'none'; frame-ancestors 'none'; base-uri 'self'";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    try { await next(context); }
    catch (DomainException ex) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = ex.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (Exception ex) when (!context.Response.HasStarted && (ex is HttpRequestException || ex is OperationCanceledException && !context.RequestAborted.IsCancellationRequested))
    {
        context.Response.StatusCode = 502;
        await context.Response.WriteAsJsonAsync(new { error = "External service unavailable. Please try again later." });
    }
    catch (System.Text.Json.JsonException) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = 502;
        await context.Response.WriteAsJsonAsync(new { error = "External service returned an invalid response." });
    }
});
app.UseMiddleware<ApiKeyMiddleware>();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

// Public: lets the watchdog and you check JARVIS is alive without a key.
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "jarvis-core",
    version,
    uptimeSeconds = (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds
}));

app.MapSessionEndpoints();
app.MapRuntimeEndpoints();
app.MapHub<StatusHub>("/hubs/status");

app.Run();

// Exposes Program to WebApplicationFactory in Jarvis.Tests.
public partial class Program;
