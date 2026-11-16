using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Device_receipt_commit_retries_and_privacy_removal_are_atomic()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var host = new PostgresApiFactory(database.ConnectionString);
        const string deviceId = "receipt-storage";
        var (grantId, secret) = await DeviceCommandTests.SetupAsync(host.Services, deviceId);
        var command = await DeviceCommandDeliveryTests.ApproveAsync(host.Services, deviceId, grantId);
        var dispatchId = Guid.NewGuid();
        await DeviceReceiptTests.DispatchAsync(host.Services, secret, dispatchId);
        var actor = await host.Services.GetRequiredService<DeviceCredentials>().AuthenticateAsync(secret, default);
        using var paired = DeviceCredentialContext.Enter(actor!);
        var receipt = DeviceReceiptTests.Sign(command.Id, dispatchId, DeviceReceiptKind.Succeeded, """{"accepted":true}""", secret);
        var proof = VerifiedDeviceReceipt.Verify(command.Id, receipt, secret);
        var broken = new DeviceReceiptService(host.Services.GetRequiredService<IDeviceCapabilityCatalog>(),
            new FailBeforeCoreCommit(host.Services.GetRequiredService<ICoreTransaction>()), TimeProvider.System);
        await Assert.ThrowsAsync<IOException>(() => broken.RecordAsync(proof));
        var reader = host.Services.GetRequiredService<IDeviceReceiptReader>();
        Assert.Empty(await reader.ListAsync(command.Id));
        var commands = host.Services.GetRequiredService<IDeviceCommandReader>();
        Assert.Equal(DeviceCommandStatus.Dispatched, (await commands.GetAsync(command.Id))!.Status);

        var service = host.Services.GetRequiredService<DeviceReceiptService>();
        var retries = await Task.WhenAll(service.RecordAsync(proof), service.RecordAsync(proof));
        Assert.Equal(retries[0], retries[1]);
        Assert.Equal(DeviceCommandStatus.Succeeded, (await commands.GetAsync(command.Id))!.Status);
        using var restarted = new PostgresApiFactory(database.ConnectionString);
        Assert.Equal(retries[0], Assert.Single(await restarted.Services.GetRequiredService<IDeviceReceiptReader>().ListAsync(command.Id)));
        var privacy = host.Services.GetRequiredService<IPrivacyStore>();
        var policy = new RetentionPolicy(ArtifactDays: 1);
        var asOf = DateTimeOffset.UtcNow.AddDays(2);
        var preview = await privacy.PreviewAsync(policy, asOf);
        Assert.Equal(1, preview.Eligible.DeviceReceipts);
        var removed = await privacy.RetainAsync(new(Guid.NewGuid(), asOf, preview.PolicyFingerprint, PrivacyData.RetentionPhrase), policy, asOf);
        Assert.Equal(1, removed.Removed.DeviceReceipts);
        Assert.Empty(await reader.ListAsync(command.Id));

        var next = await DeviceCommandDeliveryTests.ApproveAsync(host.Services, deviceId, grantId);
        var nextDispatch = Guid.NewGuid();
        await DeviceReceiptTests.DispatchAsync(host.Services, secret, nextDispatch);
        var nextReceipt = DeviceReceiptTests.Sign(next.Id, nextDispatch, DeviceReceiptKind.Acknowledged, """{"accepted":true}""", secret);
        await service.RecordAsync(VerifiedDeviceReceipt.Verify(next.Id, nextReceipt, secret));
        var status = await privacy.StatusAsync();
        await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), PrivacyData.Hash("fixture"), asOf);
        Assert.Equal(1, (await privacy.JobsAsync()).Single().Removed.DeviceReceipts);
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
        Assert.Empty(await db.DeviceReceipts.ToArrayAsync());
    }
}
