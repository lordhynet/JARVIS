namespace Jarvis.Api;

public static class ConfigKeys
{
    public const string CoreApiKey = "Jarvis:ApiKey";
    public const string OwnerKey = "BudgetManagement:OwnerKey";
    public const string OwnerSubjectId = "BudgetManagement:SubjectId";
    public const string Budget = "Budget";
    public const string BudgetFx = "Fx";
    public const string BudgetFxAuto = "FxAuto";
    public const string BudgetFxRefreshHours = "RefreshHours";
    public const string BudgetFxMarginPercent = "MarginPercent";
    public const string BudgetFxMaxPublicationAgeDays = "MaxPublicationAgeDays";
    public const string OpenAiApiKey = "OpenAI:ApiKey";
    public const string OpenAiModel = "OpenAI:Model";
    public const string OpenAiTranscriptionModel = "OpenAI:TranscriptionModel";
    public const string AnthropicApiKey = "Anthropic:ApiKey";
    public const string AnthropicModel = "Anthropic:Model";

    public const string PaidAiPrefix = "PaidAI:";
    public const string PaidAiEnabled = PaidAiPrefix + "Enabled";
    public const string DailyLimitUsd = PaidAiPrefix + "DailyLimitUsd";
    public const string MonthlyLimitUsd = PaidAiPrefix + "MonthlyLimitUsd";
    public const string BackgroundLimitUsd = PaidAiPrefix + "BackgroundLimitUsd";
    public const string VoiceLimitUsd = "AI:VoiceLimitUsd";
    public const string InteractiveLimitUsd = "AI:InteractiveLimitUsd";

    public const string VoiceProfile = "AI:Voice:";
    public const string VoiceModes = VoiceProfile + "Modes:";
    public const string VoiceSeriousDailyGbpField = "VoiceSeriousDailyGbp";
    public const string VoiceSeriousDailyGbp = "Budget:" + VoiceSeriousDailyGbpField;
    public const string EverydayRealtimeModel = "gpt-realtime-2.1-mini";
    public const string SeriousRealtimeModel = "gpt-realtime-2.1";
    public const string Enabled = "Enabled";
    public const string VoicePersona = VoiceProfile + "Persona";
    public const string TranscriptionProfile = "AI:Transcription:";
    public const string Model = "Model";
    public const string VoiceName = VoiceProfile + "VoiceName";
    public const string VoiceSpeed = VoiceProfile + "Speed";
    public const string VoiceWindowSeconds = VoiceProfile + "WindowSeconds";
    public const string VoiceTurnDetectionMode = VoiceProfile + "TurnDetection:Mode";
    public const string VoiceTurnDetectionEagerness = VoiceProfile + "TurnDetection:Eagerness";
    public const string Provider = "Provider";
    public const string MaxOutputTokens = "MaxOutputTokens";
    public const string RateCardModel = "RateCardModel";
    public const string RateCardDate = "RateCardDate";

    public static string AiProfilePrefix(string profile) => $"AI:{profile}:";
}
