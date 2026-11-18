using Jarvis.Contracts;
using System.Reflection;
using Jarvis.Api.Operations;
using Jarvis.Api.Notifications;
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
using Jarvis.Orchestrator.Workflows;

// Operator/CI diagnostic only: canonical decoding without configuration, credentials or AI calls.
if (args.Length == 2 && args[0] == "--verify-audio-file")
{
    var prepared = await new AudioDurationProbe(new ConfigurationBuilder().Build()).PrepareAsync(args[1], CancellationToken.None);
    try { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { prepared.Seconds, Bytes = new FileInfo(prepared.Path).Length })); }
    finally { File.Delete(prepared.Path); }
    return;
}
if (args.Contains("--health-probe", StringComparer.Ordinal))
{
    try
    {
        using var settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
        var port = settings.RootElement.GetProperty("Server").GetProperty(OperatingSystem.IsWindows() ? "Windows" : "Container").GetProperty("HttpPort").GetInt32();
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var response = await probe.GetAsync($"http://127.0.0.1:{port}/health/ready");
        Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
    }
    catch { Environment.ExitCode = 1; }
    return;
}
var builder = WebApplication.CreateBuilder(args);
var testing = builder.Environment.IsEnvironment("Testing");
var fixtureOverrides = testing ? builder.Configuration.AsEnumerable().ToList() : null;
builder.Configuration.Sources.Clear();
if (testing)
{
    builder.Configuration.AddJsonStream(typeof(Program).Assembly.GetManifestResourceStream("Jarvis.DefaultSettings")!);
    builder.Configuration.AddInMemoryCollection(fixtureOverrides!);
}
else
{
    // Exactly one production source; environment variables, user-secrets, command-line
    // and environment-specific JSON cannot silently override the canonical file.
    builder.Configuration.SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
    var host = builder.Configuration.GetSection(OperatingSystem.IsWindows() ? "Server:Windows" : "Server:Container");
    builder.WebHost.ConfigureKestrel(o =>
    {
        var address = System.Net.IPAddress.Parse(host["BindAddress"] ?? "127.0.0.1");
        o.Listen(address, host.GetValue<int>("HttpPort"));
        if (builder.Configuration.GetValue<bool>("Server:Tls:Enabled"))
            o.Listen(address, host.GetValue<int>("HttpsPort"), listen => listen.UseHttps(host["CertificatePath"]!, builder.Configuration["Server:Tls:Password"]));
    });
}
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = builder.Configuration.GetValue("Server:MaxRequestBodyBytes", 11_000_000L));
builder.Services.AddHttpClient("providers", client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.MaxResponseContentBufferSize = 8_000_000;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<CoreRequestLimits>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    foreach (var policy in new[] { CoreRequestLimits.Cloud, CoreRequestLimits.Voice })
        o.AddPolicy(policy, context => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            context.Items[ApiKeyMiddleware.ActorItem] is CoreActor actor ? actor.SubjectId : "unauthenticated",
            _ => CoreRequestLimits.Window(context.Items[ApiKeyMiddleware.ActorItem] is not CoreActor ? CoreRequestLimits.AnonymousPermits :
                policy == CoreRequestLimits.Voice ? CoreRequestLimits.VoicePermits : CoreRequestLimits.CloudPermits)));
});

