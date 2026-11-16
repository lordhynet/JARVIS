package uk.jarvis.mobile

import android.app.Application
import android.os.Build
import android.util.Base64
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.google.gson.JsonObject
import com.google.gson.Gson
import com.google.gson.JsonParser
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import uk.jarvis.mobile.contracts.*
import java.util.UUID

data class ScreenState(
    val connected: Boolean = false, val busy: Boolean = false, val notice: String = "", val privacyRevoked: Boolean = false, val audioStarting: Boolean = false,
    val url: String = "", val sessions: List<ConversationSession> = emptyList(), val selected: ConversationSession? = null,
    val stories: List<Story> = emptyList(), val learning: List<LearningItem> = emptyList(),
    val runs: List<WorkflowRunView> = emptyList(), val paid: PaidAiStatus? = null, val transcript: String = "",
    val pairingOffered: Boolean = false, val offline: Boolean = false, val pendingWrites: List<PracticeWrite> = emptyList(),
    val questions: List<TrainingQuestion> = emptyList(), val trainingDashboard: TrainingDashboard? = null,
    val trainingCards: List<LearningItem> = emptyList(),
    val drillQuestion: TrainingQuestion? = null, val drillAttempt: TrainingAttempt? = null, val drillDraft: String = "",
    val exercises: List<CoachingExercise> = emptyList(), val coachPrompt: CoachingPrompt? = null,
    val coachAttempt: CoachingAttemptView? = null, val coachDraft: String = "", val coachDuration: String = "",
    val coachHistory: List<CoachingAttemptSummary> = emptyList(), val programme: ProgrammeView? = null,
    val modules: List<ProductModule> = emptyList(), val review: SessionReview? = null,
    val voice: AiCapabilityStatus? = null, val aiCoach: AiCapabilityStatus? = null,
    val agentRuns: List<AgentRunView> = emptyList(), val agentSchedules: List<AgentSchedule> = emptyList(),
    val agentControls: AgentControls? = null, val notifications: List<AgentNotification> = emptyList(),
    val drillModes: List<DrillModeInfo> = emptyList(), val guided: TrainingDrillView? = null,
    val guidedHistory: List<TrainingDrillView> = emptyList(), val guidedDrafts: Map<String, String> = emptyMap(),
    val goals: List<PersonalGoal> = emptyList(), val goalDraft: GoalDraft? = null,
    val pendingGoalSave: SaveGoalRequest? = null, val goalConflict: Boolean = false,
    val goalServerVersion: PersonalGoal? = null, val goalMessage: String = ""
)

