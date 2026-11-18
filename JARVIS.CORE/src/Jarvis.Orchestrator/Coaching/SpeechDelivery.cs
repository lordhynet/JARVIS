using System.Text.RegularExpressions;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Coaching;

public static class SpeechDelivery
{
    public const string PcmQuietMethod = "core-pcm-quiet-v1";
    public static void ValidateTiming(SpeechTiming? timing, double? seconds)
    {
        if (timing is null) return;
        Guard.Require(seconds is > 0 && timing.Method == PcmQuietMethod && double.IsFinite(timing.AudioSeconds) && double.IsFinite(timing.QuietSeconds) &&
            timing.AudioSeconds is > 0 and <= 180 && timing.QuietSeconds >= 0 && timing.QuietSeconds <= timing.AudioSeconds &&
            Math.Abs(timing.AudioSeconds - seconds.Value) <= .025, 400, "Speech timing must match bounded captured audio.");
    }
    public static SpeechDeliveryTurn Measure(VoiceTurn turn)
    {
        Guard.Require(turn.Role == ChatRoles.User && !turn.Restricted, 409, "Only unrestricted captured user speech has delivery metrics.");
        ValidateTiming(turn.Timing, turn.SpeechSeconds);
        var text = TextCoach.Analyse(turn.Text, turn.SpeechSeconds);
        var first = Regex.Split(turn.Text.Trim(), @"[.!?\r\n]").FirstOrDefault() ?? "";
        var opening = text.WordCount == 0 ? 0 : (TextCoach.Analyse(first, null).WordCount <= 25 ? 1 : 0) +
            (Regex.IsMatch(first, @"^(?:the (?:main )?(?:point|outcome)|my (?:point|recommendation)|I (?:recommend|chose|decided|owned)|in summary)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? 1 : 0);
        var pronouns = text.FirstPersonSingularCount + text.FirstPersonPluralCount;
        return new(turn.Id, turn.SessionId, turn.At, text.WordCount, turn.SpeechSeconds,
            text.ApproximateWordsPerMinute, text.FillerCandidates, text.FirstPersonSingularCount, text.FirstPersonPluralCount,
            pronouns > 0 ? Math.Round((double)text.FirstPersonSingularCount / pronouns, 3) : null,
            turn.Timing is { } timing ? Math.Round(timing.QuietSeconds / timing.AudioSeconds, 3) : null, opening,
            turn.Timing is not null ? "Core PCM quiet-frame heuristic within provider VAD range" : turn.SpeechSeconds is > 0 ? "Provider VAD duration; pause timing unavailable" : "Timing unavailable");
    }
    public static IReadOnlyList<SpeechDeliverySession> Sessions(RuntimeState state, Guid? sessionId = null) => state.VoiceTurns.Values
        .Where(t => t.Role == ChatRoles.User && !t.Restricted && !state.RetiredSessions.Contains(t.SessionId) && (sessionId is null || t.SessionId == sessionId))
        .GroupBy(t => t.SessionId).OrderByDescending(g => g.Max(t => t.At)).Take(30).Select(group => {
            var turns = group.OrderBy(t => t.At).ThenBy(t => t.Id).Select(Measure).ToArray();
            var timed = turns.Where(t => t.Seconds is > 0).ToArray();
            var pauses = group.Where(t => t.Timing is not null).Select(t => t.Timing!).ToArray();
            return new SpeechDeliverySession(group.Key, turns[^1].At, turns.Length, turns.Sum(t => t.Words), timed.Length,
                timed.Length > 0 ? Math.Round(timed.Sum(t => t.Words) * 60d / timed.Sum(t => t.Seconds!.Value), 1) : null,
                turns.Sum(t => t.Fillers), turns.Sum(t => t.FirstPersonSingular), turns.Sum(t => t.FirstPersonPlural),
                pauses.Length > 0 ? Math.Round(pauses.Sum(t => t.QuietSeconds) / pauses.Sum(t => t.AudioSeconds), 3) : null,
                Math.Round(turns.Average(t => t.OpeningScore), 2), turns.TakeLast(100).ToArray());
        }).ToArray();
}
