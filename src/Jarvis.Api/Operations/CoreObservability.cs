using System.Diagnostics;
using Jarvis.Orchestrator.Runtime;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Jarvis.Api.Operations;

public static class CoreObservability
{
    public static void AddCoreObservability(this IServiceCollection services, IConfiguration config)
    {
        if (!config.GetValue("Observability:Otlp:Enabled", false)) return;
        var endpoint = config["Observability:Otlp:Endpoint"] ?? "http://127.0.0.1:4318";
        Guard.Require(Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0,
            400, "OTLP endpoint must be an absolute HTTP(S) collector URL without credentials or query.");
        var root = uri.AbsoluteUri.TrimEnd('/');
        services.AddOpenTelemetry().WithTracing(t => t.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("jarvis-core"))
            .AddSource(CoreTelemetry.Name).AddOtlpExporter(o => { o.Endpoint = new(root + "/v1/traces"); o.Protocol = OtlpExportProtocol.HttpProtobuf; o.Headers = ""; o.TimeoutMilliseconds = 3000; }))
            .WithMetrics(m => m.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("jarvis-core"))
            .AddMeter(CoreTelemetry.Name).AddMeter("Microsoft.AspNetCore.Hosting", "System.Net.Http", "System.Runtime")
            .AddOtlpExporter(o => { o.Endpoint = new(root + "/v1/metrics"); o.Protocol = OtlpExportProtocol.HttpProtobuf; o.Headers = ""; o.TimeoutMilliseconds = 3000; }));
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, string provider, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp(); var outcome = "failed";
        using var span = CoreTelemetry.Activities.StartActivity("provider.dispatch"); span?.SetTag("provider", provider);
        try { var response = await client.SendAsync(request, ct); outcome = response.IsSuccessStatusCode ? "accepted" : "rejected"; span?.SetTag("http.response.status_code", (int)response.StatusCode); return response; }
        finally
        {
            var tags = new TagList { { "provider", provider }, { "outcome", outcome } };
            CoreTelemetry.ProviderCalls.Add(1, tags); CoreTelemetry.ProviderDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        }
    }
}

public sealed class OperationsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp(); using var span = CoreTelemetry.Activities.StartActivity("core.request");
        try { await next(context); }
        finally
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            var tags = new TagList { { "http.route", route }, { "http.request.method", context.Request.Method }, { "http.response.status_code", context.Response.StatusCode } };
            CoreTelemetry.Requests.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
            span?.SetTag("http.route", route); span?.SetTag("http.response.status_code", context.Response.StatusCode);
        }
    }
}
