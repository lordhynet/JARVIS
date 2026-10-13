namespace Jarvis.Contracts;

/// <summary>The kinds of client that can connect to JARVIS Core.</summary>
public enum DeviceKind
{
    WindowsAgent,
    VoiceSatellite,
    AndroidCompanion,
    QuestClient,
    HomeAssistant
}

/// <summary>
/// What a device says it can do. JARVIS only routes work to a capability a device
/// has declared AND that has been verified on real hardware.
/// </summary>
public sealed record DeviceCapabilities(
    bool CanListen,
    bool CanSpeak,
    bool HasDisplay,
    bool CanControlHome,
    IReadOnlyList<string> Extra);

/// <summary>A device that has authenticated and registered with JARVIS Core.</summary>
public sealed record DeviceRegistration(
    string DeviceId,
    string Name,
    DeviceKind Kind,
    DeviceCapabilities Capabilities,
    DateTimeOffset RegisteredAt);
