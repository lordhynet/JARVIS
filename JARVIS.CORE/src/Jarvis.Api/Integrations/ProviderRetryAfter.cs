using System.Net;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Integrations;

internal static class ProviderRetryAfter
{
    // This records cooldown only. It never retries a paid request or releases an uncertain reservation.
    public static void RequireSuccess(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.IsSuccessStatusCode) return;
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta ?? (header?.Date is { } date ? date - now : TimeSpan.FromSeconds(60));
        throw new DomainException(response.StatusCode == HttpStatusCode.TooManyRequests ? 429 : 502,
            "Provider rejected the request; its reservation is retained and automatic paid replay is blocked.") {
            RetryAfter = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                ? TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 1, 86400)) : null
        };
    }
}
