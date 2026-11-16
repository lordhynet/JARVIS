namespace Jarvis.Contracts;

public enum DeviceReceiptKind { Acknowledged = 1, Succeeded = 2, Failed = 3 }

public sealed record SubmitDeviceReceiptRequest(Guid RequestId, Guid DispatchRequestId, DeviceReceiptKind Kind,
    string PayloadJson, string Signature);

public sealed record DeviceReceiptView(Guid Id, Guid CommandId, Guid DispatchRequestId, DeviceReceiptKind Kind,
    string PayloadJson, DateTimeOffset ReceivedAt, bool Late);
