namespace Jarvis.Api;

public static class CoreRoutes
{
    public const string IntelligenceEvaluations = "/api/intelligence/evaluations";
    public const string Devices = Jarvis.Contracts.CompanionRoutes.Devices;
    public const string DeviceHeartbeatSuffix = Jarvis.Contracts.CompanionRoutes.DeviceHeartbeatSuffix;
    public const string DeviceHeartbeat = Devices + DeviceHeartbeatSuffix;
    public const string DeviceCredentialSuffix = "/{id}/credential";
    public const string DeviceGrants = Devices + "/{id}/grants";
    public const string DeviceCommandProposals = Devices + "/{id}/commands";
    public const string DeviceCommandClaim = "/api/device-commands/claim";
    public const string DeviceCommands = "/api/device-commands";
    public const string DeviceCommand = DeviceCommands + "/{id:guid}";
    public const string DeviceCommandConfirm = DeviceCommand + "/confirm";
    public const string DeviceCommandReceipts = DeviceCommand + "/receipts";
    public const string DeviceCapabilities = Jarvis.Contracts.CompanionRoutes.DeviceCapabilities;

    public const string Privacy = "/api/privacy";
    public const string PrivacyStatusSuffix = "/status";
    public const string PrivacyStatus = Jarvis.Contracts.CompanionRoutes.PrivacyStatus;

    public const string Voice = "/api/voice";
    public const string VoiceStatus = Jarvis.Contracts.CompanionRoutes.VoiceStatus;
    public const string VoiceModes = Voice + "/modes";
    public const string VoicePrepare = Jarvis.Contracts.CompanionRoutes.VoicePrepare;
    public const string VoiceHandoff = Jarvis.Contracts.CompanionRoutes.VoiceHandoff;
    public const string VoiceWakeCandidates = Jarvis.Contracts.CompanionRoutes.VoiceWakeCandidates;
    public const string VoiceTickets = Jarvis.Contracts.CompanionRoutes.VoiceTickets;
    public const string VoiceCalibration = Voice + "/calibrate";
    public const string VoiceLocal = Voice + "/local";
    public const string VoiceLocalRenew = VoiceLocal + "/renew";
    public const string VoiceLocalRelease = Jarvis.Contracts.CompanionRoutes.VoiceLocalRelease;
    public const string VoiceStream = Jarvis.Contracts.CompanionRoutes.VoiceStream;

    public static IReadOnlyList<string> CompanionGet { get; } =
        Array.AsReadOnly(new[] { PrivacyStatus, VoiceStatus, Devices, DeviceCapabilities });

    // Device registration is available during owner-key bootstrap. Paired credentials use the other routes.
    public static IReadOnlyList<string> CompanionPost { get; } =
        Array.AsReadOnly(new[] { Devices, VoicePrepare, VoiceWakeCandidates, VoiceTickets, VoiceLocalRelease, DeviceHeartbeat, Jarvis.Contracts.RecordingProtocol.Capture, VoiceHandoff });

    // Command executors use paired credentials. This contract is separate from the Android audio client.
    public static IReadOnlyList<string> CompanionCommandPost { get; } = Array.AsReadOnly(new[] { DeviceCommandClaim, DeviceCommandReceipts });
}