class JarvisViewModel(application: Application) : AndroidViewModel(application) {
    private val vault = CredentialVault(application)
    private val mutable = MutableStateFlow(ScreenState())
    val state = mutable.asStateFlow()
    private var api: CoreApi? = null
    private var practiceStore: PracticeStore? = null
    private var queue = PracticeQueue()
    private val gson = Gson()
    private var cacheJob: Job? = null
    private var cacheSequence = 0L
    private var coreEpoch: Long? = null
    private var offeredConnection: SavedConnection? = null
    private var pendingMessage: Triple<String, String, String>? = null
    private var pendingBriefId: String? = null
    private var pendingPlanId: String? = null
    private val answerDrafts = mutableMapOf<String, String>()
    private var pendingTraining: SubmitTrainingAnswerRequest? = null
    private var pendingAssessment: SelfAssessmentRequest? = null
    private var pendingCorrection: CorrectTrainingAnswerRequest? = null
    private var pendingAssessmentTarget: String? = null
    private var pendingCorrectionTarget: String? = null
    private val drillDrafts = mutableMapOf<String, String>()
    private var pendingCoaching: SaveCoachingAnswerRequest? = null
    private var pendingCoachCorrection: Pair<String, CorrectCoachingAnswerRequest>? = null
    private var pendingProgramme: StartProgrammeRequest? = null
    private var pendingCheckIn: Pair<String, ProgrammeCheckInRequest>? = null
    private val prefs = application.getSharedPreferences("installation", 0)
    private val deviceId = prefs.getString("id", null) ?: ("android-" + UUID.randomUUID()).also { prefs.edit().putString("id", it).apply() }
    init {
        // No recovery recording is retained across process death without a retention feature.
        if (!RecordingService.state.value.recording) application.cacheDir.listFiles()?.filter { it.name.startsWith("answer-") }?.forEach { it.delete() }
        vault.read()?.let { connect(it.url, it.token, it.certificateSha256) }
    }
    private suspend fun persistPractice() {
        val store = practiceStore ?: return
        val snapshot = PracticeSnapshot(state = mutable.value.copy(busy = false, pendingWrites = emptyList(), pairingOffered = false, audioStarting = false), writes = queue.list(), coreEpoch = coreEpoch,
            drillDrafts = drillDrafts.toMap(), answerDrafts = answerDrafts.toMap())
        val sequence = ++cacheSequence
        withContext(Dispatchers.IO) { store.write(snapshot, sequence) }
    }
    private fun saveDraftSoon() {
        cacheJob?.cancel()
        cacheJob = viewModelScope.launch {
            delay(300)
            try { persistPractice() } catch (error: Exception) { if (error is CancellationException) throw error; mutable.update { it.copy(notice = "Local draft could not be saved. ${error.message.orEmpty()}") } }
        }
    }
    private suspend fun <T> practiceSend(client: CoreApi, path: String, body: Any, type: Class<T>, method: String = "POST"): T {
        val operation = queue.prepare(path, method, gson.toJson(body))
        mutable.update { it.copy(pendingWrites = queue.list()) }
        persistPractice() // Persist request identity before any network dispatch.
        check(operation.status == "Pending") { "This save needs reconciliation. Review it in Offline saves; its text has been preserved." }
        check(!mutable.value.offline) { "Saved encrypted on this phone. Use Retry same save when Core is reachable; nothing has been uploaded by this offline save." }
        try {
            val result = client.send(path, JsonParser.parseString(operation.json), type, method)
            queue.acknowledge(operation.id)
            mutable.update { it.copy(offline = false, pendingWrites = queue.list()) }
            return result
        } catch (error: CoreHttpException) {
            if (error.status in listOf(400, 401, 403, 404, 409, 422)) queue.block(operation.id, error.message.orEmpty())
            mutable.update { it.copy(pendingWrites = queue.list()) }; throw error
        } catch (error: java.io.IOException) {
            mutable.update { it.copy(offline = true) }; throw error
        }
    }
    private fun work(action: suspend (CoreApi) -> Unit) {
        val client = api ?: return
        if (mutable.value.busy) return
        viewModelScope.launch {
            mutable.update { it.copy(busy = true, notice = "") }
            try { action(client) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (error: Exception) {
                if (error is CorePrivacyException || error is CoreHttpException && error.status == 401) clearRevokedConnection(client)
                else if (api === client) mutable.update { it.copy(notice = error.message ?: "Operation failed. Retry when connected.") }
            }
            finally {
                if (api === client) {
                    mutable.update { it.copy(busy = false, audioStarting = false, pendingWrites = queue.list()) }
                    try { persistPractice() } catch (error: Exception) { if (error is CancellationException) throw error; mutable.update { it.copy(notice = "Local practice data could not be saved. ${error.message.orEmpty()}") } }
                }
            }
        }
    }
    fun offerPairing(encoded: String) {
        runCatching {
            require(encoded.length <= 8192)
            val json = com.google.gson.JsonParser.parseString(String(Base64.decode(encoded, Base64.URL_SAFE or Base64.NO_WRAP), Charsets.UTF_8)).asJsonObject
            val url = CoreAddress.validate(json.get("url").asString)
            val token = json.get("token").asString
            require(token.length in 16..512 && !token.contains('\n'))
            val pin = CertificatePin.normalize(json.get("certificateSha256").asString)
            offeredConnection = SavedConnection(url, token, pin)
            mutable.update { it.copy(pairingOffered = true, notice = "") }
        }.onFailure { mutable.update { it.copy(notice = "Pairing information was invalid. Send it again from your trusted computer.") } }
    }
    fun acceptPairing() {
        offeredConnection?.let { connect(it.url, it.token, it.certificateSha256) }
    }
    fun editAnswer(text: String) { mutable.update { it.copy(transcript = text.take(8000)) }; mutable.value.selected?.let { answerDrafts[it.id] = text.take(8000) }; saveDraftSoon() }
    fun reportAudioIssue(message: String) { mutable.update { it.copy(notice = message, audioStarting = false) } }
    fun setAudioStarting(starting: Boolean) { mutable.update { it.copy(audioStarting = starting) } }
    fun connect(url: String, token: String, certificateSha256: String? = null) {
        api?.invalidate(); viewModelScope.coroutineContext.cancelChildren(); practiceStore?.close(); practiceStore = null; api = null; queue = PracticeQueue(); coreEpoch = null
        viewModelScope.launch {
            mutable.value = ScreenState(busy = true, pairingOffered = offeredConnection != null)
            try {
                require(token.isNotBlank()) { "Enter your Core API key. Provider keys stay on the server." }
                val client = CoreApi(url, token.trim(), certificateSha256)
                api = client
                val store = PracticeStore(getApplication(), url, token.trim())
                val stored = withContext(Dispatchers.IO) { store.read() }
                practiceStore = store
                // Legacy caches have no erasure boundary and cannot safely restore private content.
                var cached = stored?.takeIf { it.coreEpoch != null }
                cached?.coreEpoch?.let { client.bindEpoch(it); coreEpoch = it }
                try {
                    val privacy = client.get("/api/privacy/status", PrivacyStatus::class.java)
                    client.bindEpoch(privacy.epoch); coreEpoch = privacy.epoch
                    if (stored != null && stored.coreEpoch == null) {
                        if (!canMigrateLegacyPractice(privacy.epoch)) throw CorePrivacyException()
                        cached = stored
                    }
                } catch (error: java.io.IOException) {
                    if (error is CoreHttpException || cached == null) throw error
                    // Previously verified encrypted practice remains available during a genuine outage.
                }
                queue = PracticeQueue(cached?.writes.orEmpty())
                drillDrafts.clear(); drillDrafts.putAll(cached?.drillDrafts.orEmpty()); answerDrafts.clear(); answerDrafts.putAll(cached?.answerDrafts.orEmpty())
                if (cached != null) mutable.value = cached.state.copy(connected = true, busy = true, offline = true, pendingWrites = queue.list(), notice = "Showing encrypted local practice data while reconnecting.")
                val sessions = client.list("/api/sessions", ConversationSession::class.java)
                val existing = client.list("/api/devices", DeviceView::class.java).firstOrNull { it.device.registration.deviceId == deviceId }
                if (existing == null) client.send("/api/devices", mapOf("deviceId" to deviceId, "name" to "Android ${Build.MODEL}", "kind" to "AndroidCompanion",
                    "capabilities" to mapOf("canListen" to true, "canSpeak" to true, "hasDisplay" to true, "canControlHome" to false, "extra" to listOf("native-android"))), JsonObject::class.java)
                else client.send("/api/devices/$deviceId/heartbeat", emptyMap<String, String>(), JsonObject::class.java)
                vault.save(CoreAddress.validate(url), token.trim(), certificateSha256); api = client; offeredConnection = null
                pendingMessage = null; pendingTraining = null
                pendingAssessment = null; pendingCorrection = null; pendingCoaching = null; pendingCoachCorrection = null
                pendingProgramme = null; pendingCheckIn = null; pendingBriefId = null; pendingPlanId = null
                mutable.update { it.connectionEstablished(url, sessions, queue.list()) }
                refresh(client)
                reloadPracticeContext(client)
                persistPractice()
            } catch (error: Exception) {
                if (error is CancellationException) throw error
                if (error is CorePrivacyException || error is CoreHttpException && error.status == 401) api?.let { clearRevokedConnection(it) }
                else if (practiceStore != null && mutable.value.connected) mutable.update { it.copy(offline = true, notice = "Core is unreachable. Encrypted local drafts are available; queued saves require explicit retry.") }
                else mutable.value = ScreenState(notice = error.message ?: "Could not connect.", pairingOffered = offeredConnection != null)
            }
            finally { mutable.update { it.copy(busy = false) } }
        }
    }
    private suspend fun refresh(client: CoreApi) {
        val stories = client.list("/api/stories", Story::class.java)
        val cards = client.list("/api/learning?due=true", LearningItem::class.java)
        val runs = client.list("/api/workflows", WorkflowRunView::class.java)
        val paid = client.get("/api/workflows/paid-ai", PaidAiStatus::class.java)
        val questions = client.list("/api/training/questions", TrainingQuestion::class.java)
        val dashboard = client.get("/api/training/dashboard", TrainingDashboard::class.java)
        val trainingCards = client.list("/api/training/learning", LearningItem::class.java)
        val exercises = client.list("/api/coaching/exercises", CoachingExercise::class.java)
        val programme = client.get("/api/programme", ProgrammeView::class.java)
        val modules = client.list("/api/modules", ProductModule::class.java)
        val voice = client.get("/api/voice/status", AiCapabilityStatus::class.java)
        val aiCoach = client.get("/api/coaching/ai-status", AiCapabilityStatus::class.java)
        val agentRuns = client.list("/api/agents/runs", AgentRunView::class.java)
        val agentSchedules = client.list("/api/agents/schedules", AgentSchedule::class.java)
        val agentControls = client.get("/api/agents/controls", AgentControls::class.java)
        val notifications = client.list("/api/agents/notifications", AgentNotification::class.java)
        val modes = client.list("/api/training/modes", DrillModeInfo::class.java)
        val guidedHistory = mutable.value.selected?.let { client.list("/api/training/sessions/${it.id}/drills", TrainingDrillView::class.java) }.orEmpty()
        val goals = client.list("/api/goals", PersonalGoal::class.java)
        mutable.update { it.copy(stories = stories, learning = cards, runs = runs, paid = paid, questions = questions, trainingDashboard = dashboard, trainingCards = trainingCards,
            exercises = exercises, programme = programme, modules = modules, voice = voice, aiCoach = aiCoach,
            agentRuns = agentRuns, agentSchedules = agentSchedules, agentControls = agentControls, notifications = notifications, drillModes = modes, guidedHistory = guidedHistory).refreshedGoals(goals) }
    }
    private suspend fun reloadPracticeContext(client: CoreApi) {
        val current = mutable.value
        suspend fun <T> latest(path: String, type: Class<T>): T? = try { client.get(path, type) }
            catch (error: CoreHttpException) { if (error.status == 404) null else throw error }
        val selected = current.selected?.let { latest("/api/sessions/${it.id}", ConversationSession::class.java) }
        val drill = current.drillAttempt?.let { latest("/api/training/attempts/${it.id}", TrainingAttempt::class.java) }
        val coach = current.coachAttempt?.let { latest("/api/coaching/attempts/${it.attempt.id}", CoachingAttemptView::class.java) }
        val guided = current.guided?.let { latest("/api/training/drills/${it.drill.id}", TrainingDrillView::class.java) }
        val history = selected?.let { client.list("/api/coaching/sessions/${it.id}/attempts", CoachingAttemptSummary::class.java) }.orEmpty()
        mutable.update { it.copy(selected = selected, drillAttempt = drill, coachAttempt = coach, coachHistory = history, review = null, guided = guided) }
    }
    fun retryPractice(id: String) = work { client ->
        val operation = queue.list().firstOrNull { it.id == id } ?: return@work
        check(operation.status == "Pending") { "Load the server version, compare your preserved draft, and discard the conflicted operation before saving a reconciled answer." }
        try {
            val response = client.send(operation.path, JsonParser.parseString(operation.json), JsonObject::class.java, operation.method)
            if (operation.path == "/api/training/drills") {
                val view = gson.fromJson(response, TrainingDrillView::class.java)
                if (mutable.value.selected?.id == view.drill.sessionId) mutable.update { it.copy(guided = view) }
            } else if (operation.path.startsWith("/api/training/")) {
                val attempt = gson.fromJson(response, TrainingAttempt::class.java)
                applyGuidedAnswer(attempt)
                mutable.update { s -> if (s.selected?.id == attempt.sessionId && s.drillQuestion?.id == attempt.questionId &&
                    (s.drillAttempt == null || s.drillAttempt.id == attempt.id)) s.copy(drillAttempt = attempt) else s }
            } else {
                val view = gson.fromJson(response, CoachingAttemptView::class.java)
                mutable.update { s -> if (s.selected?.id == view.attempt.sessionId &&
                    (s.coachAttempt?.attempt?.id == view.attempt.id || s.coachAttempt == null && s.coachPrompt?.exercise?.id == view.exercise.id)) s.copy(coachAttempt = view) else s }
            }
            queue.acknowledge(id); mutable.update { it.copy(pendingWrites = queue.list(), offline = false, notice = "Queued practice save acknowledged by Core. Your editor draft is preserved.") }
            // Save acknowledgement before optional refreshes; a crash before this point safely replays the same ID.
            persistPractice(); refresh(client); reloadPracticeContext(client)
        } catch (error: CoreHttpException) {
            if (error.status in listOf(400, 401, 403, 404, 409, 422)) queue.block(id, error.message.orEmpty())
            mutable.update { it.copy(pendingWrites = queue.list()) }; throw error
        } catch (error: java.io.IOException) { mutable.update { it.copy(offline = true) }; throw error }
    }
    fun discardPractice(id: String) = work {
        queue.acknowledge(id); mutable.update { it.copy(pendingWrites = queue.list(), notice = "Queued operation discarded. Discarding does not undo a server save; your editor text is retained.") }
        persistPractice()
    }
    fun reloadPractice() = work { client -> refresh(client); reloadPracticeContext(client); mutable.update { it.copy(offline = false, notice = "Latest server versions loaded. Local editor drafts have been preserved for comparison.") } }
    fun editGuided(text: String) {
        val plan = mutable.value.guided?.drill ?: return
        val key = "${plan.id}:${plan.attemptIds.size}"
        mutable.update { it.copy(guidedDrafts = it.guidedDrafts + (key to text.take(20000))) }; saveDraftSoon()
    }
    fun startGuided(mode: TrainingMode) = work { client ->
        val session = mutable.value.selected?.takeIf { it.status == SessionStatus.Active } ?: run {
            check(!mutable.value.offline) { "Open a saved active session before starting a drill offline." }
            client.send("/api/sessions", CreateSessionRequest(ActivityType.General, "Guided technical practice", null), ConversationSession::class.java)
        }
        mutable.update { it.copy(selected = session) }
        val view = practiceSend(client, "/api/training/drills", StartTrainingDrillRequest("", session.id, mode, null), TrainingDrillView::class.java)
        mutable.update { it.copy(guided = view) }; refresh(client)
    }
    fun resumeGuided(view: TrainingDrillView) = work { client ->
        val current = if (mutable.value.offline) view else client.get("/api/training/drills/${view.drill.id}", TrainingDrillView::class.java)
        check(current.drill.sessionId == mutable.value.selected?.id) { "Open this drill's session first." }
        mutable.update { it.copy(guided = current) }
    }
    fun reloadGuided() = work { client ->
        val id = mutable.value.guided?.drill?.id ?: return@work
        val view = client.get("/api/training/drills/$id", TrainingDrillView::class.java)
        mutable.update { it.copy(guided = view, offline = false) }; refresh(client)
    }
    private fun applyGuidedAnswer(answer: TrainingAttempt) {
        val view = mutable.value.guided ?: return
        if (answer.drillId != view.drill.id || answer.stepIndex != view.drill.attemptIds.size || answer.id in view.drill.attemptIds) return
        val plan = view.drill.copy(attemptIds = view.drill.attemptIds + answer.id, revision = view.drill.revision + 1,
            status = if (view.drill.attemptIds.size + 1 == view.drill.steps.size) "Completed" else "Active")
        val key = "${view.drill.id}:${answer.stepIndex}"
        mutable.update { it.copy(guided = TrainingDrillView(plan, view.answers + answer),
            guidedDrafts = if (it.guidedDrafts[key]?.trim() == answer.currentAnswer) it.guidedDrafts - key else it.guidedDrafts) }
    }
    fun saveGuided(skipped: Boolean = false) = work { client ->
        val view = mutable.value.guided ?: return@work; val plan = view.drill
        check(plan.sessionId == mutable.value.selected?.id) { "Open this drill's session first." }
        check(plan.status == "Active") { "This drill is already finished." }
        val step = plan.steps[plan.attemptIds.size]; val text = mutable.value.guidedDrafts["${plan.id}:${step.index}"].orEmpty().trim()
        val request = SubmitTrainingAnswerRequest("", plan.sessionId, step.questionId, step.questionVersion, plan.mode, if (skipped) "" else text, skipped, plan.id, step.index)
        val answer = practiceSend(client, "/api/training/attempts", request, TrainingAttempt::class.java)
        applyGuidedAnswer(answer)
        val current = client.get("/api/training/drills/${plan.id}", TrainingDrillView::class.java)
        mutable.update { it.copy(guided = current, notice = "Stage saved. Completion is not a mastery score.") }; refresh(client)
    }
    fun cancelGuided() = work { client ->
        val plan = mutable.value.guided?.drill ?: return@work
        val view = client.send("/api/training/drills/${plan.id}/cancel", CancelTrainingDrillRequest(plan.revision), TrainingDrillView::class.java)
        mutable.update { it.copy(guided = view) }; refresh(client)
    }
    fun refresh() = work { client -> refresh(client); reloadPracticeContext(client); val sessions = client.list("/api/sessions", ConversationSession::class.java); mutable.update { it.copy(sessions = sessions, offline = false) } }
    fun editCoach(text: String) { mutable.update { it.copy(coachDraft = text.take(20000)) }; saveDraftSoon() }
    fun editCoachDuration(text: String) { mutable.update { it.copy(coachDuration = text.take(12)) }; saveDraftSoon() }
    fun chooseCoach(exercise: CoachingExercise, story: Story?, claim: Int?) = work { client ->
        if (mutable.value.offline) {
            val session = mutable.value.selected ?: error("Open a saved session before going offline. Creating a new session requires Core.")
            check(session.status != SessionStatus.Ended) { "Choose an active saved session for offline practice." }
            check(!exercise.requiresStory || story != null) { "Choose a cached real story." }
            check(!exercise.requiresClaim || claim != null && claim in story?.content?.cvClaims.orEmpty().indices) { "Choose an existing saved claim." }
            val prompt = CoachingPrompt(exercise, exercise.prompt + (story?.let { "\nStory: ${it.content.title}. Service: ${it.content.serviceFeature}." } ?: "") +
                (claim?.let { "\nSaved claim: ${story!!.content.cvClaims!![it]}" } ?: ""), story?.id, story?.version, claim)
            mutable.update { it.copy(coachPrompt = prompt, coachAttempt = null, coachDraft = "", coachDuration = "", review = null) }
            return@work
        }
        val path = "/api/coaching/prompt?exerciseId=${exercise.id}" + (story?.let { "&storyId=${it.id}&storyVersion=${it.version}" } ?: "") + (claim?.let { "&claimIndex=$it" } ?: "")
        val prompt = client.get(path, CoachingPrompt::class.java)
        val current = mutable.value.selected
        val session = current?.takeIf { it.status != SessionStatus.Ended } ?: client.send("/api/sessions",
            CreateSessionRequest(if (exercise.area == CoachingArea.Communication) ActivityType.Communication else ActivityType.MockInterview, exercise.title, null), ConversationSession::class.java)
        val checkpoint = client.send("/api/sessions/${session.id}/checkpoints", AddCheckpointRequest("Coaching: ${exercise.title}", "coaching:${exercise.id}"), ConversationSession::class.java)
        val history = client.list("/api/coaching/sessions/${session.id}/attempts", CoachingAttemptSummary::class.java)
        mutable.update { it.copy(selected = checkpoint, coachPrompt = prompt, coachAttempt = null, coachDraft = "", coachDuration = "", coachHistory = history, review = null) }
    }
    private fun reportedDuration(): Double? = mutable.value.coachDuration.trim().takeIf { it.isNotEmpty() }?.let {
        it.toDoubleOrNull()?.takeIf { value -> value.isFinite() && value in 1.0..3600.0 } ?: error("Reported elapsed time must be 1–3600 seconds, or leave it blank.")
    }
    fun saveCoach() = work { client ->
        val s = mutable.value; val prompt = s.coachPrompt ?: error("Choose a coaching exercise first.")
        val session = s.selected ?: error("Open a session first.")
        val candidate = SaveCoachingAnswerRequest("", session.id, prompt.exercise.id, prompt.exercise.version, s.coachDraft.trim(), prompt.storyId, prompt.storyVersion, prompt.claimIndex, reportedDuration())
        val request = pendingCoaching?.takeIf { it.copy(requestId = "") == candidate } ?: candidate.copy(requestId = UUID.randomUUID().toString())
        pendingCoaching = request
        val saved = practiceSend(client, "/api/coaching/attempts", request, CoachingAttemptView::class.java)
        pendingCoaching = null; mutable.update { it.copy(coachAttempt = saved, notice = "Answer saved. Feedback is a text heuristic, not an AI grade.") }
        loadCoachHistory(client, session.id)
    }
    fun correctCoach() = work { client ->
        val attempt = mutable.value.coachAttempt?.attempt ?: return@work
        val candidate = CorrectCoachingAnswerRequest("", attempt.revision, mutable.value.coachDraft.trim(), reportedDuration())
        val request = pendingCoachCorrection?.takeIf { it.first == attempt.id && it.second.copy(requestId = "") == candidate }?.second
            ?: candidate.copy(requestId = UUID.randomUUID().toString())
        pendingCoachCorrection = Pair(attempt.id, request)
        val updated = practiceSend(client, "/api/coaching/attempts/${attempt.id}", request, CoachingAttemptView::class.java, "PATCH")
        pendingCoachCorrection = null; mutable.update { it.copy(coachAttempt = updated, notice = "Correction saved; observations recalculated from the current answer.") }
        loadCoachHistory(client, attempt.sessionId)
    }
    private suspend fun loadCoachHistory(client: CoreApi, id: String) {
        val history = client.list("/api/coaching/sessions/$id/attempts", CoachingAttemptSummary::class.java)
        mutable.update { it.copy(coachHistory = history, review = null) }
    }
    fun openCoach(id: String) = work { client ->
        val view = client.get("/api/coaching/attempts/$id", CoachingAttemptView::class.java)
        val session = client.get("/api/sessions/${view.attempt.sessionId}", ConversationSession::class.java)
        val answer = view.attempt.answers.last()
        mutable.update { it.copy(selected = session, coachAttempt = view, coachDraft = answer.text,
            coachDuration = answer.reportedDurationSeconds?.toString().orEmpty(), coachPrompt = null, review = null) }
        loadCoachHistory(client, session.id)
    }
    fun freshCoach() { mutable.update { it.copy(coachAttempt = null, coachDraft = "", coachDuration = "") } }
    fun deleteCoach() = work { client ->
        val attempt = mutable.value.coachAttempt?.attempt ?: return@work
        client.delete("/api/coaching/attempts/${attempt.id}?expectedRevision=${attempt.revision}")
        mutable.update { it.copy(coachAttempt = null, coachDraft = "", coachDuration = "", notice = "Saved coaching answer deleted.") }
        loadCoachHistory(client, attempt.sessionId)
    }
    fun sessionReview() = work { client ->
        val id = mutable.value.selected?.id ?: error("Open a conversation first.")
        val review = client.get("/api/sessions/$id/review", SessionReview::class.java)
        mutable.update { it.copy(review = review) }
    }
    fun prepareLive(onReady: (String) -> Unit) = work { client ->
        var session = mutable.value.selected?.takeIf { it.status != SessionStatus.Ended }
            ?: client.send("/api/sessions", CreateSessionRequest(ActivityType.Communication, "Live voice coaching", null), ConversationSession::class.java)
        if (session.status == SessionStatus.Paused) session = client.send("/api/sessions/${session.id}/status", mapOf("status" to "Active"), ConversationSession::class.java)
        client.send("/api/devices/$deviceId/heartbeat", emptyMap<String, String>(), JsonObject::class.java)
        session = client.send("/api/sessions/${session.id}/claim", mapOf("deviceId" to deviceId), ConversationSession::class.java)
        mutable.update { it.copy(selected = session) }; onReady(session.id)
    }
    private val aiRequests = mutableMapOf<String, AiAssessmentRequest>()
    fun assessWithAi(technical: Boolean, consent: Boolean) = work { client ->
        check(consent) { "Approve cloud processing for the saved answer first." }
        val s = mutable.value
        val id = if (technical) s.drillAttempt?.id else s.coachAttempt?.attempt?.id
        val revision = if (technical) s.drillAttempt?.revision else s.coachAttempt?.attempt?.revision
        val textRevision = if (technical) s.drillAttempt?.textRevision else s.coachAttempt?.attempt?.answers?.lastOrNull()?.version
        check(id != null && revision != null && textRevision != null) { "Save an answer first." }
        val key = "$technical:$id:$revision:$textRevision"
        val request = aiRequests.getOrPut(key) { AiAssessmentRequest(UUID.randomUUID().toString(), revision, textRevision, true, "Europe/London") }
        if (technical) {
            val updated = client.send("/api/training/attempts/$id/ai-assessments", request, TrainingAttempt::class.java)
            mutable.update { it.copy(drillAttempt = updated) }
        } else {
            val updated = client.send("/api/coaching/attempts/$id/ai-assessments", request, CoachingAttemptView::class.java)
            mutable.update { it.copy(coachAttempt = updated) }
        }
        aiRequests.remove(key); refresh(client)
    }
    private var pendingAgent: StartAgentRunRequest? = null
    fun startAgent(workflow: String, brief: String, consent: Boolean) = work { client ->
        val candidate = StartAgentRunRequest("", workflow, consent, brief)
        val request = pendingAgent?.takeIf { it.copy(requestId = "") == candidate } ?: candidate.copy(requestId = UUID.randomUUID().toString())
        pendingAgent = request; client.send("/api/agents/runs", request, AgentRunView::class.java); pendingAgent = null; refresh(client)
    }
    fun setAgentPause(paused: Boolean) = work { client ->
        val settings = mutable.value.agentControls ?: return@work
        client.send("/api/agents/controls", settings.copy(paused = paused), AgentControls::class.java, "PUT"); refresh(client)
    }
    fun saveDailyAgent(workflow: String, title: String, time: String, consent: Boolean) = work { client ->
        client.send("/api/agents/schedules", SaveAgentScheduleRequest(UUID.randomUUID().toString(), null, title, workflow, "Europe/London", time, true, consent), AgentSchedule::class.java); refresh(client)
    }
    fun toggleAgentSchedule(schedule: AgentSchedule) = work { client ->
        client.send("/api/agents/schedules", SaveAgentScheduleRequest(schedule.id, schedule.revision, schedule.title, schedule.workflow, schedule.timeZone, schedule.localTime, !schedule.enabled, schedule.allowCloudProcessing), AgentSchedule::class.java); refresh(client)
    }
    fun cancelAgent(id: String, delete: Boolean) = work { client ->
        if (delete) client.delete("/api/agents/runs/$id") else client.send("/api/agents/runs/$id/cancel", emptyMap<String, String>(), JsonObject::class.java)
        refresh(client)
    }
    fun startProgramme(date: String) = work { client ->
        val candidate = StartProgrammeRequest("", date, "Europe/London")
        val request = pendingProgramme?.takeIf { it.copy(requestId = "") == candidate } ?: candidate.copy(requestId = UUID.randomUUID().toString())
        pendingProgramme = request
        client.send("/api/programme", request, ImprovementProgramme::class.java); pendingProgramme = null
        refresh(client)
    }
    fun checkIn(week: Int, note: String) = work { client ->
        val programme = mutable.value.programme?.programme ?: return@work
        val candidate = ProgrammeCheckInRequest("", programme.revision, week, note.trim())
        val request = pendingCheckIn?.takeIf { it.first == programme.id && it.second.copy(requestId = "") == candidate }?.second ?: candidate.copy(requestId = UUID.randomUUID().toString())
        pendingCheckIn = Pair(programme.id, request)
        client.send("/api/programme/${programme.id}/check-ins", request, ImprovementProgramme::class.java); pendingCheckIn = null
        refresh(client)
    }
    fun deleteProgramme() = work { client ->
        val programme = mutable.value.programme?.programme ?: return@work
        client.delete("/api/programme/${programme.id}?expectedRevision=${programme.revision}"); refresh(client)
    }
    fun editDrill(text: String) {
        mutable.value.drillQuestion?.let { drillDrafts[it.id] = text.take(20000) }
        mutable.update { it.copy(drillDraft = text.take(20000)) }
        saveDraftSoon()
    }
    fun chooseDrill(question: TrainingQuestion) = work { client ->
        if (mutable.value.offline) {
            val session = mutable.value.selected ?: error("Open a saved session before going offline. Creating a new session requires Core.")
            check(session.status != SessionStatus.Ended) { "Choose an active saved session for offline practice." }
            mutable.update { it.copy(drillQuestion = question, drillAttempt = it.drillAttempt?.takeIf { attempt -> attempt.questionId == question.id && attempt.sessionId == session.id },
                drillDraft = drillDrafts[question.id].orEmpty()) }
            return@work
        }
        val current = mutable.value.selected
        var session = current?.takeIf { it.status != SessionStatus.Ended }
            ?: client.send("/api/sessions", CreateSessionRequest(ActivityType.General, "Technical practice", null), ConversationSession::class.java)
        session = client.send("/api/sessions/${session.id}/checkpoints", AddCheckpointRequest("Practising: ${question.title}", "training-question:${question.id}"), ConversationSession::class.java)
        val history = client.list("/api/training/sessions/${session.id}/attempts", TrainingAttemptSummary::class.java)
        val attempt = history.firstOrNull { it.questionId == question.id }?.let { client.get("/api/training/attempts/${it.id}", TrainingAttempt::class.java) }
        mutable.update { it.copy(selected = session, drillQuestion = question, drillAttempt = attempt,
            drillDraft = drillDrafts[question.id] ?: attempt?.currentAnswer.orEmpty()) }
    }
    fun saveDrill(mode: TrainingMode, skipped: Boolean = false) = work { client ->
        val s = mutable.value; val question = s.drillQuestion ?: return@work
        val session = s.selected ?: error("Open a training question first.")
        val text = if (skipped) "" else s.drillDraft.trim()
        val previous = pendingTraining
        val request = previous?.takeIf { it.sessionId == session.id && it.questionId == question.id && it.text == text && it.mode == mode && it.skipped == skipped }
            ?: SubmitTrainingAnswerRequest(UUID.randomUUID().toString(), session.id, question.id, question.version, mode, text, skipped)
        pendingTraining = request
        val attempt = practiceSend(client, "/api/training/attempts", request, TrainingAttempt::class.java)
        pendingTraining = null
        mutable.update { it.copy(drillAttempt = attempt, notice = if (skipped) "Skipped without a score." else "Answer saved to Core.") }
        refresh(client)
    }
    fun assessDrill(value: Int?, evidence: String, rationale: String) = work { client ->
        val attempt = mutable.value.drillAttempt ?: return@work
        val scores = listOf(DimensionScore(AssessmentDimension.TechnicalAccuracy, value, evidence, rationale))
        val request = pendingAssessment?.takeIf { pendingAssessmentTarget == attempt.id && it.expectedRevision == attempt.revision && it.textRevision == attempt.textRevision && it.scores == scores }
            ?: SelfAssessmentRequest(UUID.randomUUID().toString(), attempt.revision, attempt.textRevision, scores, "Europe/London")
        pendingAssessment = request
        pendingAssessmentTarget = attempt.id
        val updated = practiceSend(client, "/api/training/attempts/${attempt.id}/assessments", request, TrainingAttempt::class.java)
        pendingAssessment = null
        mutable.update { it.copy(drillAttempt = updated, notice = "Self-assessment saved. Your review schedule is updated.") }
        refresh(client)
    }
    fun correctDrill() = work { client ->
        val attempt = mutable.value.drillAttempt ?: return@work
        val text = mutable.value.drillDraft.trim()
        val request = pendingCorrection?.takeIf { pendingCorrectionTarget == attempt.id && it.expectedRevision == attempt.revision && it.text == text }
            ?: CorrectTrainingAnswerRequest(UUID.randomUUID().toString(), attempt.revision, text)
        pendingCorrection = request
        pendingCorrectionTarget = attempt.id
        val updated = practiceSend(client, "/api/training/attempts/${attempt.id}/answer", request, TrainingAttempt::class.java, "PATCH")
        pendingCorrection = null
        mutable.update { it.copy(drillAttempt = updated, notice = "Correction saved. Assess the revised answer again.") }
        refresh(client)
    }
    fun freshDrill() { mutable.value.drillQuestion?.let { drillDrafts.remove(it.id) }; mutable.update { it.copy(drillAttempt = null, drillDraft = "") } }
    fun deleteDrill() = work { client ->
        val attempt = mutable.value.drillAttempt ?: return@work
        client.delete("/api/training/attempts/${attempt.id}?expectedRevision=${attempt.revision}")
        mutable.value.drillQuestion?.let { drillDrafts.remove(it.id) }
        mutable.update { it.copy(drillAttempt = null, drillDraft = "", notice = "Saved answer and assessments deleted.") }
        refresh(client)
    }
    suspend fun poll() {
        if (!mutable.value.connected || mutable.value.busy) return
        val client = api ?: return
        try {
            client.get("/api/privacy/status", PrivacyStatus::class.java)
            client.send("/api/devices/$deviceId/heartbeat", emptyMap<String, String>(), JsonObject::class.java)
            val runs = client.list("/api/workflows", WorkflowRunView::class.java)
            if (api === client) mutable.update { it.copy(runs = runs) }
        }
        catch (error: Exception) {
            if (error is CancellationException) throw error
            if (error is CorePrivacyException || error is CoreHttpException && error.status == 401) clearRevokedConnection(client)
            else if (api === client) mutable.update { it.copy(offline = true, notice = "Core is unreachable. Displayed results may be out of date. Practice drafts are stored locally; retry queued saves explicitly.") }
        }
    }
    fun openSession(id: String) = work { client ->
        mutable.value.selected?.let { answerDrafts[it.id] = mutable.value.transcript }
        if (mutable.value.offline) {
            val session = mutable.value.sessions.firstOrNull { it.id == id } ?: error("This session is not cached. Reconnect to load it.")
            mutable.update { it.copy(selected = session, transcript = answerDrafts[id].orEmpty(), drillQuestion = null, drillAttempt = null, drillDraft = "",
                coachPrompt = null, coachAttempt = null, coachDraft = "", coachDuration = "", coachHistory = emptyList(), review = null) }
            return@work
        }
        val session = client.get("/api/sessions/$id", ConversationSession::class.java)
        mutable.update { it.copy(selected = session, transcript = answerDrafts[id].orEmpty()) }
        restoreDrill(client, session)
        loadCoachHistory(client, session.id)
        val guidedHistory = client.list("/api/training/sessions/${session.id}/drills", TrainingDrillView::class.java)
        mutable.update { it.copy(guidedHistory = guidedHistory, guided = it.guided?.takeIf { view -> view.drill.sessionId == session.id }) }
        mutable.update { it.copy(coachAttempt = null, coachPrompt = null, coachDraft = "", coachDuration = "", review = null) }
    }
    private suspend fun restoreDrill(client: CoreApi, session: ConversationSession) {
        val questionId = session.currentTask?.takeIf { it.startsWith("training-question:") }?.removePrefix("training-question:")
        val question = mutable.value.questions.firstOrNull { it.id == questionId }
        val attempt = if (question == null) null else client.list("/api/training/sessions/${session.id}/attempts", TrainingAttemptSummary::class.java)
            .firstOrNull { it.questionId == question.id }?.let { client.get("/api/training/attempts/${it.id}", TrainingAttempt::class.java) }
        mutable.update { it.copy(drillQuestion = question, drillAttempt = attempt, drillDraft = question?.let { q -> drillDrafts[q.id] ?: attempt?.currentAnswer.orEmpty() }.orEmpty()) }
    }
    fun openTrainingAttempt(id: String) = work { client ->
        val attempt = client.get("/api/training/attempts/$id", TrainingAttempt::class.java)
        val session = client.get("/api/sessions/${attempt.sessionId}", ConversationSession::class.java)
        val question = mutable.value.questions.firstOrNull { it.id == attempt.questionId } ?: error("Question is unavailable.")
        mutable.update { it.copy(selected = session, drillQuestion = question, drillAttempt = attempt,
            drillDraft = drillDrafts[question.id] ?: attempt.currentAnswer) }
        val guidedHistory = client.list("/api/training/sessions/${session.id}/drills", TrainingDrillView::class.java)
        mutable.update { it.copy(guidedHistory = guidedHistory) }
    }
    fun createSession(activity: ActivityType) = work { client ->
        mutable.value.selected?.let { answerDrafts[it.id] = mutable.value.transcript }
        val session = client.send("/api/sessions", CreateSessionRequest(activity, "Android ${activity.name}", null), ConversationSession::class.java)
        mutable.update { it.copy(selected = session, sessions = listOf(session) + it.sessions, transcript = "", drillQuestion = null, drillAttempt = null, drillDraft = "") }
    }
    fun message(text: String) = work { client ->
        val session = mutable.value.selected ?: error("Open a conversation first.")
        val previous = pendingMessage
        val requestId = if (previous?.first == session.id && previous.second == text) previous.third else UUID.randomUUID().toString()
        pendingMessage = Triple(session.id, text, requestId)
        val updated = client.send("/api/sessions/${session.id}/messages", ChatRequest(requestId, text), ConversationSession::class.java)
        pendingMessage = null; answerDrafts.remove(session.id)
        mutable.update { it.copy(selected = updated, transcript = if (it.transcript == text) "" else it.transcript) }
    }
    fun transition(status: String) = work { client ->
        val session = mutable.value.selected ?: return@work
        if (status != "Active") client.send("/api/sessions/${session.id}/checkpoints", AddCheckpointRequest("Android practice paused at the current transcript.", session.currentTask), ConversationSession::class.java)
        val updated = client.send("/api/sessions/${session.id}/status", mapOf("status" to status), ConversationSession::class.java)
        mutable.update { it.copy(selected = updated) }
    }
    fun verifyAudio() = work { client ->
        client.send("/api/devices/$deviceId/verify", emptyMap<String, String>(), JsonObject::class.java)
        mutable.update { it.copy(notice = "Audio test confirmed. Claim a conversation before recording.") }
    }
    fun claimAudio(onReady: () -> Unit) = work { client ->
        val session = mutable.value.selected ?: error("Open an active conversation first.")
        client.send("/api/devices/$deviceId/heartbeat", emptyMap<String, String>(), JsonObject::class.java)
        val updated = client.send("/api/sessions/${session.id}/claim", mapOf("deviceId" to deviceId), ConversationSession::class.java)
        mutable.update { it.copy(selected = updated) }; onReady()
    }
    fun transcribe() = work { client ->
        val file = RecordingService.state.value.file ?: error("Finish a recording first.")
        try { val text = client.transcribe(file); mutable.update { it.copy(transcript = text, notice = "Review this transcript before sending it to your conversation.") } }
        finally { RecordingService.clearFile() }
    }
    fun saveStory(id: String?, version: Int, content: StoryContent, done: () -> Unit) = work { client ->
        if (id == null) client.send("/api/stories", CreateStoryRequest(UUID.randomUUID().toString(), content), Story::class.java)
        else client.send("/api/stories/$id", UpdateStoryRequest(version, content), Story::class.java, "PUT")
        refresh(client); done()
    }
    fun deleteStory(story: Story) = work { client -> client.delete("/api/stories/${story.id}?expectedVersion=${story.version}"); refresh(client) }
    fun review(item: LearningItem, rating: ReviewRating) = work { client ->
        client.send("/api/learning/${item.id}/reviews", ReviewLearningRequest(UUID.randomUUID().toString(), rating, 0.0), LearningItem::class.java); refresh(client)
    }
    fun plan() = work { client ->
        val id = pendingPlanId ?: UUID.randomUUID().toString().also { pendingPlanId = it }
        client.send("/api/workflows/practice-plans", CreatePracticePlanRequest(id, null), WorkflowRunView::class.java)
        pendingPlanId = null; refresh(client)
    }
    fun brief(consent: Boolean) = work { client ->
        check(consent) { "Approve cloud processing for this brief first." }
        val id = pendingBriefId ?: UUID.randomUUID().toString().also { pendingBriefId = it }
        client.send("/api/workflows/interview-briefs", CreateInterviewBriefRequest(id, true), WorkflowRunView::class.java)
        pendingBriefId = null; refresh(client)
    }
    fun cancel(run: WorkflowRunView) = work { client -> client.send("/api/workflows/${run.id}/cancel", emptyMap<String, String>(), WorkflowRunView::class.java); refresh(client) }
    fun deleteRun(run: WorkflowRunView) = work { client -> client.delete("/api/workflows/${run.id}"); refresh(client) }
    private fun goalFeedback(message: String) { mutable.update { it.copy(goalMessage = message, notice = message) } }
    fun newGoal() {
        if (!mutable.value.connected || mutable.value.busy || mutable.value.goalDraft != null || mutable.value.pendingGoalSave != null) return
        mutable.update { it.copy(goalDraft = GoalDraft.fresh(), goalConflict = false, goalServerVersion = null, goalMessage = "Local goal draft. Save while connected to create the goal in Core.") }; saveDraftSoon()
    }
    fun editGoal(goal: PersonalGoal) {
        if (!mutable.value.connected || mutable.value.busy || mutable.value.goalDraft != null || mutable.value.pendingGoalSave != null) return
        val draft = runCatching { GoalDraft.from(goal) }.getOrElse { goalFeedback("Goal dates could not be read. Refresh Core before editing."); return }
        mutable.update { it.copy(goalDraft = draft, goalConflict = false, goalServerVersion = null, goalMessage = "Editing a saved goal. Other devices' changes are checked when you save.") }; saveDraftSoon()
    }
    fun updateGoalDraft(change: (GoalDraft) -> GoalDraft) {
        if (mutable.value.busy || mutable.value.pendingGoalSave != null) return
        mutable.update { it.copy(goalDraft = it.goalDraft?.let(change)) }; saveDraftSoon()
    }
    fun discardGoalDraft() {
        if (mutable.value.busy || mutable.value.pendingGoalSave != null) return
        mutable.update { it.copy(goalDraft = null, goalConflict = false, goalServerVersion = null, goalMessage = "Local goal draft discarded. Saved Core goals are unchanged.") }; saveDraftSoon()
    }
    fun useLatestGoal(keepDraft: Boolean) {
        if (mutable.value.busy || mutable.value.pendingGoalSave != null) return
        val latest = mutable.value.goalServerVersion ?: return
        val draft = if (keepDraft) mutable.value.goalDraft?.copy(expectedRevision = latest.revision)
            else runCatching { GoalDraft.from(latest) }.getOrElse { goalFeedback("Current Core goal dates could not be read. Keep your draft and refresh Core."); return }
        mutable.update { s -> s.copy(goalDraft = draft,
            goalConflict = false, goalServerVersion = null, goalMessage = "Review all fields, including dates and cloud consent, before saving against the current Core version.") }; saveDraftSoon()
    }
    private fun goalConflict(latest: PersonalGoal?, message: String) {
        mutable.update { it.copy(pendingGoalSave = null, goalConflict = true, goalServerVersion = latest, goalMessage = message, notice = message) }
    }
    private fun goalSaved(saved: PersonalGoal) {
        val edited = mutable.value.pendingGoalSave?.expectedRevision != null
        mutable.update { it.copy(goals = (it.goals.filterNot { goal -> goal.id == saved.id } + saved).sortedBy { goal -> goal.nextReviewAt },
            goalDraft = null, pendingGoalSave = null, goalConflict = false, goalServerVersion = null,
            agentRuns = if (edited) emptyList() else it.agentRuns, notifications = if (edited) emptyList() else it.notifications,
            goalMessage = "Goal confirmed on Core. Reviews follow Core pause controls and budgets.", notice = "Goal confirmed on Core.", offline = false) }
    }
    private suspend fun refreshGoalActivity(client: CoreApi) {
        try {
            val runs = client.list("/api/agents/runs", AgentRunView::class.java)
            val notes = client.list("/api/agents/notifications", AgentNotification::class.java)
            mutable.update { it.copy(agentRuns = runs, notifications = notes) }
        } catch (error: java.io.IOException) {
            if (error is CoreHttpException && error.status == 401) throw error
            goalFeedback("Goal change confirmed. Review activity could not be refreshed; refresh when Core is reachable.")
        }
    }
    private suspend fun checkPendingGoal(client: CoreApi, rejected: Boolean = false, reason: String? = null) {
        val request = mutable.value.pendingGoalSave ?: return
        val goals = client.list("/api/goals", PersonalGoal::class.java)
        val latest = goals.firstOrNull { it.id == request.requestId }
        mutable.update { it.copy(goals = goals, offline = false) }
        when (checkGoalSave(request, latest)) {
            GoalSaveCheck.Confirmed -> { goalSaved(latest!!); refreshGoalActivity(client) }
            GoalSaveCheck.Retry -> if (rejected) goalConflict(latest, "${reason ?: "Core rejected this save."} Your draft is preserved; compare the current Core goal before saving again.")
                else goalFeedback("Core has not confirmed this save. Retry the same saved request; no new goal ID will be created.")
            GoalSaveCheck.Missing -> goalConflict(null, "This goal is absent from Core. Your local draft is preserved, but saving to the deleted goal is blocked.")
            GoalSaveCheck.Conflict -> goalConflict(latest, "The Core goal differs from your save. Your draft is preserved. Compare the current version before choosing how to continue.")
        }
    }
    fun refreshGoals() = work { client ->
        try {
            client.get("/api/privacy/status", PrivacyStatus::class.java)
            if (mutable.value.pendingGoalSave != null) checkPendingGoal(client)
            else { val goals = client.list("/api/goals", PersonalGoal::class.java); mutable.update { it.refreshedGoals(goals) }; goalFeedback("Latest Core goals loaded. Local drafts are unchanged.") }
        } catch (error: java.io.IOException) {
            if (error is CoreHttpException && error.status == 401) throw error
            mutable.update { it.copy(offline = true) }; goalFeedback("Core could not be checked. Your local draft and any unconfirmed save are preserved.")
        }
    }
    fun saveGoal() = work { client ->
        if (mutable.value.pendingGoalSave != null) { goalFeedback("Check Core or retry the unconfirmed save before changing this request."); return@work }
        if (mutable.value.offline || mutable.value.goalConflict) { goalFeedback("Connect and resolve the current version before saving. Your draft remains on this phone."); return@work }
        val draft = mutable.value.goalDraft ?: return@work
        val request = try { draft.request() } catch (error: IllegalArgumentException) { goalFeedback(error.message ?: "Check goal fields."); return@work }
        mutable.update { it.copy(pendingGoalSave = request) }
        try { persistPractice() } catch (error: Exception) {
            if (error is CancellationException) throw error
            mutable.update { it.copy(pendingGoalSave = null) }; goalFeedback("The save could not be stored safely on this phone; nothing was dispatched. ${error.message.orEmpty()}"); return@work
        }
        dispatchGoal(client, request)
    }
    fun retryGoalSave() = work { client ->
        val request = mutable.value.pendingGoalSave ?: return@work
        try {
            client.get("/api/privacy/status", PrivacyStatus::class.java)
            // The exact body and ID were encrypted before the first dispatch; never mint a replacement.
            dispatchGoal(client, request)
        } catch (error: java.io.IOException) {
            if (error is CoreHttpException && error.status == 401) throw error
            mutable.update { it.copy(offline = true) }; goalFeedback("Core could not confirm the retry. The original request is preserved; check Core before changing it.")
        }
    }
    private suspend fun dispatchGoal(client: CoreApi, request: SaveGoalRequest) {
        try { val saved = client.send("/api/goals", request, PersonalGoal::class.java); goalSaved(saved); refreshGoalActivity(client) }
        catch (error: CoreHttpException) {
            when (error.status) {
                401 -> throw error
                409 -> checkPendingGoal(client, rejected = true, reason = error.message)
                400, 403, 404, 422 -> { mutable.update { it.copy(pendingGoalSave = null) }; goalFeedback(error.message ?: "Goal rejected. Your draft is preserved.") }
                else -> goalFeedback("Goal save was not confirmed. Check Core or retry the same request. ${error.message.orEmpty()}")
            }
        } catch (_: java.io.IOException) {
            mutable.update { it.copy(offline = true) }; goalFeedback("Goal save was not confirmed. Its exact request is encrypted on this phone. Check Core or retry that request when connected.")
        }
    }
    fun deleteGoal(goal: PersonalGoal) = work { client ->
        if (mutable.value.offline || mutable.value.pendingGoalSave != null) { goalFeedback("Check Core and resolve unconfirmed saves before deleting."); return@work }
        var removed = false
        try { client.delete("/api/goals/${goal.id}?revision=${goal.revision}"); removed = true }
        catch (error: CoreHttpException) {
            if (error.status !in listOf(404, 409)) throw error
            val current = client.list("/api/goals", PersonalGoal::class.java); mutable.update { it.copy(goals = current) }
            removed = current.none { it.id == goal.id }
            if (!removed) goalFeedback("The goal changed. Latest goals are loaded; review and confirm deletion again.")
        } catch (_: java.io.IOException) { goalFeedback("Deletion was not confirmed. Refresh Core goals before trying again; no automatic deletion is queued.") }
        if (removed) {
            mutable.update { s -> s.copy(goals = s.goals.filterNot { it.id == goal.id }, goalDraft = s.goalDraft?.takeUnless { it.id == goal.id },
                goalConflict = if (s.goalDraft?.id == goal.id) false else s.goalConflict, goalServerVersion = s.goalServerVersion?.takeUnless { it.id == goal.id },
                agentRuns = emptyList(), notifications = emptyList(), goalMessage = "Goal context deleted and linked review work stopped.", notice = "Goal deleted.") }
            refreshGoalActivity(client)
        }
    }
    fun disconnect(eraseAllCaches: Boolean = true, clearCredentials: Boolean = true) {
        api?.invalidate(); viewModelScope.coroutineContext.cancelChildren(); api = null; coreEpoch = null
        try { practiceStore?.clear(); if (eraseAllCaches) PracticeStore.eraseAll(getApplication()) }
        catch (error: Exception) { mutable.update { it.copy(busy = false, notice = error.message ?: "Local data could not be erased. Try signing out again.") }; return }
        practiceStore = null; queue = PracticeQueue(); if (clearCredentials) vault.clear(); RecordingService.clearFile()
        pendingMessage = null; pendingBriefId = null; pendingPlanId = null; offeredConnection = null; answerDrafts.clear(); mutable.value = ScreenState()
        pendingTraining = null; pendingAssessment = null; pendingCorrection = null
        pendingAssessmentTarget = null; pendingCorrectionTarget = null; drillDrafts.clear()
        pendingCoaching = null; pendingCoachCorrection = null; pendingProgramme = null; pendingCheckIn = null
        pendingAgent = null; aiRequests.clear()
    }
    private fun clearRevokedConnection(client: CoreApi) {
        client.invalidate()
        if (api !== client) return
        val app = getApplication<Application>()
        if (RecordingService.state.value.recording) app.startService(android.content.Intent(app, RecordingService::class.java).setAction(RecordingService.DISCARD))
        if (LiveVoiceService.state.value.active) app.startService(android.content.Intent(app, LiveVoiceService::class.java).setAction(LiveVoiceService.STOP))
        val clearCredentials = vault.read()?.let { client.uses(it) } == true
        disconnect(eraseAllCaches = false, clearCredentials = clearCredentials)
        val cleanupFailed = practiceStore != null
        if (clearCredentials) vault.clear()
        mutable.value = ScreenState(privacyRevoked = true, notice = if (cleanupFailed) "Core access changed. This connection was closed and private content is hidden, but local file removal failed. Use Sign out and forget local data to retry cleanup."
            else "Core access or its data generation changed. This connection's local practice was cleared and it was closed. Pair again with the current Core.")
    }
}
