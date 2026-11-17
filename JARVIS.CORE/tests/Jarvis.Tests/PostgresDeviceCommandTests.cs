using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Device_command_review_prioritizes_active_proposals_and_uses_a_bounded_history()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var host = new PostgresApiFactory(database.ConnectionString);
        const string deviceId = "persistent-command-review";
        var (grantId, _) = await DeviceCommandTests.SetupAsync(host.Services, deviceId);
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var service = host.Services.GetRequiredService<DeviceCommandService>();
        var now = DateTimeOffset.UtcNow;
        var pending = await service.ProposeAsync(deviceId, DeviceCommandTests.Request(grantId, now));
        var transactions = host.Services.GetRequiredService<ICoreTransaction>();
        await transactions.ExecuteAsync(async writes =>
        {
            var earlier = (await writes.ReadDeviceCommandAsync(pending.Id))!;
            for (var index = 0; index < IDeviceCommandReader.ReviewLimit; index++)
                await writes.SaveDeviceCommandAsync(earlier with { Id = Guid.NewGuid(), CreatedAt = now.AddSeconds(index),
                    Status = DeviceCommandStatus.Cancelled });
            return true;
        });
        var review = await host.Services.GetRequiredService<IDeviceCommandReader>().ListForReviewAsync(now);
        Assert.Equal(IDeviceCommandReader.ReviewLimit, review.Count);
        Assert.Equal(pending.Id, review[0].Id);
    }

    [PostgresFact]
    public async Task Device_command_replays_are_atomic_and_survive_restart_with_exact_arguments()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string deviceId = "persistent-command";
        ProposeDeviceCommandRequest request;
        DeviceCommandView approved;
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var actor = new BudgetActor("fixture-owner", [DeviceCommandService.ConfirmScope]);
        using (var host = new PostgresApiFactory(database.ConnectionString))
        {
            var (grantId, _) = await DeviceCommandTests.SetupAsync(host.Services, deviceId);
            request = DeviceCommandTests.Request(grantId, DateTimeOffset.UtcNow);
            var reader = host.Services.GetRequiredService<IDeviceCommandReader>();
            var failing = new DeviceCommandService(host.Services.GetRequiredService<IDeviceCapabilityCatalog>(),
                new FailBeforeCoreCommit(host.Services.GetRequiredService<ICoreTransaction>()), TimeProvider.System);
            await Assert.ThrowsAsync<IOException>(() => failing.ProposeAsync(deviceId, request));
            Assert.Null(await reader.GetAsync(request.RequestId));

            var service = host.Services.GetRequiredService<DeviceCommandService>();
            var proposals = await Task.WhenAll(service.ProposeAsync(deviceId, request), service.ProposeAsync(deviceId, request));
            Assert.Equal(proposals[0], proposals[1]);
            var decision = new ConfirmDeviceCommandRequest(proposals[0].ArgumentHash, true);
            await Assert.ThrowsAsync<IOException>(() => failing.ConfirmAsync(request.RequestId, decision, actor));
            Assert.Equal(DeviceCommandStatus.AwaitingConfirmation, (await reader.GetAsync(request.RequestId))!.Status);
            var approvals = await Task.WhenAll(service.ConfirmAsync(request.RequestId, decision, actor),
                service.ConfirmAsync(request.RequestId, decision, actor));
            approved = approvals[0];
            Assert.Equal(approved, approvals[1]);
            Assert.Equal(DeviceCommandStatus.Pending, approved.Status);
        }

        using var restarted = new PostgresApiFactory(database.ConnectionString);
        var persisted = await restarted.Services.GetRequiredService<IDeviceCommandReader>().GetAsync(request.RequestId);
        Assert.NotNull(persisted);
        Assert.Equal(approved, persisted.View(DateTimeOffset.UtcNow));
        Assert.Equal(DeviceCommandTests.Arguments, persisted.ArgumentsJson);
        Assert.Equal(approved, await restarted.Services.GetRequiredService<DeviceCommandService>().ProposeAsync(deviceId, request));
        await using var db = await restarted.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
        Assert.Equal(request.RequestId, (await db.DeviceCommands.SingleAsync()).Id);
    }

    [PostgresFact]
    public async Task Device_command_privacy_preview_retention_and_erasure_cover_relational_rows()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var host = new PostgresApiFactory(database.ConnectionString);
        const string deviceId = "command-privacy";
        var (grantId, _) = await DeviceCommandTests.SetupAsync(host.Services, deviceId);
        var service = host.Services.GetRequiredService<DeviceCommandService>();
        var now = DateTimeOffset.UtcNow;
        var command = await service.ProposeAsync(deviceId, DeviceCommandTests.Request(grantId, now));
        var privacy = host.Services.GetRequiredService<IPrivacyStore>();
        var policy = new RetentionPolicy(ArtifactDays: 1);
        Assert.Equal(0, (await privacy.PreviewAsync(policy, now)).Eligible.DeviceCommands);
        var asOf = now.AddDays(2);
        var preview = await privacy.PreviewAsync(policy, asOf);
        Assert.Equal(1, preview.Eligible.DeviceCommands);
        var retained = await privacy.RetainAsync(new(Guid.NewGuid(), asOf, preview.PolicyFingerprint, PrivacyData.RetentionPhrase), policy, asOf);
        Assert.Equal(1, retained.Removed.DeviceCommands);
        Assert.Null(await host.Services.GetRequiredService<IDeviceCommandReader>().GetAsync(command.Id));

        await service.ProposeAsync(deviceId, DeviceCommandTests.Request(grantId, now));
        var status = await privacy.StatusAsync();
        var erased = await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase),
            PrivacyData.Hash("fixture-owner-key"), asOf, default);
        Assert.True(erased.Erased);
        Assert.Equal(1, (await privacy.JobsAsync()).Single().Removed.DeviceCommands);
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
        Assert.Empty(await db.DeviceCommands.ToArrayAsync());
    }
}
