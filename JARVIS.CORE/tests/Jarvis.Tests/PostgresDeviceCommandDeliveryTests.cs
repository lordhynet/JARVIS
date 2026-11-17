using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Device_command_claim_is_atomic_under_concurrency_and_recoverable_after_restart()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string deviceId = "durable-dispatch";
        CoreActor actor;
        DeliveredDeviceCommand dispatched;
        var request = new ClaimDeviceCommandRequest(Guid.NewGuid());
        using (var host = new PostgresApiFactory(database.ConnectionString))
        {
            var (grantId, secret) = await DeviceCommandTests.SetupAsync(host.Services, deviceId);
            var approved = await DeviceCommandDeliveryTests.ApproveAsync(host.Services, deviceId, grantId);
            actor = (await host.Services.GetRequiredService<DeviceCredentials>().AuthenticateAsync(secret, default))!;
            using var paired = DeviceCredentialContext.Enter(actor);
            var reader = host.Services.GetRequiredService<IDeviceCommandReader>();
            var failing = new DeviceCommandService(host.Services.GetRequiredService<IDeviceCapabilityCatalog>(),
                new FailBeforeCoreCommit(host.Services.GetRequiredService<ICoreTransaction>()), TimeProvider.System);
            await Assert.ThrowsAsync<IOException>(() => failing.ClaimAsync(request));
            var untouched = (await reader.GetAsync(approved.Id))!;
            Assert.Equal(DeviceCommandStatus.Pending, untouched.Status);
            Assert.Null(untouched.DispatchRequestId);

            var service = host.Services.GetRequiredService<DeviceCommandService>();
            var competitors = await Task.WhenAll(service.ClaimAsync(request), service.ClaimAsync(new(Guid.NewGuid())));
            // The two claims race for one pending row. Recover using the request that won the dispatch.
            dispatched = Assert.Single(competitors.OfType<DeliveredDeviceCommand>());
            Assert.Equal(approved.Id, dispatched.Id);
            var stored = (await reader.GetAsync(approved.Id))!;
            Assert.Equal(DeviceCommandStatus.Dispatched, stored.Status);
            Assert.Equal(dispatched.DispatchedAt, stored.DispatchedAt);
            request = new(stored.DispatchRequestId!.Value);
            var retries = await Task.WhenAll(service.ClaimAsync(request), service.ClaimAsync(request));
            Assert.All(retries, claim => Assert.Equal(dispatched, claim));
        }

        using var restarted = new PostgresApiFactory(database.ConnectionString);
        using var device = DeviceCredentialContext.Enter(actor);
        var recovered = restarted.Services.GetRequiredService<DeviceCommandService>();
        Assert.Equal(dispatched, await recovered.ClaimAsync(request));
        Assert.Null(await recovered.ClaimAsync(new(Guid.NewGuid())));
    }

    [PostgresFact]
    public async Task Device_command_claim_skips_ineligible_older_pages_without_leaking_or_starving_valid_commands()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new ScenarioClock();
        using var baseHost = new PostgresApiFactory(database.ConnectionString);
        using var host = baseHost.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); }));
        const string deviceId = "dispatch-page";
        var (invalidGrant, secret) = await DeviceCommandTests.SetupAsync(host.Services, deviceId);
        var grants = host.Services.GetRequiredService<IDeviceGrantStore>();
        var original = (await grants.GetAsync(invalidGrant))!;
        var validGrant = await grants.AddAsync(original with { Id = Guid.NewGuid() });
        var invalidCommand = await DeviceCommandDeliveryTests.ApproveAsync(host.Services, deviceId, invalidGrant);
        var template = (await host.Services.GetRequiredService<IDeviceCommandReader>().GetAsync(invalidCommand.Id))!;
        var rejected = Enumerable.Range(0, ICoreTransactionWrites.DeviceCommandBatchSize)
            .Select(index => new Guid(index + 1, 0, 0, new byte[8])).ToArray();
        var tx = host.Services.GetRequiredService<ICoreTransaction>();
        await tx.ExecuteAsync(async writes =>
        {
            foreach (var id in rejected) await writes.SaveDeviceCommandAsync(template with { Id = id });
            return true;
        });
        var good = await DeviceCommandDeliveryTests.ApproveAsync(host.Services, deviceId, validGrant.Id);
        Assert.Equal(template.CreatedAt, good.CreatedAt);
        Assert.True(await grants.RevokeAsync(invalidGrant, clock.GetUtcNow()));
        var actor = await host.Services.GetRequiredService<DeviceCredentials>().AuthenticateAsync(secret, default);
        using var paired = DeviceCredentialContext.Enter(actor!);
        var service = host.Services.GetRequiredService<DeviceCommandService>();
        Assert.Equal(good.Id, (await service.ClaimAsync(new(Guid.NewGuid())))!.Id);
        Assert.Null(await service.ClaimAsync(new(Guid.NewGuid())));
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
        Assert.Equal(good.Id, (await db.DeviceCommands.SingleAsync(x => x.DispatchedAt != null)).Id);
    }
}
