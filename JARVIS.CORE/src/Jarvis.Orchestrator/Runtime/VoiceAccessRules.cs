using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Runtime;

public static class VoiceAccessRules
{
    public const string BrowserCompanionTag = "browser-companion";

    public static bool RequiresLockReport(RegisteredDevice? device) => device is null || device.Registration.Kind == DeviceKind.WindowsAgent &&
        !(device.Registration.Capabilities.HasDisplay && device.Registration.Capabilities.Extra.Contains(BrowserCompanionTag, StringComparer.Ordinal));

    public static bool Restricted(RuntimeState state, Guid sessionId) =>
        state.AudioLease is { Locked: true } lease && lease.SessionId == sessionId;

    public static bool ReportedState(RuntimeState state, string deviceId, bool? locked) =>
        locked ?? RequiresLockReport(state.Devices.GetValueOrDefault(deviceId));
}
