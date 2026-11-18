using System.Net;
using System.Text.Json;
using Jarvis.Api.Security;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using WebPush;

namespace Jarvis.Api.Notifications;

public interface IPushSender
{
    Task SendAsync(BrowserPushSubscription subscription, Guid noticeId, CancellationToken ct);
    Task SendAsync(BrowserPushSubscription subscription, AgentNotification notice, CancellationToken ct) => SendAsync(subscription, notice.Id, ct);
}
public static class PushEligibility
{
    public static bool HasDispatchWork(RuntimeState s, DateTimeOffset now, string fingerprint)
        => HasDispatchWork(s, now, new HashSet<string> { fingerprint });
    public static bool HasDispatchWork(RuntimeState s, DateTimeOffset now, IReadOnlySet<string> fingerprints, bool travelWhenPaused = true)
    {
        if (s.PushDeliveries.Values.Any(d => !s.AgentOutbox.ContainsKey(d.NotificationId) || !s.PushSubscriptions.ContainsKey(d.SubscriptionId))) return true;
        return s.AgentOutbox.Values.Any(n => Allowed(s, n, now, travelWhenPaused) &&
                s.PushSubscriptions.Values.Any(subscription => fingerprints.Contains(subscription.KeyFingerprint) &&
                    (!s.PushDeliveries.TryGetValue(AgentRuntime.StableId($"push-v1:{n.Id}:{subscription.Id}"), out var delivery) || IsReady(delivery, now))));
    }

    public static bool PublicTravel(AgentNotification notice) => notice.Category == NotificationCategories.Travel && notice.RunId == Guid.Empty &&
        notice.TravelAuthority is { RouteId: var route, RouteRevision: > 0, LineId.Length: > 0, Occurrence.Length: > 0 } && route != Guid.Empty &&
        notice.PublicSummary is { Length: > 0 } summary && System.Text.Encoding.UTF8.GetByteCount(summary) <= 240 && !summary.Any(char.IsControl);
    public static bool Allowed(RuntimeState state, AgentNotification notice, DateTimeOffset now, bool travelWhenPaused)
    {
        if (!notice.Delivered || notice.Read || notice.CreatedAt > now || notice.DeliverAfter > now || notice.ExpiresAt <= now) return false;
        // Core writes this authority only inside an opted-in window and bounds expiry to its end.
        // Route changes atomically expire it. Do not nest a travel-store read inside the runtime transaction.
        if (notice.Category == NotificationCategories.Travel) return PublicTravel(notice) && (!state.AgentControls.EffectiveAgentsPaused || travelWhenPaused);
        return !state.AgentControls.EffectiveAgentsPaused && !AgentRuntime.IsQuiet(state.AgentControls, now);
    }

    public static bool IsReady(PushDelivery delivery, DateTimeOffset now) =>
        delivery.Attempt < 3 && delivery.AvailableAt <= now &&
        (delivery.Status == "Pending" || delivery.Status == "Sending" && delivery.LeaseExpiresAt <= now);
}

public sealed class WebPushSender(IHttpClientFactory clients, IConfiguration config, TimeProvider clock) : IPushSender
{
    public const string PrivateBody = "Open your private JARVIS inbox for a new update.";
    public static string Payload(Guid id, AgentNotification? notice = null)
    {
        var travel = notice is not null && PushEligibility.PublicTravel(notice);
        return JsonSerializer.Serialize(new { title = travel ? "JARVIS travel" : "JARVIS update", category = travel ? NotificationCategories.Travel : NotificationCategories.Agent,
            body = travel ? notice!.PublicSummary : PrivateBody, expiresAt = travel ? notice!.ExpiresAt : (DateTimeOffset?)null, tag = id.ToString() });
    }
    public Task SendAsync(BrowserPushSubscription subscription, Guid noticeId, CancellationToken ct) => SendPayloadAsync(subscription, Payload(noticeId), ct);
    public Task SendAsync(BrowserPushSubscription subscription, AgentNotification notice, CancellationToken ct) => SendPayloadAsync(subscription, Payload(notice.Id, notice), ct,
        PushEligibility.PublicTravel(notice) ? Math.Clamp((int)Math.Floor((notice.ExpiresAt - clock.GetUtcNow()).TotalSeconds), 0, 300) : 300);
    private async Task SendPayloadAsync(BrowserPushSubscription subscription, string payload, CancellationToken ct, int ttl = 300)
    {
        PushNotifications.ValidateEndpoint(subscription.Endpoint);
        using var sender = new WebPushClient(clients.CreateClient("push"));
        var keys = new VapidDetails(config["Push:Subject"], config["Push:PublicKey"], config["Push:PrivateKey"]);
        await sender.SendNotificationAsync(new PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth),
            payload,
            new Dictionary<string, object> { ["vapidDetails"] = keys, ["TTL"] = ttl }, ct);
    }
}

