using Jarvis.Api;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs.Integrations;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.Extensions.Configuration;

namespace Jarvis.Tests;

public sealed class CalendarAutomationTests
{
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-13T12:00Z"); }
    private sealed class Calendar : IIntegrationReader
    {
        public List<CalendarEntry> Entries = []; public bool Fail;
        public IReadOnlyList<IntegrationStatus> Status() => [];
        public Task<IReadOnlyList<TransitLine>> TransitAsync(string? lines, CancellationToken ct) => Task.FromResult<IReadOnlyList<TransitLine>>([]);
        public Task<IReadOnlyList<CalendarEntry>> CalendarAsync(string provider, DateTimeOffset start, DateTimeOffset end, CancellationToken ct) => Fail ? throw new HttpRequestException("fixture") : Task.FromResult<IReadOnlyList<CalendarEntry>>(Entries);
    }
    [Fact]
    public async Task Calendar_reschedule_supersedes_old_brief_and_outage_does_not_cancel_events()
    {
        var clock = new Clock(); var store = new InMemoryRuntimeStore(); var runtime = new AgentRuntime(store, clock); var provider = new Calendar();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Automation:Calendar:Enabled"] = "true" }).Build();
        var sync = new CalendarTriggerSync(provider, store, runtime, config, clock);
        provider.Entries = [new("stable-event", "Backend interview", "2026-11-14T10:00Z", "2026-11-14T11:00Z", false, "google")];
        Assert.Equal(1, await sync.SyncAsync(default)); Assert.Equal(0, await sync.SyncAsync(default));
        await runtime.TickAsync(default); var prior = Assert.Single(await store.ReadAsync(s => s.AgentRuns.Values.ToList()));
        provider.Entries = [provider.Entries[0] with { Start = "2026-11-14T11:00Z" }];
        Assert.Equal(1, await sync.SyncAsync(default)); Assert.Equal("Cancelled", await store.ReadAsync(s => s.AgentRuns[prior.Id].Status));
        provider.Fail = true; await Assert.ThrowsAsync<HttpRequestException>(() => sync.SyncAsync(default));
        Assert.Equal("Pending", (Assert.Single(await store.ReadAsync(s => s.AgentTriggers.Values.ToList()))).Status);
        provider.Fail = false; provider.Entries = []; Assert.Equal(1, await sync.SyncAsync(default));
        Assert.Equal("Cancelled", (Assert.Single(await store.ReadAsync(s => s.AgentTriggers.Values.ToList()))).Status);
    }
}
