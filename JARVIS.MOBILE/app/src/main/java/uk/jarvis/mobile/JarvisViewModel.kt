package uk.jarvis.mobile

import android.app.Application
import android.content.Intent
import android.net.Uri
import android.os.Build
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.google.gson.JsonObject
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import uk.jarvis.mobile.contracts.*
import java.util.UUID

data class ScreenState(
    val connected: Boolean = false, val busy: Boolean = false, val notice: String = "",
    val url: String = "", val pairingOffered: Boolean = false, val offline: Boolean = false,
    val audioStarting: Boolean = false, val privacyRevoked: Boolean = false,
    val verified: Boolean = false, val voice: AiCapabilityStatus? = null, val legacyDrafts: Boolean = false
)

/** Connection/device/audio transport only. Conversation and feature state live on Core. */
class JarvisViewModel(application: Application) : AndroidViewModel(application) {
    private val vault = CredentialVault(application)
    private val mutable = MutableStateFlow(ScreenState())
    val state = mutable.asStateFlow()
    private var api: CoreApi? = null
    private var connection: SavedConnection? = null
    private var offer: SavedConnection? = null
    private var preparationId: String? = null
    private val prefs = application.getSharedPreferences("installation", 0)
    private var deviceId = prefs.getString("id", null) ?: ("android-" + UUID.randomUUID()).also { prefs.edit().putString("id", it).apply() }
    init {
        application.cacheDir.listFiles()?.filter { it.name.startsWith("answer-") }?.forEach { it.delete() }
        vault.read()?.let { connect(it.url, it.token, it.certificateSha256) }
    }