// Enums as readable strings ("MockInterview") in JSON, for both REST and SignalR.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR().AddJsonProtocol(o =>
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AudioFenceChanges>();
builder.Services.AddCoreAuthorization();
var storageProvider = builder.Configuration["Jarvis:StorageProvider"] ?? "InMemory";
if (storageProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
{
    var connection = testing ? builder.Configuration.GetConnectionString("Jarvis") : new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = OperatingSystem.IsWindows() ? "127.0.0.1" : builder.Configuration["Database:Host"],
        Port = builder.Configuration.GetValue<int>(OperatingSystem.IsWindows() ? "Database:ExposedPort" : "Database:Port"),
        Database = builder.Configuration["Database:Name"], Username = builder.Configuration["Database:Username"], Password = builder.Configuration["Database:Password"]
    }.ConnectionString;
    if (string.IsNullOrWhiteSpace(connection))
        throw new InvalidOperationException("Postgres storage requires ConnectionStrings:Jarvis.");
    builder.Services.AddDbContextFactory<SessionDbContext>(o => o.UseNpgsql(connection));
    builder.Services.AddSingleton<ISessionStore, PostgresSessionStore>();
    builder.Services.AddSingleton<IDeviceGrantStore, PostgresDeviceGrantStore>();
    builder.Services.AddSingleton<IDeviceCommandReader, PostgresDeviceCommandStore>();
    builder.Services.AddSingleton<IDeviceReceiptReader, PostgresDeviceReceiptStore>();
    builder.Services.AddSingleton<IRuntimeStore, PostgresRuntimeStore>();
    builder.Services.AddSingleton<ICoreTransaction, PostgresCoreTransaction>();
    builder.Services.AddSingleton<IWorkflowStore, PostgresWorkflowStore>();
    builder.Services.AddSingleton<IPrivacyStore, PostgresPrivacyStore>();
    builder.Services.AddSingleton<Jarvis.Orchestrator.Domains.IDomainRegistryStore, PostgresDomainRegistryStore>();
    builder.Services.AddSingleton<IStorageReadiness, PostgresReadiness>();
}
else if (storageProvider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<InMemorySessionStore>(); builder.Services.AddSingleton<InMemoryRuntimeStore>(); builder.Services.AddSingleton<InMemoryWorkflowStore>();
    builder.Services.AddSingleton<MemoryPrivacyBoundary>();
    builder.Services.AddSingleton<ISessionStore, GuardedMemorySessions>();
    builder.Services.AddSingleton<InMemoryDeviceGrantStore>(); builder.Services.AddSingleton<IDeviceGrantStore>(s => s.GetRequiredService<InMemoryDeviceGrantStore>());
    builder.Services.AddSingleton<IDeviceCommandReader, InMemoryDeviceCommandStore>();
    builder.Services.AddSingleton<IDeviceReceiptReader, InMemoryDeviceReceiptStore>();
    builder.Services.AddSingleton<IRuntimeStore, GuardedMemoryRuntime>();
    builder.Services.AddSingleton<ICoreTransaction, MemoryCoreTransaction>();
    builder.Services.AddSingleton<IWorkflowStore, GuardedMemoryWorkflows>();
    builder.Services.AddSingleton<IPrivacyStore, InMemoryPrivacyStore>();
    builder.Services.AddSingleton<Jarvis.Orchestrator.Domains.IDomainRegistryStore, Jarvis.Orchestrator.Domains.MemoryDomainRegistryStore>();
    builder.Services.AddSingleton<IStorageReadiness, InMemoryReadiness>();
}
else
{
    throw new InvalidOperationException("Jarvis:StorageProvider must be InMemory or Postgres.");
}
builder.Services.AddSingleton<ISessionNotifier, SignalRSessionNotifier>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<VoiceTranscriptService>();
builder.Services.AddSingleton<DeviceService>();
builder.Services.AddSingleton<SessionLifecycleService>();
builder.Services.AddSingleton<LearningService>();
builder.Services.AddSingleton<ToolService>();
builder.Services.AddSingleton<ProviderGateway>();
builder.Services.AddSingleton<HomeAssistantCommandAdapter>();
builder.Services.AddSingleton<IHomeControl>(s => s.GetRequiredService<HomeAssistantCommandAdapter>());
if (!testing) builder.Services.AddHostedService<HomeAssistantCommandWorker>();
builder.Services.AddSingleton<IIntegrationReader>(s => s.GetRequiredService<ProviderGateway>());
builder.Services.AddSingleton<OpenAiProvider>();
builder.Services.AddSingleton<IConversationProvider>(s => s.GetRequiredService<OpenAiProvider>());
builder.Services.AddSingleton<ITranscriptionProvider>(s => s.GetRequiredService<OpenAiProvider>());
builder.Services.AddSingleton<ConversationService>();
builder.Services.AddJarvisCqrs();
builder.Services.AddHostedService<RuntimeWorker>();
builder.Services.AddSingleton<WorkflowService>();
builder.Services.AddSingleton<PracticePlanProcessor>();
builder.Services.AddSingleton<IWorkflowStepExecutor, LegacyPracticePlanExecutor>();
builder.Services.AddSingleton<IWorkflowStepExecutor, LegacyInterviewBriefExecutor>();
builder.Services.AddSingleton<UsageBudget>();
builder.Services.AddSingleton<IBudgetPolicySource, BudgetSettings>();
builder.Services.AddSingleton<FxQuoteCache>();
builder.Services.AddHttpClient(FxAutoSettings.ClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddHostedService<FxQuoteRefresher>();
builder.Services.AddSingleton<BudgetApprovalService>();
builder.Services.AddSingleton<BudgetManagementAuthorization>();
builder.Services.AddSingleton<ManagedAi>();
builder.Services.AddSingleton<RateCard>();
builder.Services.AddSingleton<TextReasoningService>();
builder.Services.AddSingleton<Jarvis.Orchestrator.Intelligence.IIntelligenceEvaluator, IntelligenceEvaluator>();
builder.Services.AddSingleton<Jarvis.Orchestrator.Intelligence.ITextReasoningProvider>(s => s.GetRequiredService<TextReasoningService>());
builder.Services.AddSingleton<ITextReasoningAdapter, OpenAiResponsesAdapter>();
builder.Services.AddSingleton<ITextReasoningAdapter, AnthropicMessagesAdapter>();
builder.Services.AddSingleton<AiCoach>();
builder.Services.AddSingleton<Jarvis.Orchestrator.Cqrs.Coaching.IAiAssessmentService>(s => s.GetRequiredService<AiCoach>());
builder.Services.AddSingleton<AgentRuntime>();
builder.Services.AddSingleton<UsageReconciler>();
builder.Services.AddSingleton<IPushSender, WebPushSender>();
builder.Services.AddSingleton<PushNotifications>();
builder.Services.AddHostedService<PushWorker>();
builder.Services.AddHttpClient("push", c => c.Timeout = TimeSpan.FromSeconds(10)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<CalendarTriggerSync>();
builder.Services.AddHostedService<CalendarTriggerWorker>();
builder.Services.AddSingleton<IAgentExecutor, AgentExecutor>();
builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddSingleton<IAudioDurationProbe, AudioDurationProbe>();
builder.Services.AddSingleton<AudioOwnership>();
builder.Services.AddSingleton<CompanionVoiceService>();
builder.Services.AddSingleton<WakeCandidateArbiter>();
builder.Services.AddSingleton<Jarvis.Orchestrator.Training.VoicePracticeService>();
builder.Services.AddSingleton<VoiceRecallService>();
builder.Services.AddSingleton<DeviceCredentials>();
builder.Services.AddSingleton<DeviceGrantService>();
builder.Services.AddSingleton<DeviceCommandService>();
builder.Services.AddSingleton<DeviceReceiptService>();
builder.Services.AddSingleton<Jarvis.Orchestrator.Runtime.IDeviceCapabilityCatalog>(
    _ => new Jarvis.Orchestrator.Runtime.DeviceCapabilityCatalog(Jarvis.Orchestrator.Runtime.BuiltInDeviceCapabilities.All.Concat(WindowsDeviceCapabilities.All)));
builder.Services.AddSingleton<OwnerAuthorization>();
builder.Services.AddSingleton<Jarvis.Orchestrator.Domains.IWorkflowCatalog, Jarvis.Orchestrator.Domains.WorkflowCatalog>();
builder.Services.AddSingleton<Jarvis.Orchestrator.Domains.ICapabilityCatalog>(s => new Jarvis.Orchestrator.Domains.CapabilityCatalog(
    Jarvis.Api.Voice.CoreVoiceTools.Capabilities.Concat(AgentCapabilityIds.BuiltIns).Concat(
        s.GetRequiredService<IDeviceCapabilityCatalog>().Installed.Select(d => new Jarvis.Orchestrator.Domains.CapabilityDescriptor(d.Id, d.Description,
            Jarvis.Orchestrator.Domains.CapabilityRisk.ExternalAction, d.Version, d.ArgumentSchemaJson, [ReasoningDataClass.Personal],
            d.Risk is DeviceCapabilityRisk.Sensitive or DeviceCapabilityRisk.Irreversible, d.Risk == DeviceCapabilityRisk.Irreversible, ActionRisk: d.Risk)))));
builder.Services.AddSingleton<Jarvis.Orchestrator.Domains.ICapabilityRegistry>(s => s.GetRequiredService<Jarvis.Orchestrator.Domains.ICapabilityCatalog>());
builder.Services.AddSingleton<AgentSpecialistCatalog>(_ => new AgentSpecialistCatalog());
builder.Services.AddSingleton<IAgentToolHandler, AgentTemplateTool>();
builder.Services.AddSingleton<IAgentToolHandler>(s => new AgentReasoningTool(s.GetRequiredService<TextReasoningService>(), false));
builder.Services.AddSingleton<IAgentToolHandler>(s => new AgentReasoningTool(s.GetRequiredService<TextReasoningService>(), true));
builder.Services.AddSingleton<AgentToolGateway>();
builder.Services.AddSingleton<AgentActionService>();
foreach (var capability in BuiltInDeviceCapabilities.All.Concat(WindowsDeviceCapabilities.All)) {
    var id = capability.Id;
    builder.Services.AddSingleton<IAgentToolHandler>(s => new AgentDeviceTool(id, s.GetRequiredService<IRuntimeStore>(),
        s.GetRequiredService<IDeviceGrantStore>(), s.GetRequiredService<IDeviceCapabilityCatalog>(), s.GetRequiredService<DeviceCommandService>(), s.GetRequiredService<TimeProvider>()));
}
builder.Services.AddSingleton<Jarvis.Orchestrator.Domains.IDomainFactory, Jarvis.Orchestrator.Domains.DomainFactory>();
builder.Services.AddSingleton<Jarvis.Orchestrator.Intelligence.ContextMemoryService>();
builder.Services.AddSingleton<Jarvis.Orchestrator.Domains.IDomainRegistry, Jarvis.Orchestrator.Domains.DomainRegistryService>();
builder.Services.AddSingleton<Jarvis.Orchestrator.Domains.IDomainCatalog, Jarvis.Orchestrator.Domains.DomainCatalog>();
builder.Services.AddSingleton<HubTickets>();
builder.Services.AddSingleton<Jarvis.Orchestrator.Coaching.VoiceCoachingService>();
builder.Services.AddSingleton<Jarvis.Api.Voice.CoreVoiceTools>();
builder.Services.AddSingleton<Jarvis.Api.Voice.CoreWindowsVoiceTool>();
builder.Services.AddSingleton<Jarvis.Api.Voice.CoreHomeVoiceTool>();
builder.Services.AddSingleton<Jarvis.Orchestrator.Intelligence.IRealtimeConnectionFactory, Jarvis.Api.Voice.RealtimeConnectionFactory>();
builder.Services.AddSingleton<Jarvis.Api.Voice.RealtimeGateway>();
builder.Services.AddSingleton<PaidAiProvider>();
builder.Services.AddHostedService<WorkflowWorker>();
builder.Services.AddSingleton<Jarvis.Api.Privacy.RetentionSettings>();
builder.Services.AddHostedService<Jarvis.Api.Privacy.RetentionWorker>();

builder.Services.AddCoreObservability(builder.Configuration);
var app = builder.Build();

if (storageProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
{
    await using var db = await app.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>()
        .CreateDbContextAsync();
    if (app.Configuration.GetValue("Jarvis:MigrateOnStartup", true)) {
        await db.Database.OpenConnectionAsync();
        try {
            // Database-scoped advisory lock also covers first-host migration history creation.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(746271590)");
            try { await db.Database.MigrateAsync(); }
            finally { await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(746271590)"); }
        } finally { await db.Database.CloseConnectionAsync(); }
    } else Guard.Require(!(await db.Database.GetPendingMigrationsAsync()).Any(), 503, "Run the designated migrator before starting this Core host.");
}
else
{
    app.Logger.LogWarning("InMemory storage selected. Sessions will be lost when the API stops.");
}

var startedAt = DateTimeOffset.UtcNow;
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

if (string.IsNullOrWhiteSpace(app.Configuration[ConfigKeys.CoreApiKey]))
    app.Logger.LogWarning("No API key set. Protected endpoints will return 503. Configure {Key} in the private root appsettings.json.",
        ConfigKeys.CoreApiKey);

app.UseWebSockets();
app.UseMiddleware<OperationsMiddleware>();
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
        if (ex.RetryAfter is { } retry) context.Response.Headers.RetryAfter = Math.Clamp((int)Math.Ceiling(retry.TotalSeconds), 1, 86400).ToString(System.Globalization.CultureInfo.InvariantCulture);
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
app.UseRouting();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseAuthentication();
// Static browser assets are public and must short-circuit before the deny-by-default
// endpoint authorization fallback, which applies to every routed endpoint.
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthorization();
app.UseMiddleware<Jarvis.Api.Privacy.PrivacyMiddleware>();
app.UseRateLimiter();
app.MapVoiceEndpoints();
app.MapAgentEndpoints();
app.MapDomainEndpoints();
app.MapBudgetEndpoints();
app.MapPrivacyEndpoints();
app.MapPost("/api/hubs/ticket", (HubTickets tickets, CancellationToken ct) => tickets.IssueAsync(ct)).RequireAuthorization(CorePolicies.Owner);
app.MapImportEndpoints();
app.MapGet("/api/push/status", (PushNotifications push, IConfiguration config) => new { ready = push.Ready, publicKey = push.Ready ? config["Push:PublicKey"] : "" }).RequireAuthorization(CorePolicies.Owner);
app.MapPost("/api/push/subscriptions", (Jarvis.Contracts.SavePushSubscriptionRequest request, HttpContext context, PushNotifications push, CancellationToken ct) => push.SubscribeAsync(request, (string)context.Items[ApiKeyMiddleware.FingerprintItem]!, ct)).RequireAuthorization(CorePolicies.Owner);
app.MapDelete("/api/push/subscriptions/{id:guid}", (Guid id, PushNotifications push, CancellationToken ct) => push.UnsubscribeAsync(id, ct)).RequireAuthorization(CorePolicies.Owner);
app.MapGet("/api/operations", (IRuntimeReader reader, TimeProvider clock, CancellationToken ct) => reader.ReadAsync(s => new
{
    queuedTasks = s.AgentTasks.Values.Count(x => x.Status is Jarvis.Contracts.AgentTaskStatus.Queued or Jarvis.Contracts.AgentTaskStatus.RetryScheduled),
    runningTasks = s.AgentTasks.Values.Count(x => x.Status == Jarvis.Contracts.AgentTaskStatus.Running),
    expiredLeases = s.AgentTasks.Values.Count(x => x.Status == Jarvis.Contracts.AgentTaskStatus.Running && x.LeaseExpiresAt <= clock.GetUtcNow()),
    activeGoals = s.Goals.Values.Count(x => x.Status == GoalStatus.Active), pendingTriggers = s.AgentTriggers.Values.Count(x => x.Status == AgentTriggerStatus.Pending),
    uncertainCharges = s.UsageReservations.Values.Count(x => x.ActualUsd is null || x.ReconciliationRequired),
    monthCommittedUsd = s.UsageReservations.Values.Where(x => x.At.Year == clock.GetUtcNow().Year && x.At.Month == clock.GetUtcNow().Month).Sum(x => x.ActualUsd ?? x.ReservedUsd),
    agentsPaused = s.AgentControls.EffectiveAgentsPaused,
    paidAiPaused = s.AgentControls.EffectivePaidAiPaused
}, ct)).RequireAuthorization(CorePolicies.Owner);
// Public: lets the watchdog and you check JARVIS is alive without a key.
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "jarvis-core",
    version,
    uptimeSeconds = (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds
})).AllowAnonymous();

app.MapSessionEndpoints();
app.MapRuntimeEndpoints();
app.MapHub<StatusHub>("/hubs/status").RequireAuthorization(CorePolicies.Owner);
app.MapDeviceCapabilityEndpoints();
app.MapDeviceGrantEndpoints();
app.MapDeviceCommandEndpoints();

app.Run();

// Exposes Program to WebApplicationFactory in Jarvis.Tests.
public partial class Program;