public sealed class PushNotifications(IRuntimeStore store, IConfiguration config, IPushSender sender, TimeProvider clock)
{
    public bool Ready => config.GetValue("Push:Enabled", false) && !string.IsNullOrWhiteSpace(config["Push:PublicKey"]) && !string.IsNullOrWhiteSpace(config["Push:PrivateKey"]);
    public static void ValidateEndpoint(string endpoint)
    {
        Guard.Require(!string.IsNullOrWhiteSpace(endpoint) && endpoint.Length <= 3000 && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.IsDefaultPort &&
            (uri.Host is "fcm.googleapis.com" or "updates.push.services.mozilla.com" || uri.Host.EndsWith(".push.apple.com", StringComparison.OrdinalIgnoreCase)),
            400, "Unsupported browser push destination.");
    }
    private static byte[] Decode(string value)
    {
        Guard.Require(!string.IsNullOrWhiteSpace(value) && value.Length <= 100 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), 400, "Invalid browser subscription key.");
        try { return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4)); }
        catch (FormatException) { throw new DomainException(400, "Invalid browser subscription key."); }
    }
    public Task<Guid> SubscribeAsync(SavePushSubscriptionRequest request, string fingerprint, CancellationToken ct)
    {
        Guard.Require(Ready, 503, "Browser push is not configured."); ValidateEndpoint(request.Endpoint);
        Guard.Require(Decode(request.P256dh).Length == 65 && Decode(request.Auth).Length == 16, 400, "Subscription key lengths are invalid.");
        var id = AgentRuntime.StableId(request.Endpoint);
        return store.UpdateAsync(s =>
        {
            Guard.Require(BrowserOwnerAccess.ActiveFingerprints(s, config).Contains(fingerprint), 401, "The browser credential changed before subscription.");
            Guard.Require(s.PushSubscriptions.ContainsKey(id) || s.PushSubscriptions.Count < 50, 409, "Browser notification device limit reached.");
            s.PushSubscriptions[id] = new(id, request.Endpoint, request.P256dh, request.Auth, fingerprint, clock.GetUtcNow()); return id;
        }, ct);
    }
    public Task<bool> UnsubscribeAsync(Guid id, CancellationToken ct) => store.UpdateAsync(s => s.PushSubscriptions.Remove(id), ct);

    public async Task<bool> DispatchOneAsync(CancellationToken ct)
    {
        if (!Ready) return false;
        var nowCheck = clock.GetUtcNow();
        var travelWhenPaused = config.GetValue("Travel:NotifyWhenAgentsPaused", true);
        if (!await store.ReadAsync(s => PushEligibility.HasDispatchWork(s, nowCheck, BrowserOwnerAccess.ActiveFingerprints(s, config), travelWhenPaused), ct)) return false;
        var claim = await store.UpdateAsync(s =>
        {
            var now = clock.GetUtcNow();
            var fingerprints = BrowserOwnerAccess.ActiveFingerprints(s, config);
            foreach (var obsolete in s.PushDeliveries.Values.Where(x => !s.AgentOutbox.ContainsKey(x.NotificationId) || !s.PushSubscriptions.ContainsKey(x.SubscriptionId)).ToList()) s.PushDeliveries.Remove(obsolete.Id);
            if (!PushEligibility.HasDispatchWork(s, now, fingerprints, travelWhenPaused)) return (Delivery: (PushDelivery?)null, Subscription: (BrowserPushSubscription?)null, Notice: (AgentNotification?)null);
            // The durable inbox owns freshness and quiet-hour release.
            foreach (var notice in s.AgentOutbox.Values.Where(x => PushEligibility.Allowed(s, x, now, travelWhenPaused)))
                foreach (var subscription in s.PushSubscriptions.Values.Where(x => fingerprints.Contains(x.KeyFingerprint)))
                {
                    var id = AgentRuntime.StableId($"push-v1:{notice.Id}:{subscription.Id}");
                    s.PushDeliveries.TryAdd(id, new(id, notice.Id, subscription.Id, 0, "Pending", now));
                }
            var next = s.PushDeliveries.Values.OrderBy(x => x.AvailableAt).FirstOrDefault(x =>
                PushEligibility.IsReady(x, now) &&
                s.AgentOutbox.TryGetValue(x.NotificationId, out var note) && PushEligibility.Allowed(s, note, now, travelWhenPaused) &&
                s.PushSubscriptions.TryGetValue(x.SubscriptionId, out var subscription) && fingerprints.Contains(subscription.KeyFingerprint));
            if (next is null) return (Delivery: (PushDelivery?)null, Subscription: (BrowserPushSubscription?)null, Notice: (AgentNotification?)null);
            var owned = next with { Status = "Sending", Attempt = next.Attempt + 1, ClaimToken = Guid.NewGuid(), LeaseExpiresAt = now.AddSeconds(30) };
            s.PushDeliveries[owned.Id] = owned; return (Delivery: (PushDelivery?)owned, Subscription: (BrowserPushSubscription?)s.PushSubscriptions[owned.SubscriptionId], Notice: (AgentNotification?)s.AgentOutbox[owned.NotificationId]);
        }, ct);
        if (claim.Delivery is null) return false;
        var succeeded = false; var gone = false;
        try { await sender.SendAsync(claim.Subscription!, claim.Notice!, ct); succeeded = true; }
        catch (WebPushException error) { gone = error.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound; }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        await store.UpdateAsync(s =>
        {
            if (!s.PushDeliveries.TryGetValue(claim.Delivery.Id, out var current) || current.ClaimToken != claim.Delivery.ClaimToken) return false;
            if (gone) s.PushSubscriptions.Remove(current.SubscriptionId);
            s.PushDeliveries[current.Id] = current with { Status = succeeded ? "Delivered" : gone || current.Attempt >= 3 ? "Failed" : "Pending", ClaimToken = null, LeaseExpiresAt = null, AvailableAt = clock.GetUtcNow().AddSeconds(Math.Pow(2, current.Attempt) * 10) };
            return true;
        }, ct);
        return true;
    }
}

public sealed class PushWorker(PushNotifications notifications, IPrivacyReader privacy, ILogger<PushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var invocation = CoreInvocationContext.Enter(InvocationOrigin.Agent);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            try { var status = await privacy.StatusAsync(stoppingToken); if (status.Erased) continue; using var scope = PrivacyContext.Enter(status.Epoch); await notifications.DispatchOneAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Browser push delivery failed ({Type}).", error.GetType().Name); }
    }
}