    fun offerPairing(encoded: String) {
        runCatching { Provisioning.parse(encoded) }.onSuccess { connection ->
            offer = connection
            mutable.update { it.copy(pairingOffered = true, notice = "") }
        }.onFailure { reportAudioIssue("Pairing details were invalid. Send them again from your trusted Core computer.") }
    }
    fun acceptPairing() { offer?.let { connect(it.url, it.token, it.certificateSha256) } }
    fun connect(url: String, token: String, pin: String? = null) {
        if (vault.read() != SavedConnection(url.trim().trimEnd('/'), token.trim(), pin)) stopAudio()
        api?.invalidate(); viewModelScope.coroutineContext.cancelChildren(); api = null; connection = null; preparationId = null
        viewModelScope.launch {
            mutable.value = ScreenState(busy = true, pairingOffered = offer != null)
            var client: CoreApi? = null
            try {
                require(token.isNotBlank()) { "Enter the Core connection key." }
                val saved = SavedConnection(CoreAddress.validate(url), token.trim(), pin)
                client = CoreApi(saved.url, saved.token, saved.certificateSha256); api = client; connection = saved
                val privacy = client.get(CoreCompanionRoutes.PRIVACY_STATUS, PrivacyStatus::class.java); client.bindEpoch(privacy.epoch)
                val devices = client.list(CoreCompanionRoutes.DEVICES, DeviceView::class.java)
                if (saved.token.startsWith("jdv1_")) {
                    val enrolled = devices.singleOrNull() ?: error("Ask the owner to enrol this audio companion on Core.")
                    deviceId = enrolled.device.registration.deviceId
                    prefs.edit().putString("id", deviceId).apply()
                }
                val existing = devices.firstOrNull { it.device.registration.deviceId == deviceId }
                if (CompanionRegistrationPolicy.shouldRegister(saved.token.startsWith("jdv1_"), existing != null,
                    existing?.device?.registration?.capabilities?.extra?.contains("audio-only") == true)) {
                    client.send(CoreCompanionRoutes.DEVICES, mapOf("deviceId" to deviceId, "name" to "Android ${Build.MODEL}", "kind" to "AndroidCompanion",
                        "capabilities" to mapOf("canListen" to true, "canSpeak" to true, "hasDisplay" to false, "canControlHome" to false, "extra" to listOf("native-android", "audio-only"))), JsonObject::class.java)
                } else client.send(CoreCompanionRoutes.heartbeat(deviceId), emptyMap<String, String>(), JsonObject::class.java)
                val voice = client.get(CoreCompanionRoutes.VOICE_STATUS, AiCapabilityStatus::class.java)
                vault.save(saved.url, saved.token, saved.certificateSha256); offer = null
                mutable.value = ScreenState(connected = true, url = saved.url, voice = voice,
                    verified = existing?.let { it.device.capabilitiesVerified && "audio-only" in it.device.registration.capabilities.extra } == true,
                    legacyDrafts = LegacyDraftRecovery.available(getApplication(), saved))
            } catch (error: Exception) {
                if (error is CancellationException) throw error
                if (client != null && revoked(error)) revoke(client, CompanionAccessPolicy.eraseLegacyFor(error))
                else mutable.value = ScreenState(pairingOffered = offer != null, notice = if (error is CompanionSetupException) error.message.orEmpty() else "Could not reach Core: ${error.message.orEmpty()}")
            } finally { mutable.update { it.copy(busy = false) } }
        }
    }
    private fun revoked(error: Exception) = error is CorePrivacyException || error is CoreHttpException && error.status == 401
    private fun work(action: suspend (CoreApi) -> Unit) {
        val client = api ?: return
        if (mutable.value.busy) return
        viewModelScope.launch {
            mutable.update { it.copy(busy = true, notice = "") }
            try { action(client) }
            catch (error: Exception) {
                if (error is CancellationException) throw error
                if (revoked(error)) revoke(client, CompanionAccessPolicy.eraseLegacyFor(error))
                else if (api === client) mutable.update { it.copy(notice = error.message ?: "Core could not complete the request.", offline = error is java.io.IOException) }
            } finally { if (api === client) mutable.update { it.copy(busy = false, audioStarting = false) } }
        }
    }
    fun prepareLive(onReady: (String) -> Unit) = work { client ->
        check(mutable.value.verified) { "Confirm your microphone and earbuds in connection settings first." }
        check(mutable.value.voice?.ready == true) { mutable.value.voice?.detail ?: "Voice is not ready on Core." }
        val id = preparationId ?: UUID.randomUUID().toString().also { preparationId = it }
        client.send(CoreCompanionRoutes.heartbeat(deviceId), emptyMap<String, String>(), JsonObject::class.java)
        try {
            val prepared = client.send(CoreCompanionRoutes.VOICE_PREPARE, CompanionVoiceRequest(id, deviceId, true), CompanionVoiceSession::class.java)
            preparationId = null; onReady(prepared.sessionId)
        } catch (error: CoreHttpException) { if (error.status in listOf(400, 409)) preparationId = null; throw error }
    }
    fun refresh() = work { client ->
        client.get(CoreCompanionRoutes.PRIVACY_STATUS, PrivacyStatus::class.java)
        client.send(CoreCompanionRoutes.heartbeat(deviceId), emptyMap<String, String>(), JsonObject::class.java)
        val voice = client.get(CoreCompanionRoutes.VOICE_STATUS, AiCapabilityStatus::class.java)
        val device = client.list(CoreCompanionRoutes.DEVICES, DeviceView::class.java).firstOrNull { it.device.registration.deviceId == deviceId }
        mutable.update { it.copy(voice = voice, verified = device?.device?.capabilitiesVerified == true, offline = false, notice = "Connected to Core.") }
    }
    fun verifyAudio() = work { client ->
        val device = client.list(CoreCompanionRoutes.DEVICES, DeviceView::class.java).firstOrNull { it.device.registration.deviceId == deviceId }
        mutable.update { it.copy(verified = device?.device?.capabilitiesVerified == true,
            notice = "Confirm this device's audio on the trusted Core computer, then refresh. The phone cannot verify itself.") }
    }
    suspend fun poll() {
        if (!mutable.value.connected || mutable.value.busy) return
        val client = api ?: return
        try {
            client.get(CoreCompanionRoutes.PRIVACY_STATUS, PrivacyStatus::class.java)
            client.send(CoreCompanionRoutes.heartbeat(deviceId), emptyMap<String, String>(), JsonObject::class.java)
            if (api === client) mutable.update { it.copy(offline = false) }
        } catch (error: Exception) {
            if (error is CancellationException) throw error
            if (revoked(error)) revoke(client, CompanionAccessPolicy.eraseLegacyFor(error))
            else if (api === client) mutable.update { it.copy(offline = true, notice = "Core is unreachable. Voice requires a live connection.") }
        }
    }
    fun reportAudioIssue(message: String) { mutable.update { it.copy(notice = message, audioStarting = false) } }
    fun setAudioStarting(value: Boolean) { mutable.update { it.copy(audioStarting = value) } }
    fun exportLegacy(uri: Uri) {
        val saved = connection ?: return
        val client = api ?: return
        viewModelScope.launch {
            try { client.get(CoreCompanionRoutes.PRIVACY_STATUS, PrivacyStatus::class.java); withContext(Dispatchers.IO) {
                val plain = LegacyDraftRecovery.export(getApplication(), saved)
                getApplication<Application>().contentResolver.openOutputStream(uri, "w")?.use { it.write(plain) } ?: error("The chosen file could not be opened.")
            }; reportAudioIssue("Legacy local drafts exported. Nothing was uploaded or replayed.") }
            catch (error: Exception) { if (error is CancellationException) throw error; if (revoked(error)) revoke(client, CompanionAccessPolicy.eraseLegacyFor(error)) else reportAudioIssue("Export failed; the encrypted originals remain on this phone.") }
        }
    }
    private fun stopAudio() {
        if (LiveVoiceService.state.value.active) getApplication<Application>().startService(Intent(getApplication(), LiveVoiceService::class.java).setAction(LiveVoiceService.STOP))
    }
    private fun revoke(client: CoreApi, eraseLegacy: Boolean) {
        client.invalidate(); if (api !== client) return
        stopAudio(); val saved = connection; api = null; connection = null; preparationId = null
        viewModelScope.coroutineContext.cancelChildren()
        var cleaned = true
        if (eraseLegacy && saved != null) runCatching { LegacyDraftRecovery.eraseIdentity(getApplication(), saved) }.onFailure { cleaned = false }
        if (vault.read()?.let { client.uses(it) } == true) vault.clear()
        mutable.value = ScreenState(privacyRevoked = true, notice = if (cleaned) "Core access changed. Voice stopped; reconnect with the current Core." else "Core access changed. Voice stopped; local legacy-file cleanup needs retry in Settings.")
    }
    fun disconnect() {
        stopAudio(); api?.invalidate(); api = null; connection = null; offer = null; preparationId = null
        viewModelScope.coroutineContext.cancelChildren()
        vault.clear()
        try { LegacyDraftRecovery.eraseAll(getApplication()); mutable.value = ScreenState() }
        catch (_: Exception) { mutable.value = ScreenState(notice = "Connection removed, but local legacy files could not be erased. Retry cleanup.") }
    }
}
