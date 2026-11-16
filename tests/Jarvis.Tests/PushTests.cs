using Jarvis.Api.Notifications;
using Jarvis.Api;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.Configuration;

namespace Jarvis.Tests;

public sealed class PushTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T12:00Z"); public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Sender : IPushSender
    {
        public int Count; public Task SendAsync(BrowserPushSubscription subscription, Guid noticeId, CancellationToken ct) { Interlocked.Increment(ref Count); return Task.CompletedTask; }
    }
    [Theory]
    [InlineData("https://127.0.0.1/push")]
    [InlineData("https://fcm.googleapis.com.evil.example/push")]
    [InlineData("https://fcm.googleapis.com:8443/push")]
    [InlineData("https://key@fcm.googleapis.com/push")]
    public void Subscription_destinations_cannot_be_arbitrary_fetch_targets(string url) => Assert.Throws<DomainException>(() => PushNotifications.ValidateEndpoint(url));

    [Fact]
    public async Task Delivery_obeys_quiet_hours_current_credentials_expiry_and_concurrent_deduplication()
    {
        var store = new InMemoryRuntimeStore(); var clock = new Clock(); var sender = new Sender();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Push:Enabled"] = "true", ["Push:PublicKey"] = "fixture", ["Push:PrivateKey"] = "fixture", [ConfigKeys.CoreApiKey] = "current-key" }).Build();
        var notifications = new PushNotifications(store, config, sender, clock);
        var id = Guid.NewGuid(); var subId = Guid.NewGuid();
        await store.UpdateAsync(s => { s.PushSubscriptions[subId] = new(subId, "https://fcm.googleapis.com/fcm/send/fixture", "fixture", "fixture", PrivacyData.Hash("current-key"), clock.Now);
            s.AgentOutbox[id] = new(id, Guid.NewGuid(), "Ready", clock.Now, clock.Now, clock.Now.AddDays(1), Delivered: false); return true; });
        Assert.False(await notifications.DispatchOneAsync(default));
        await store.UpdateAsync(s => { s.AgentOutbox[id] = s.AgentOutbox[id] with { Delivered = true }; return true; });
        Assert.True(await store.ReadAsync(s => PushEligibility.HasDispatchWork(s, clock.Now, PrivacyData.Hash("current-key"))));
        clock.Now = clock.Now.AddHours(11); Assert.False(await notifications.DispatchOneAsync(default));
        clock.Now = clock.Now.AddHours(-11); config[ConfigKeys.CoreApiKey] = "rotated-key";
        Assert.False(await notifications.DispatchOneAsync(default)); config[ConfigKeys.CoreApiKey] = "current-key";
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => notifications.DispatchOneAsync(default)));
        Assert.Equal(1, sender.Count); Assert.Equal("Delivered", Assert.Single(await store.ReadAsync(s => s.PushDeliveries.Values.ToList())).Status);
        Assert.False(await store.ReadAsync(s => PushEligibility.HasDispatchWork(s, clock.Now, PrivacyData.Hash("current-key"))));
        Assert.False(await notifications.DispatchOneAsync(default));
        await notifications.UnsubscribeAsync(subId, default); Assert.False(await notifications.DispatchOneAsync(default));
    }
}
