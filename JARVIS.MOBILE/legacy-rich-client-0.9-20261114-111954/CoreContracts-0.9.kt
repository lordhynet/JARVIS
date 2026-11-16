// Generated from Jarvis.Contracts by Jarvis.ContractGen. Do not edit.
package uk.jarvis.mobile.contracts

enum class ActivityType { General, Briefing, LeetCode, SystemDesign, MockInterview, Communication, French, WindDown }

data class AddCheckpointRequest(
    val summary: String = "",
    val currentTask: String? = null,
)

data class AgentControls(
    val paused: Boolean = false,
    val pausedWorkflows: List<String>? = null,
    val quietStart: String = "",
    val quietEnd: String = "",
    val timeZone: String = "",
)

data class AgentNotification(
    val id: String = "",
    val runId: String = "",
    val title: String = "",
    val createdAt: String = "",
    val deliverAfter: String = "",
    val expiresAt: String = "",
    val delivered: Boolean = false,
    val read: Boolean = false,
)

enum class AgentRole { InterviewCoach, TechnicalTrainer, CommunicationCoach, Research, Review, CareerPlanner, PersonalPlanner }

data class AgentRun(
    val id: String = "",
    val scheduleId: String? = null,
    val occurrence: String = "",
    val workflow: String = "",
    val version: Int = 0,
    val allowCloudProcessing: Boolean = false,
    val createdAt: String = "",
    val deadline: String = "",
    val status: String = "",
    val taskIds: List<String> = emptyList(),
    val deletedAt: String? = null,
)

data class AgentRunView(
    val run: AgentRun = AgentRun(),
    val tasks: List<AgentTaskView> = emptyList(),
)

data class AgentSchedule(
    val id: String = "",
    val title: String = "",
    val workflow: String = "",
    val timeZone: String = "",
    val localTime: String = "",
    val enabled: Boolean = false,
    val allowCloudProcessing: Boolean = false,
    val revision: Int = 0,
    val createdAt: String = "",
    val lastOccurrence: String? = null,
)

enum class AgentTaskStatus { Queued, Running, RetryScheduled, Succeeded, Failed, Cancelled, NeedsReconciliation }

data class AgentTaskView(
    val id: String = "",
    val runId: String = "",
    val role: AgentRole = AgentRole.InterviewCoach,
    val status: AgentTaskStatus = AgentTaskStatus.Queued,
    val attempt: Int = 0,
    val output: String? = null,
    val errorCode: String? = null,
)

data class AiAssessmentRequest(
    val requestId: String = "",
    val expectedRevision: Int = 0,
    val textRevision: Int = 0,
    val allowCloudProcessing: Boolean = false,
    val timeZone: String = "",
)

data class AiCapabilityStatus(
    val ready: Boolean = false,
    val model: String = "",
    val detail: String = "",
)

data class AiCoachAssessment(
    val id: String = "",
    val attemptId: String = "",
    val answerVersion: Int = 0,
    val scores: List<DimensionScore> = emptyList(),
    val summary: String = "",
    val followUps: List<String> = emptyList(),
    val model: String = "",
    val at: String = "",
)

enum class AssessmentDimension { TechnicalAccuracy, Specificity, PersonalOwnership, ArchitectureReasoning, TradeOffs, CommunicationStructure, RecallStability }

data class AudioLease(
    val sessionId: String = "",
    val deviceId: String = "",
    val fence: Long = 0L,
    val expiresAt: String = "",
)

data class CancelTrainingDrillRequest(
    val expectedRevision: Int = 0,
)

data class ChatMessage(
    val requestId: String = "",
    val role: String = "",
    val text: String = "",
    val at: String = "",
)

data class ChatRequest(
    val requestId: String = "",
    val text: String = "",
)

