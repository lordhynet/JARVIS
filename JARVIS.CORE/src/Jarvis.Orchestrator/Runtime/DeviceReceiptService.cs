using System.Security.Cryptography;
using System.Text;
using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Runtime;

public sealed class VerifiedDeviceReceipt
{
    private VerifiedDeviceReceipt(CoreActor actor, Guid commandId, SubmitDeviceReceiptRequest request)
    {
        Actor = actor;
        CommandId = commandId;
        Request = request;
    }

    internal CoreActor Actor { get; }
    internal Guid CommandId { get; }
    internal SubmitDeviceReceiptRequest Request { get; }

    public static VerifiedDeviceReceipt Verify(Guid commandId, SubmitDeviceReceiptRequest request, string? credential)
    {
        var actor = DeviceCredentialContext.RequirePairedActor();
        Guard.Require(commandId != Guid.Empty && request.RequestId != Guid.Empty && request.DispatchRequestId != Guid.Empty &&
            Enum.IsDefined(request.Kind) && request.PayloadJson is { Length: > 0 and <= 8000 }, 400, "Invalid device receipt envelope.");
        Span<byte> signature = stackalloc byte[32];
        Guard.Require(credential is not null && PrivacyData.Hash(credential) == actor.CredentialHash &&
            request.Signature is { Length: 64 } && Convert.FromHexString(request.Signature, signature, out _, out _) == System.Buffers.OperationStatus.Done &&
            CryptographicOperations.FixedTimeEquals(signature, Mac(commandId, request, credential)), 401, "Invalid device receipt signature.");
        return new(actor, commandId, request);
    }

    public static string Sign(Guid commandId, SubmitDeviceReceiptRequest request, string credential) =>
        Convert.ToHexString(Mac(commandId, request, credential));

    private static byte[] Mac(Guid commandId, SubmitDeviceReceiptRequest request, string credential)
    {
        var message = $"jarvis-device-receipt-v1\n{commandId:D}\n{request.DispatchRequestId:D}\n{request.RequestId:D}\n{request.Kind}\n{PrivacyData.Hash(request.PayloadJson)}";
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(credential), Encoding.UTF8.GetBytes(message));
    }
}

public interface IDeviceReceiptReader
{
    Task<IReadOnlyList<DeviceReceiptView>> ListAsync(Guid commandId, CancellationToken ct = default);
}

public sealed class DeviceReceiptService(IDeviceCapabilityCatalog capabilities, ICoreTransaction transaction, TimeProvider clock, DeviceCommandService? commandPolicy = null)
{
    public Task<DeviceReceiptView> RecordAsync(VerifiedDeviceReceipt receipt, CancellationToken ct = default)
    {
        var actor = DeviceCredentialContext.RequirePairedActor();
        Guard.Require(actor == receipt.Actor, 403, "The receipt belongs to a different device credential.");
        var request = receipt.Request;
        return transaction.ExecuteAsync(async writes =>
        {
            DeviceCredentialContext.RequireCurrent(writes.Runtime, actor.DeviceId!);
            var command = await writes.ReadDeviceCommandAsync(receipt.CommandId);
            Guard.Require(command is not null && command.DeviceId == actor.DeviceId && command.CredentialHash == actor.CredentialHash,
                404, "Device command was not found.");
            Guard.Require(command.DispatchedAt is not null && command.DispatchRequestId == request.DispatchRequestId,
                409, "Receipt does not match a dispatch for this command.");
            if (WindowsDeviceCapabilities.Installed(command.CapabilityId, command.CapabilityVersion) && request.Kind == DeviceReceiptKind.Acknowledged)
                Guard.Require(commandPolicy is not null && await commandPolicy.ReadDeliveryCapabilityAsync(writes, command) is not null,
                    403, "Windows execution acknowledgement requires a current command and grant.");

            var previous = await writes.ReadDeviceReceiptsAsync(command.Id);
            var replay = previous.SingleOrDefault(x => x.Id == request.RequestId);
            if (replay is not null)
            {
                Guard.Require(replay.Kind == request.Kind && replay.PayloadJson == request.PayloadJson,
                    409, "Receipt request ID was used for different input.");
                return replay;
            }

            var final = request.Kind != DeviceReceiptKind.Acknowledged;
            Guard.Require(previous.All(x => (x.Kind != DeviceReceiptKind.Acknowledged) != final),
                409, "The command already has a receipt for this phase.");
            Guard.Require(capabilities.AcceptsReceipt(command.CapabilityId, command.CapabilityVersion, request.Kind, request.PayloadJson),
                400, "Receipt payload does not match the capability schema or outcome.");

            var now = clock.GetUtcNow();
            var status = command.StatusAt(now);
            var late = status == DeviceCommandStatus.Expired;
            Guard.Require(late || status == DeviceCommandStatus.Dispatched || (final && status == DeviceCommandStatus.Acknowledged),
                409, "The command no longer accepts this receipt.");
            var receivedAt = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
            var saved = new DeviceReceiptView(request.RequestId, command.Id, request.DispatchRequestId, request.Kind,
                request.PayloadJson, receivedAt, late);
            await writes.AddDeviceReceiptAsync(saved);
            var updated = late ? DeviceCommandStatus.Expired : request.Kind switch
            {
                DeviceReceiptKind.Acknowledged => DeviceCommandStatus.Acknowledged,
                DeviceReceiptKind.Succeeded => DeviceCommandStatus.Succeeded,
                _ => DeviceCommandStatus.Failed
            };
            if (updated != command.Status)
                await writes.SaveDeviceCommandAsync(command with { Status = updated });
            return saved;
        }, ct);
    }
}
