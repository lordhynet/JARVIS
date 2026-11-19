namespace Jarvis.Contracts;

public sealed record SpeechTiming(double AudioSeconds, double QuietSeconds, string Method);
public sealed record SpeechDeliveryTurn(Guid TurnId, Guid SessionId, DateTimeOffset At, int Words, double? Seconds,
    double? WordsPerMinute, int Fillers, int FirstPersonSingular, int FirstPersonPlural, double? PersonalPronounShare,
    double? PauseRatio, int OpeningScore, string TimingSource);
public sealed record SpeechDeliverySession(Guid SessionId, DateTimeOffset LastAt, int Turns, int Words, int TimedTurns,
    double? WordsPerMinute, int Fillers, int FirstPersonSingular, int FirstPersonPlural, double? PauseRatio,
    double OpeningScore, IReadOnlyList<SpeechDeliveryTurn> Trend);
