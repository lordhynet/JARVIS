// Generated from Jarvis.Contracts by Jarvis.ContractGen. Do not edit.
package uk.jarvis.mobile.contracts

data class AiCapabilityStatus(
    val ready: Boolean = false,
    val model: String = "",
    val detail: String = "",
)

data class AudioLease(
    val sessionId: String = "",
    val deviceId: String = "",
    val fence: Long = 0L,
    val expiresAt: String = "",
)

data class CompanionVoiceRequest(
    val requestId: String = "",
    val deviceId: String = "",
    val allowCloudProcessing: Boolean = false,
)

data class CompanionVoiceSession(
    val sessionId: String = "",
)

data class DeviceCapabilities(
    val canListen: Boolean = false,
    val canSpeak: Boolean = false,
    val hasDisplay: Boolean = false,
    val canControlHome: Boolean = false,
    val extra: List<String> = emptyList(),
)

enum class DeviceKind { WindowsAgent, VoiceSatellite, AndroidCompanion, QuestClient, HomeAssistant }

data class DeviceRegistration(
    val deviceId: String = "",
    val name: String = "",
    val kind: DeviceKind = DeviceKind.WindowsAgent,
    val capabilities: DeviceCapabilities = DeviceCapabilities(),
    val registeredAt: String = "",
)

data class DeviceView(
    val device: RegisteredDevice = RegisteredDevice(),
    val online: Boolean = false,
)

data class PrivacyStatus(
    val epoch: Long = 0L,
    val revision: Int = 0,
    val erased: Boolean = false,
    val erasedAt: String? = null,
    val erasureRequestId: String? = null,
)

data class RegisterDeviceRequest(
    val deviceId: String = "",
    val name: String = "",
    val kind: DeviceKind = DeviceKind.WindowsAgent,
    val capabilities: DeviceCapabilities = DeviceCapabilities(),
)

data class RegisteredDevice(
    val registration: DeviceRegistration = DeviceRegistration(),
    val lastSeenAt: String = "",
    val capabilitiesVerified: Boolean = false,
)

data class VoiceStartRequest(
    val sessionId: String = "",
    val deviceId: String = "",
    val allowCloudProcessing: Boolean = false,
)

data class VoiceTicket(
    val ticket: String = "",
    val lease: AudioLease = AudioLease(),
    val windowSeconds: Int = 0,
    val sampleRate: Int = 0,
)

data class WakeCandidateRequest(
    val requestId: String = "",
    val deviceId: String = "",
    val allowCloudProcessing: Boolean = false,
)

data class WakeCandidateResponse(
    val sessionId: String = "",
    val voice: VoiceTicket = VoiceTicket(),
)

