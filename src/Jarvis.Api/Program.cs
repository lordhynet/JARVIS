using System.Reflection;
using System.Text.Json.Serialization;
using Jarvis.Api.Endpoints;
using Jarvis.Api.Hubs;
using Jarvis.Api.Security;
using Jarvis.Orchestrator.Sessions;

var builder = WebApplication.CreateBuilder(args);

// Enums as readable strings ("MockInterview") in JSON, for both REST and SignalR.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR().AddJsonProtocol(o =>
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
builder.Services.AddSingleton<ISessionNotifier, SignalRSessionNotifier>();
builder.Services.AddSingleton<SessionService>();

var app = builder.Build();

var startedAt = DateTimeOffset.UtcNow;
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

if (string.IsNullOrWhiteSpace(app.Configuration[ApiKeyMiddleware.ConfigKey]))
    app.Logger.LogWarning("No API key set. Protected endpoints will return 503. Run: dotnet user-secrets set \"{Key}\" \"<your key>\"",
        ApiKeyMiddleware.ConfigKey);

app.UseWebSockets();
app.UseMiddleware<ApiKeyMiddleware>();

// Public: lets the watchdog and you check JARVIS is alive without a key.
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "jarvis-core",
    version,
    uptimeSeconds = (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds
}));

app.MapSessionEndpoints();
app.MapHub<StatusHub>("/hubs/status");

app.Run();

// Exposes Program to WebApplicationFactory in Jarvis.Tests.
public partial class Program;