data class CoachingAnalysis(
    val wordCount: Int = 0,
    val sentenceCount: Int = 0,
    val fillerCandidates: Int = 0,
    val firstPersonSingularCount: Int = 0,
    val firstPersonPluralCount: Int = 0,
    val reportedDurationSeconds: Double? = null,
    val approximateWordsPerMinute: Double? = null,
    val timingSource: String = "",
    val observations: List<TextObservation> = emptyList(),
    val nextDrills: List<String> = emptyList(),
    val ruleVersion: String = "",
)

data class CoachingAnswerVersion(
    val version: Int = 0,
    val text: String = "",
    val reportedDurationSeconds: Double? = null,
    val at: String = "",
)

enum class CoachingArea { Communication, Interview }

data class CoachingAttempt(
    val id: String = "",
    val sessionId: String = "",
    val exerciseId: String = "",
    val exerciseVersion: Int = 0,
    val storyId: String? = null,
    val storyVersion: Int? = null,
    val claimIndex: Int? = null,
    val revision: Int = 0,
    val createdAt: String = "",
    val answers: List<CoachingAnswerVersion> = emptyList(),
    val deletedAt: String? = null,
    val aiAssessmentId: String? = null,
)

data class CoachingAttemptSummary(
    val id: String = "",
    val sessionId: String = "",
    val exerciseId: String = "",
    val title: String = "",
    val revision: Int = 0,
    val createdAt: String = "",
    val wordCount: Int = 0,
    val sourceCurrent: Boolean = false,
)

data class CoachingAttemptView(
    val attempt: CoachingAttempt = CoachingAttempt(),
    val exercise: CoachingExercise = CoachingExercise(),
    val analysis: CoachingAnalysis = CoachingAnalysis(),
    val sourceCurrent: Boolean = false,
    val aiAssessment: AiCoachAssessment? = null,
)

data class CoachingExercise(
    val id: String = "",
    val version: Int = 0,
    val area: CoachingArea = CoachingArea.Communication,
    val title: String = "",
    val prompt: String = "",
    val framework: List<String> = emptyList(),
    val followUps: List<String> = emptyList(),
    val targetSeconds: Int? = null,
    val requiresStory: Boolean = false,
    val requiresClaim: Boolean = false,
)

data class CoachingPrompt(
    val exercise: CoachingExercise = CoachingExercise(),
    val question: String = "",
    val storyId: String? = null,
    val storyVersion: Int? = null,
    val claimIndex: Int? = null,
)

data class ConversationSession(
    val id: String = "",
    val activity: ActivityType = ActivityType.General,
    val title: String = "",
    val status: SessionStatus = SessionStatus.Active,
    val activeDeviceId: String? = null,
    val currentTask: String? = null,
    val createdAt: String = "",
    val updatedAt: String = "",
    val checkpoints: List<SessionCheckpoint> = emptyList(),
    val handoff: SessionHandoff? = null,
    val messages: List<ChatMessage>? = null,
)

data class CorrectCoachingAnswerRequest(
    val requestId: String = "",
    val expectedRevision: Int = 0,
    val text: String = "",
    val reportedDurationSeconds: Double? = null,
)

data class CorrectTrainingAnswerRequest(
    val requestId: String = "",
    val expectedRevision: Int = 0,
    val text: String = "",
)

data class CreateInterviewBriefRequest(
    val requestId: String = "",
    val allowCloudProcessing: Boolean = false,
)

data class CreatePracticePlanRequest(
    val requestId: String = "",
    val notBefore: String? = null,
)

data class CreateSessionRequest(
    val activity: ActivityType = ActivityType.General,
    val title: String? = null,
    val deviceId: String? = null,
)

