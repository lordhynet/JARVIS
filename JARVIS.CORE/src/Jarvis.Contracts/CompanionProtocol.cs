namespace Jarvis.Contracts;

public static class CompanionVoiceProtocol
{
    public const int Version = 1;
    public const int SampleRate = 24000;
    public const string AudioOnly = "audio-only";
    public const string NativeWindows = "native-windows";
}

// Shared by Core, the Windows client and the existing Kotlin contract generator.
public static class CompanionRoutes
{
    public const string Devices = "/api/devices";
    public const string DeviceHeartbeatSuffix = "/{id}/heartbeat";
    public const string DeviceHeartbeat = Devices + DeviceHeartbeatSuffix;
    public const string DeviceCapabilities = "/api/device-capabilities";
    public const string PrivacyStatus = "/api/privacy/status";
    public const string VoiceStatus = "/api/voice/status";
    public const string VoicePrepare = "/api/voice/prepare";
    public const string VoiceWakeCandidates = "/api/voice/wake-candidates";
    public const string VoiceTickets = "/api/voice/tickets";
    public const string VoiceLocalRelease = "/api/voice/local/release";
    public const string VoiceStream = "/voice-stream";
    public static string Heartbeat(string id) => Devices + "/" + Uri.EscapeDataString(id) + "/heartbeat";
}

public static class CompanionHeaders
{
    public const string ApiKey = "X-Jarvis-Key";
    public const string DataEpoch = "X-Jarvis-Data-Epoch";
}
