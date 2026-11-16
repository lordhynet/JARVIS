namespace Jarvis.Api;

public static class CoreRoutes
{
    public const string Devices = "/api/devices";
    public const string DeviceHeartbeatSuffix = "/{id}/heartbeat";
    public const string DeviceHeartbeat = Devices + DeviceHeartbeatSuffix;
    public const string DeviceCredentialSuffix = "/{id}/credential";
    public const string DeviceGrants = Devices + "/{id}/grants";
    public const string DeviceCapabilities = "/api/device-capabilities";

    public const string Privacy = "/api/privacy";
    public const string PrivacyStatusSuffix = "/status";
    public const string PrivacyStatus = Privacy + PrivacyStatusSuffix;

    public const string Voice = "/api/voice";
    public const string VoiceStatus = Voice + "/status";
    public const string VoicePrepare = Voice + "/prepare";
    public const string VoiceWakeCandidates = Voice + "/wake-candidates";
    public const string VoiceTickets = Voice + "/tickets";
    public const string VoiceCalibration = Voice + "/calibrate";
    public const string VoiceLocal = Voice + "/local";
    public const string VoiceLocalRenew = VoiceLocal + "/renew";
    public const string VoiceLocalRelease = VoiceLocal + "/release";
    public const string VoiceStream = "/voice-stream";

    public static IReadOnlyList<string> CompanionGet { get; } =
        Array.AsReadOnly(new[] { PrivacyStatus, VoiceStatus, Devices, DeviceCapabilities });

    // Device registration is available during owner-key bootstrap. Paired credentials use the other routes.
    public static IReadOnlyList<string> CompanionPost { get; } =
        Array.AsReadOnly(new[] { Devices, VoicePrepare, VoiceWakeCandidates, VoiceTickets, VoiceLocalRelease, DeviceHeartbeat });
}