data class CreateStoryRequest(
    val requestId: String = "",
    val content: StoryContent = StoryContent(),
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

data class DimensionScore(
    val dimension: AssessmentDimension = AssessmentDimension.TechnicalAccuracy,
    val value: Int? = null,
    val evidence: String? = null,
    val rationale: String? = null,
)

data class DrillModeInfo(
    val mode: TrainingMode = TrainingMode.RapidDrill,
    val title: String = "",
    val description: String = "",
)

enum class HandoffState { Requested, Checkpointed, TargetReady, AudioTransferred, Completed, TimedOut, Failed }

data class ImprovementProgramme(
    val id: String = "",
    val localStartDate: String = "",
    val timeZone: String = "",
    val revision: Int = 0,
    val createdAt: String = "",
    val checkIns: List<ProgrammeCheckIn> = emptyList(),
    val deletedAt: String? = null,
)

data class InterviewBrief(
    val summary: String = "",
    val drills: List<String> = emptyList(),
    val sourceIds: List<String> = emptyList(),
    val model: String = "",
    val generatedAt: String = "",
)

enum class LearningDomain { LeetCode, SystemDesign, French, InterviewStory, Communication, CSharpDotNet, DistributedSystems, Databases, ApiSecurity, ProductionDevOps, PaymentsFintech }

data class LearningItem(
    val id: String = "",
    val domain: LearningDomain = LearningDomain.LeetCode,
    val prompt: String = "",
    val answer: String = "",
    val stability: Double = 0.0,
    val difficulty: Double = 0.0,
    val dueAt: String = "",
    val createdAt: String = "",
    val masteryScore: Double? = null,
    val strongStreak: Int = 0,
    val schedulingRuleVersion: String = "",
    val masterySource: String = "",
)

data class PaidAiStatus(
    val ready: Boolean = false,
    val detail: String = "",
    val dailyLimitUsd: Double = 0.0,
    val monthlyLimitUsd: Double = 0.0,
    val backgroundLimitUsd: Double = 0.0,
    val todayCommittedUsd: Double = 0.0,
    val monthCommittedUsd: Double = 0.0,
    val uncertainCharges: Int = 0,
)

data class PersonalGoal(
    val id: String = "",
    val revision: Int = 0,
    val title: String = "",
    val brief: String = "",
    val workflow: String = "",
    val allowCloudProcessing: Boolean = false,
    val nextReviewAt: String = "",
    val deadline: String = "",
    val status: String = "",
    val createdAt: String = "",
    val updatedAt: String = "",
    val lastRunId: String? = null,
)

data class PracticePlan(
    val steps: List<PracticeStep> = emptyList(),
    val gaps: List<String> = emptyList(),
)

data class PracticeStep(
    val kind: String = "",
    val resourceId: String = "",
    val prompt: String = "",
)

data class PrivacyStatus(
    val epoch: Long = 0L,
    val revision: Int = 0,
    val erased: Boolean = false,
    val erasedAt: String? = null,
    val erasureRequestId: String? = null,
)

data class ProductModule(
    val id: String = "",
    val title: String = "",
    val status: String = "",
    val available: List<String> = emptyList(),
    val remaining: List<String> = emptyList(),
)

data class ProgrammeCheckIn(
    val id: String = "",
    val week: Int = 0,
    val note: String = "",
    val at: String = "",
)

data class ProgrammeCheckInRequest(
    val requestId: String = "",
    val expectedRevision: Int = 0,
    val week: Int = 0,
    val note: String? = null,
)

data class ProgrammeView(
    val programme: ImprovementProgramme? = null,
    val currentWeek: Int = 0,
    val weeks: List<ProgrammeWeek> = emptyList(),
    val progressSource: String = "",
)

data class ProgrammeWeek(
    val number: Int = 0,
    val focus: String = "",
    val tasks: List<String> = emptyList(),
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

data class ReviewLearningRequest(
    val requestId: String = "",
    val rating: ReviewRating = ReviewRating.Again,
    val responseSeconds: Double = 0.0,
)

enum class ReviewRating { Again, Hard, Good, Easy }

data class SaveAgentScheduleRequest(
    val requestId: String = "",
    val expectedRevision: Int? = null,
    val title: String = "",
    val workflow: String = "",
    val timeZone: String = "",
    val localTime: String = "",
    val enabled: Boolean = false,
    val allowCloudProcessing: Boolean = false,
)

data class SaveCoachingAnswerRequest(
    val requestId: String = "",
    val sessionId: String = "",
    val exerciseId: String = "",
    val exerciseVersion: Int = 0,
    val text: String = "",
    val storyId: String? = null,
    val storyVersion: Int? = null,
    val claimIndex: Int? = null,
    val reportedDurationSeconds: Double? = null,
)

data class SaveGoalRequest(
    val requestId: String = "",
    val expectedRevision: Int? = null,
    val title: String = "",
    val brief: String = "",
    val workflow: String = "",
    val allowCloudProcessing: Boolean = false,
    val nextReviewAt: String = "",
    val deadline: String = "",
    val status: String = "",
)

data class SelfAssessmentRequest(
    val requestId: String = "",
    val expectedRevision: Int = 0,
    val textRevision: Int = 0,
    val scores: List<DimensionScore>? = null,
    val timeZone: String = "",
)

data class SessionCheckpoint(
    val id: String = "",
    val sessionId: String = "",
    val sequence: Int = 0,
    val summary: String = "",
    val currentTask: String? = null,
    val createdAt: String = "",
)

data class SessionHandoff(
    val id: String = "",
    val fromDeviceId: String = "",
    val toDeviceId: String = "",
    val state: HandoffState = HandoffState.Requested,
    val requestedAt: String = "",
    val expiresAt: String = "",
)

data class SessionReview(
    val sessionId: String = "",
    val title: String = "",
    val fingerprint: String = "",
    val technicalAttempts: Int = 0,
    val skippedQuestions: Int = 0,
    val coachingAttempts: Int = 0,
    val userMessages: Int = 0,
    val questions: List<String> = emptyList(),
    val selfRatedDomains: List<WeakDomain> = emptyList(),
    val textObservations: List<TextObservation> = emptyList(),
    val nextDrills: List<String> = emptyList(),
    val assessmentSource: String = "",
)

enum class SessionStatus { Active, Paused, Ended }

data class StartAgentRunRequest(
    val requestId: String = "",
    val workflow: String = "",
    val allowCloudProcessing: Boolean = false,
    val brief: String? = null,
)

data class StartProgrammeRequest(
    val requestId: String = "",
    val localStartDate: String = "",
    val timeZone: String = "",
)

data class StartTrainingDrillRequest(
    val requestId: String = "",
    val sessionId: String = "",
    val mode: TrainingMode = TrainingMode.RapidDrill,
    val questionId: String? = null,
)

data class Story(
    val id: String = "",
    val content: StoryContent = StoryContent(),
    val version: Int = 0,
    val createdAt: String = "",
    val updatedAt: String = "",
)

data class StoryContent(
    val title: String = "",
    val companyProject: String = "",
    val serviceFeature: String = "",
    val context: String = "",
    val problem: String = "",
    val constraints: String = "",
    val personalOwnership: String = "",
    val technicalDecisions: String = "",
    val tradeOffs: String = "",
    val implementation: String = "",
    val collaboration: String = "",
    val failureModes: String = "",
    val result: String = "",
    val metrics: String = "",
    val changeToday: String = "",
    val followUpQuestions: List<String>? = null,
    val cvClaims: List<String>? = null,
)

data class SubmitTrainingAnswerRequest(
    val requestId: String = "",
    val sessionId: String = "",
    val questionId: String = "",
    val questionVersion: Int = 0,
    val mode: TrainingMode = TrainingMode.RapidDrill,
    val text: String? = null,
    val skipped: Boolean = false,
    val drillId: String? = null,
    val stepIndex: Int? = null,
)

data class TextObservation(
    val code: String = "",
    val detail: String = "",
    val evidence: List<String> = emptyList(),
)

data class TrainingAnswerVersion(
    val revision: Int = 0,
    val text: String = "",
    val source: String = "",
    val at: String = "",
)

data class TrainingAssessment(
    val id: String = "",
    val version: Int = 0,
    val textRevision: Int = 0,
    val source: String = "",
    val rubricVersion: String = "",
    val scores: List<DimensionScore> = emptyList(),
    val contentScore: Double? = null,
    val timeZone: String = "",
    val at: String = "",
)

data class TrainingAttempt(
    val id: String = "",
    val sessionId: String = "",
    val questionId: String = "",
    val questionVersion: Int = 0,
    val mode: TrainingMode = TrainingMode.RapidDrill,
    val sequence: Long = 0L,
    val revision: Int = 0,
    val skipped: Boolean = false,
    val submittedAt: String = "",
    val answers: List<TrainingAnswerVersion> = emptyList(),
    val assessments: List<TrainingAssessment> = emptyList(),
    val canonicalAssessmentId: String? = null,
    val deletedAt: String? = null,
    val drillId: String? = null,
    val stepIndex: Int? = null,
    val currentAnswer: String = "",
    val textRevision: Int = 0,
    val canonicalAssessment: TrainingAssessment? = null,
    val wordsPerMinute: Double? = null,
    val answerDurationMs: Long? = null,
)

data class TrainingAttemptSummary(
    val id: String = "",
    val sessionId: String = "",
    val questionId: String = "",
    val revision: Int = 0,
    val skipped: Boolean = false,
    val submittedAt: String = "",
    val contentScore: Double? = null,
    val assessmentSource: String = "",
)

data class TrainingDashboard(
    val completedAttempts: Int = 0,
    val skippedAttempts: Int = 0,
    val practiceDays: Int = 0,
    val dueQuestions: Int = 0,
    val weakDomains: List<WeakDomain> = emptyList(),
    val recentAttempts: List<TrainingAttemptSummary> = emptyList(),
    val timeZone: String = "",
)

data class TrainingDrill(
    val id: String = "",
    val sessionId: String = "",
    val mode: TrainingMode = TrainingMode.RapidDrill,
    val requestedQuestionId: String? = null,
    val revision: Int = 0,
    val status: String = "",
    val createdAt: String = "",
    val steps: List<TrainingDrillStep> = emptyList(),
    val attemptIds: List<String> = emptyList(),
)

data class TrainingDrillStep(
    val index: Int = 0,
    val questionId: String = "",
    val questionVersion: Int = 0,
    val title: String = "",
    val prompt: String = "",
    val reference: String = "",
    val sourceUrl: String = "",
    val codeSnippet: String? = null,
    val suggestedSeconds: Int? = null,
)

data class TrainingDrillView(
    val drill: TrainingDrill = TrainingDrill(),
    val answers: List<TrainingAttempt> = emptyList(),
)

enum class TrainingMode { RapidDrill, ExplainSimply, SpacedReview, DeepDive, ArchitectureChallenge, FailureInjection, ProductionIncident, CodeReview, RandomMixedInterview }

data class TrainingQuestion(
    val id: String = "",
    val version: Int = 0,
    val domain: LearningDomain = LearningDomain.LeetCode,
    val title: String = "",
    val prompt: String = "",
    val reference: String = "",
    val followUps: List<String> = emptyList(),
    val sourceUrl: String = "",
)

data class UpdateStoryRequest(
    val expectedVersion: Int = 0,
    val content: StoryContent = StoryContent(),
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

data class WeakDomain(
    val domain: LearningDomain = LearningDomain.LeetCode,
    val meanScore: Double = 0.0,
    val ratedQuestions: Int = 0,
    val assessmentSource: String = "",
)

data class WorkflowRunView(
    val id: String = "",
    val kind: String = "",
    val workflowVersion: Int = 0,
    val status: WorkflowStatus = WorkflowStatus.Queued,
    val attempt: Int = 0,
    val revision: Int = 0,
    val createdAt: String = "",
    val updatedAt: String = "",
    val expiresAt: String = "",
    val availableAt: String? = null,
    val errorCode: String? = null,
    val result: PracticePlan? = null,
    val brief: InterviewBrief? = null,
)

enum class WorkflowStatus { Queued, Running, RetryScheduled, Succeeded, Failed, Cancelled, Expired }

