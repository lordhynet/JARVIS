using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Privacy_erasure_is_durable_across_hosts_and_settles_only_existing_charges()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var a = new PostgresApiFactory(database.ConnectionString); using var b = new PostgresApiFactory(database.ConnectionString);
        using var x = a.AuthorisedClient(); using var y = b.AuthorisedClient();
        await CreateSession(x);
        var privacy = a.Services.GetRequiredService<IPrivacyStore>();
        var runtime = b.Services.GetRequiredService<IRuntimeStore>();
        var budget = b.Services.GetRequiredService<UsageBudget>(); var id = Guid.NewGuid();
        using var old = PrivacyContext.Enter(0);
        await budget.ReserveAsync(id, Guid.NewGuid(), "fixture", .1m, new(1, 5, 5), default);
        var request = new EraseCoreRequest(Guid.NewGuid(), 1, PrivacyData.ErasePhrase);
        var erased = await privacy.EraseAsync(request, PrivacyData.Hash("old-key"), DateTimeOffset.UtcNow);
        Assert.Equal(erased, await privacy.EraseAsync(request, PrivacyData.Hash("old-key"), DateTimeOffset.UtcNow));
        Assert.True((await budget.SettleAsync(id, .2m, 100, 20, default)).ReconciliationRequired);
        await Assert.ThrowsAsync<DomainException>(() => runtime.UpdateAsync(s => { s.ProviderResults.Clear(); return true; }));
        using (PrivacyContext.Enter(erased.Epoch))
        {
            var exported = await b.Services.GetRequiredService<IPrivacyReader>().ExportAsync(DateTimeOffset.UtcNow);
            Assert.Empty(exported.Sessions); Assert.Empty(exported.ProviderResults); Assert.Equal(.2m, Assert.Single(exported.Usage).ActualUsd);
            await privacy.ReopenAsync(new(erased.Revision, PrivacyData.ReopenPhrase), PrivacyData.Hash("new-key"));
        }
        await Assert.ThrowsAsync<DomainException>(() => runtime.ReadAsync(s => s.Devices.Count));
        using var restarted = new PostgresApiFactory(database.ConnectionString); using var client = restarted.AuthorisedClient();
        var restored = await restarted.Services.GetRequiredService<IPrivacyReader>().StatusAsync();
        Assert.False(restored.Erased); Assert.Equal(2, restored.Epoch);
        using (PrivacyContext.Enter(restored.Epoch)) Assert.Empty((await restarted.Services.GetRequiredService<IPrivacyReader>().ExportAsync(DateTimeOffset.UtcNow)).Sessions);
    }
    [PostgresFact]
    public async Task Retention_is_atomic_and_idempotent_after_restart()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var host = new PostgresApiFactory(database.ConnectionString); using var client = host.AuthorisedClient();
        var sessions = host.Services.GetRequiredService<Jarvis.Orchestrator.Sessions.ISessionStore>();
        var now = DateTimeOffset.Parse("2026-11-13T12:00:00Z"); var id = Guid.NewGuid();
        await sessions.AddAsync(new(id, ActivityType.General, "Expired fixture", SessionStatus.Ended, null, null, now.AddDays(-400), now.AddDays(-400), []));
        var privacy = host.Services.GetRequiredService<IPrivacyStore>(); var policy = new RetentionPolicy();
        var preview = await privacy.PreviewAsync(policy, now); Assert.Equal(1, preview.Eligible.Sessions);
        var request = new ApplyRetentionRequest(Guid.NewGuid(), now, preview.PolicyFingerprint, PrivacyData.RetentionPhrase);
        await Assert.ThrowsAsync<DomainException>(() => privacy.RetainAsync(request with { Confirmation = "wrong" }, policy, now));
        Assert.NotNull(await sessions.GetAsync(id));
        var job = await privacy.RetainAsync(request, policy, now); Assert.Null(await sessions.GetAsync(id));
        using var restarted = new PostgresApiFactory(database.ConnectionString); using var other = restarted.AuthorisedClient();
        var restored = restarted.Services.GetRequiredService<IPrivacyStore>();
        Assert.Equal(job, await restored.RetainAsync(request, policy, now.AddMinutes(1)));
        await Assert.ThrowsAsync<DomainException>(() => restored.RetainAsync(request with { AsOf = now.AddMinutes(-1) }, policy, now));
        Assert.Contains(id, await restarted.Services.GetRequiredService<IRuntimeStore>().ReadAsync(state => state.RetiredSessions));
    }

}
