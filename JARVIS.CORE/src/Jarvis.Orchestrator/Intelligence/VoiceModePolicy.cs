using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Intelligence;

public static class VoiceModePolicy
{
    public const string Version = "voice-mode-v1";

    public static VoiceMode Recommend(ActivityType activity, TrainingMode? drill = null)
    {
        if (drill is TrainingMode.RapidDrill or TrainingMode.SpacedReview or TrainingMode.ExplainSimply)
            return VoiceMode.Everyday;
        if (drill is not null || activity is ActivityType.MockInterview or ActivityType.SystemDesign or ActivityType.Communication)
            return VoiceMode.Serious;
        return VoiceMode.Everyday;
    }
}
