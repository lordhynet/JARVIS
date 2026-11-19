package uk.jarvis.mobile

import android.content.Context
import uk.jarvis.mobile.contracts.*

internal class RecordingCoreTransport private constructor(val connection: SavedConnection, val deviceId: String, val epoch: Long, private val api: CoreApi) {
    val binding get() = RecordingQueue.binding(connection.url, deviceId, epoch)
    suspend fun send(request: RecordingCaptureRequest): RecordingCaptureResult {
        check(request.deviceId == deviceId)
        return api.send(CoreCompanionRoutes.RECORDING_CAPTURE, request, RecordingCaptureResult::class.java)
    }
    companion object {
        suspend fun connect(context: Context): RecordingCoreTransport {
            val connection = CredentialVault(context).read() ?: error("Connect this phone first.")
            check(Regex("jdv1_[a-fA-F0-9]{64}").matches(connection.token)) { "Pair this phone with its own QR credential before recording." }
            val api = CoreApi(connection.url, connection.token, connection.certificateSha256)
            val privacy = api.get(CoreCompanionRoutes.PRIVACY_STATUS, PrivacyStatus::class.java)
            if (privacy.erased) throw CorePrivacyException(eraseLegacy = true)
            api.bindEpoch(privacy.epoch)
            val devices = api.list(CoreCompanionRoutes.DEVICES, DeviceView::class.java)
            check(devices.size == 1 && devices[0].device.registration.kind == DeviceKind.AndroidCompanion && !devices[0].device.registration.capabilities.hasDisplay) { "Use this audio companion's own scoped credential." }
            return RecordingCoreTransport(connection, devices[0].device.registration.deviceId, privacy.epoch, api)
        }
    }
}
