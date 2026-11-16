using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Integrations;

public sealed class HomeAssistantCommandAdapter(ProviderGateway gateway, IConfiguration config, IPrivacyReader privacy,
    DeviceCredentials credentials, IDeviceGrantStore grants, DeviceCommandService commands, DeviceReceiptService receipts,
    ILogger<HomeAssistantCommandAdapter> logger) : IHomeControl
{
    public const string ExecutorDeviceId = "HomeAssistant:ExecutorDeviceId";
    public const string ExecutorCredential = "HomeAssistant:ExecutorCredential";

    public bool Configured => !string.IsNullOrWhiteSpace(config[ExecutorDeviceId]) ||
        !string.IsNullOrWhiteSpace(config[ExecutorCredential]);

    public Task<string> ExecuteAsync(string entityId, string action, CancellationToken ct) =>
        gateway.ExecuteAsync(entityId, action, ct);

    internal async Task<string> VoiceExecutorIdAsync(CancellationToken ct)
    {
        var executor = await ReadExecutorAsync(ct);
        Guard.Require(executor is not null, 503, "Configure a current paired credential for the Home Assistant executor.");
        return executor.Value.Actor.DeviceId!;
    }

    internal async Task ExecuteVoiceCommandAsync(Guid id, Func<CancellationToken, Task<bool>> currentAudio, CancellationToken ct)
    {
        var executor = await ReadExecutorAsync(ct);
        Guard.Require(executor is not null, 503, "Configure a current paired credential for the Home Assistant executor.");
        var (actor, secret) = executor.Value;
        using var device = DeviceCredentialContext.Enter(actor);
        var delivery = await commands.ClaimVoiceHomeCommandAsync(id, ct);
        if (delivery is not null) await ExecuteDeliveryAsync(delivery, secret, ct, currentAudio);
    }

    public async Task<string> ExecuteConfirmedAsync(Guid toolId, string entityId, string action, BudgetActor? owner, CancellationToken ct)
    {
        if (!Configured) return await ExecuteAsync(entityId, action, ct);
        var executor = await ReadExecutorAsync(ct);
        Guard.Require(executor is not null, 503, "Configure a current paired credential for the Home Assistant executor.");
        var (actor, secret) = executor.Value;
        using var device = DeviceCredentialContext.Enter(actor);
        var candidateGrants = (await grants.ListAsync(actor.DeviceId, ct))
            .Where(x => x.CapabilityId == BuiltInDeviceCapabilities.HomeAssistantTurn &&
                x.CapabilityVersion == BuiltInDeviceCapabilities.HomeAssistantTurnVersion && x.RevokedAt is null)
            .Select(x => x.Id).ToArray();
        var delivery = await commands.DispatchConfirmedHomeToolAsync(toolId, owner, candidateGrants, ct);
        return await ExecuteDeliveryAsync(delivery, secret, ct);
    }

    public async Task<bool> DispatchNextAsync(CancellationToken ct)
    {
        if (!Configured) return false;
        var status = await privacy.StatusAsync(ct);
        if (status.Erased) return false;
        using var epoch = PrivacyContext.Enter(status.Epoch);
        var executor = await ReadExecutorAsync(ct);
        if (executor is null) return false;
        var (actor, secret) = executor.Value;
        using var device = DeviceCredentialContext.Enter(actor);
        var delivery = await commands.ClaimHomeAssistantAsync(new(Guid.NewGuid()), ct);
        if (delivery is null) return false;
        await ExecuteDeliveryAsync(delivery, secret, ct);
        return true;
    }

    private async Task<(CoreActor Actor, string Secret)?> ReadExecutorAsync(CancellationToken ct)
    {
        var deviceId = config[ExecutorDeviceId];
        var secret = config[ExecutorCredential];
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(secret)) return null;
        var actor = await credentials.AuthenticateAsync(secret, ct);
        return actor?.DeviceId == deviceId ? (actor, secret) : null;
    }

    private async Task<string> ExecuteDeliveryAsync(DeliveredDeviceCommand delivery, string credential, CancellationToken ct,
        Func<CancellationToken, Task<bool>>? currentAudio = null)
    {
        var acknowledged = await RecordAsync(delivery, credential, DeviceReceiptKind.Acknowledged, null, ct);
        Guard.Require(!acknowledged.Late, 409, "The Home Assistant dispatch expired before execution.");
        string result;
        try
        {
            await commands.EnsureHomeAssistantExecutionAllowedAsync(delivery, ct);
            using var args = JsonDocument.Parse(delivery.ArgumentsJson);
            var entityId = args.RootElement.GetProperty("entityId").GetString()!;
            var action = args.RootElement.GetProperty("action").GetString()!;
            Guard.Require(currentAudio is null || await currentAudio(ct), 409, "The voice window no longer owns audio.");
            await ExecuteAsync(entityId, action, ct);
            result = await gateway.VerifyHomeStateAsync(entityId, action, ct);
        }
        catch (Exception error) when (error is DomainException or HttpRequestException or OperationCanceledException)
        {
            var detail = error is DomainException { StatusCode: not 502 } failure
                ? failure.Message
                : "Home Assistant did not confirm the outcome. Check the device before requesting another action.";
            try { await RecordAsync(delivery, credential, DeviceReceiptKind.Failed, detail, CancellationToken.None); }
            catch (Exception receiptError)
            {
                logger.LogWarning("Home Assistant failure receipt could not be persisted ({ErrorType}).", receiptError.GetType().Name);
            }
            throw;
        }

        DeviceReceiptView finished;
        try { finished = await RecordAsync(delivery, credential, DeviceReceiptKind.Succeeded, result, CancellationToken.None); }
        catch (Exception receiptError)
        {
            logger.LogWarning("Home Assistant completion receipt could not be persisted ({ErrorType}).", receiptError.GetType().Name);
            throw new DomainException(502, "Home Assistant responded but Core could not record the outcome. Check the device.");
        }
        Guard.Require(!finished.Late, 502, "Home Assistant responded after the dispatch expired. Check the device.");
        return result;
    }

    private Task<DeviceReceiptView> RecordAsync(DeliveredDeviceCommand delivery, string credential,
        DeviceReceiptKind kind, string? detail, CancellationToken ct)
    {
        var accepted = kind != DeviceReceiptKind.Failed;
        var payload = detail is null ? JsonSerializer.Serialize(new { accepted }) : JsonSerializer.Serialize(new { accepted, detail });
        var request = new SubmitDeviceReceiptRequest(Guid.NewGuid(), delivery.DispatchRequestId, kind, payload, "");
        var signed = request with { Signature = VerifiedDeviceReceipt.Sign(delivery.Id, request, credential) };
        return receipts.RecordAsync(VerifiedDeviceReceipt.Verify(delivery.Id, signed, credential), ct);
    }
}

public sealed class HomeAssistantCommandWorker(HomeAssistantCommandAdapter adapter, ILogger<HomeAssistantCommandWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        using var invocation = CoreInvocationContext.Enter(InvocationOrigin.Agent);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await adapter.DispatchNextAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (DomainException) { } // Expected authorization and provider failures are recorded when a command was dispatched.
            catch (Exception error) { logger.LogWarning("Home Assistant dispatch failed ({ErrorType}).", error.GetType().Name); }
        }
    }
}
