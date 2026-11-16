using System.Net;
using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jarvis.Tests;

public sealed class DeviceReceiptTests
{
    [Fact]
    public async Task Device_receipt_routes_verify_signature_dispatch_and_credential_then_advance_the_command()
    {
        using var host = new JarvisApiFactory();
        const string deviceId = "receipt-target";
        var (grantId, secret) = await DeviceCommandTests.SetupAsync(host.Services, deviceId);
        var (_, otherSecret) = await DeviceCommandTests.SetupAsync(host.Services, "receipt-other");
        using var owner = host.CreateAuthorisedClient();
        using var device = host.CreateClient();
        using var other = host.CreateClient();
        device.DefaultRequestHeaders.Add("X-Jarvis-Key", secret);
        other.DefaultRequestHeaders.Add("X-Jarvis-Key", otherSecret);
        var command = await DeviceCommandDeliveryTests.ApproveAsync(host.Services, deviceId, grantId);
        var path = $"/api/device-commands/{command.Id}/receipts";
        var dispatchId = Guid.NewGuid();
        var ack = Sign(command.Id, dispatchId, DeviceReceiptKind.Acknowledged, """{"accepted":true}""", secret);
        await ExpectAsync(device, path, ack, HttpStatusCode.Conflict);
        await DispatchAsync(host.Services, secret, dispatchId);
        await ExpectAsync(owner, path, ack, HttpStatusCode.Forbidden);
        await ExpectAsync(device, path, ack with { PayloadJson = """{"accepted":true,"detail":"altered"}""" }, HttpStatusCode.Unauthorized);
        await ExpectAsync(other, path, ack, HttpStatusCode.Unauthorized);
        await ExpectAsync(other, path, Sign(command.Id, dispatchId, DeviceReceiptKind.Acknowledged, ack.PayloadJson, otherSecret), HttpStatusCode.NotFound);
        await ExpectAsync(device, path, Sign(command.Id, Guid.NewGuid(), DeviceReceiptKind.Acknowledged, ack.PayloadJson, secret), HttpStatusCode.Conflict);
        await ExpectAsync(device, path, Sign(command.Id, dispatchId, DeviceReceiptKind.Acknowledged, """{"accepted":"true"}""", secret), HttpStatusCode.BadRequest);
        var accepted = await PostAsync(device, path, ack);
        Assert.Equal(accepted, await PostAsync(device, path, ack));
        Assert.Equal(DeviceCommandStatus.Acknowledged, (await host.Services.GetRequiredService<IDeviceCommandReader>().GetAsync(command.Id))!.Status);
        var invalid = Sign(command.Id, dispatchId, DeviceReceiptKind.Succeeded, """{"accepted":false}""", secret);
        await ExpectAsync(device, path, invalid, HttpStatusCode.BadRequest);
        var final = Sign(command.Id, dispatchId, DeviceReceiptKind.Failed, """{"accepted":false,"detail":"Failed"}""", secret);
        var finished = await PostAsync(device, path, final);
        var changed = final with { PayloadJson = """{ "accepted": false }""" };
        await ExpectAsync(device, path, changed with { Signature = VerifiedDeviceReceipt.Sign(command.Id, changed, secret) }, HttpStatusCode.Conflict);
        Assert.Equal(DeviceCommandStatus.Failed, (await host.Services.GetRequiredService<IDeviceCommandReader>().GetAsync(command.Id))!.Status);
        Assert.Equal(new[] { accepted, finished }, (await owner.GetFromJsonAsync<DeviceReceiptView[]>(path, LifeScenarioTests.Json))!);
        using var forbidden = await device.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Device_receipt_late_retry_is_idempotent_and_never_reopens_expired_work()
    {
        var clock = new ScenarioClock();
        using var baseHost = new JarvisApiFactory();
        using var host = baseHost.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); }));
        const string deviceId = "late-receipt";
        var (grantId, secret) = await DeviceCommandTests.SetupAsync(host.Services, deviceId);
        var command = await DeviceCommandDeliveryTests.ApproveAsync(host.Services, deviceId, grantId);
        var dispatchId = Guid.NewGuid();
        await DispatchAsync(host.Services, secret, dispatchId);
        var credentials = host.Services.GetRequiredService<DeviceCredentials>();
        using var scope = DeviceCredentialContext.Enter((await credentials.AuthenticateAsync(secret, default))!);
        var service = host.Services.GetRequiredService<DeviceReceiptService>();
        clock.Advance(TimeSpan.FromMinutes(3));
        var signed = Sign(command.Id, dispatchId, DeviceReceiptKind.Succeeded, """{"accepted":true}""", secret);
        var proof = VerifiedDeviceReceipt.Verify(command.Id, signed, secret);
        var late = await service.RecordAsync(proof);
        Assert.True(late.Late);
        Assert.Equal(late, await service.RecordAsync(proof));
        Assert.Equal(DeviceCommandStatus.Expired, (await host.Services.GetRequiredService<IDeviceCommandReader>().GetAsync(command.Id))!.Status);
        var replacement = await credentials.IssueAsync(deviceId, default);
        var stale = await Assert.ThrowsAsync<DomainException>(() => service.RecordAsync(proof));
        Assert.Equal(401, stale.StatusCode);
        using var rotated = DeviceCredentialContext.Enter((await credentials.AuthenticateAsync(replacement.Secret, default))!);
        var resubmitted = VerifiedDeviceReceipt.Verify(command.Id, Sign(command.Id, dispatchId, signed.Kind, signed.PayloadJson, replacement.Secret), replacement.Secret);
        var inaccessible = await Assert.ThrowsAsync<DomainException>(() => service.RecordAsync(resubmitted));
        Assert.Equal(404, inaccessible.StatusCode);
    }

    internal static SubmitDeviceReceiptRequest Sign(Guid commandId, Guid dispatchId, DeviceReceiptKind kind, string payload, string secret)
    {
        var request = new SubmitDeviceReceiptRequest(Guid.NewGuid(), dispatchId, kind, payload, "");
        return request with { Signature = VerifiedDeviceReceipt.Sign(commandId, request, secret) };
    }

    internal static async Task DispatchAsync(IServiceProvider services, string secret, Guid dispatchId)
    {
        var actor = await services.GetRequiredService<DeviceCredentials>().AuthenticateAsync(secret, default);
        using var scope = DeviceCredentialContext.Enter(actor!);
        Assert.NotNull(await services.GetRequiredService<DeviceCommandService>().ClaimAsync(new(dispatchId)));
    }

    private static async Task ExpectAsync(HttpClient client, string path, SubmitDeviceReceiptRequest request, HttpStatusCode expected)
    {
        using var response = await client.PostAsJsonAsync(path, request);
        Assert.Equal(expected, response.StatusCode);
    }

    private static async Task<DeviceReceiptView> PostAsync(HttpClient client, string path, SubmitDeviceReceiptRequest request)
    {
        using var response = await client.PostAsJsonAsync(path, request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeviceReceiptView>(LifeScenarioTests.Json))!;
    }
}
