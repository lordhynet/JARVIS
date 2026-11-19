using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Jarvis.Orchestrator.Runtime;

// Tags are fixed categories. Never attach prompts, transcripts, keys, URLs or resource IDs.
public static class CoreTelemetry
{
    public const string Name = "Jarvis.Core";
    public static readonly ActivitySource Activities = new(Name);
    public static readonly Meter Meter = new(Name);
    public static readonly Histogram<double> Requests = Meter.CreateHistogram<double>("jarvis.request.duration", "s");
    public static readonly Histogram<double> ProviderDuration = Meter.CreateHistogram<double>("jarvis.provider.duration", "s");
    public static readonly Counter<long> ProviderCalls = Meter.CreateCounter<long>("jarvis.provider.calls");
    public static readonly Histogram<double> QueueAge = Meter.CreateHistogram<double>("jarvis.agent.queue_age", "s");
    public static readonly Counter<long> AgentClaims = Meter.CreateCounter<long>("jarvis.agent.claims");
    public static readonly Counter<long> AgentCompletions = Meter.CreateCounter<long>("jarvis.agent.completions");
    public static readonly Counter<long> LeaseRecoveries = Meter.CreateCounter<long>("jarvis.agent.lease_recoveries");
    public static readonly Histogram<double> ScheduleDelay = Meter.CreateHistogram<double>("jarvis.schedule.delay", "s");
    public static readonly Histogram<double> VoiceFirstAudio = Meter.CreateHistogram<double>("jarvis.voice.first_audio", "s");
    // Voice latency from Core's side. Tags are fixed categories only, never transcripts, sessions or device IDs.
    public static readonly Histogram<double> VoicePrepare = Meter.CreateHistogram<double>("jarvis.voice.prepare", "s");
    public static readonly Histogram<double> VoiceTicketToReady = Meter.CreateHistogram<double>("jarvis.voice.ticket_to_ready", "s");
    public static readonly Histogram<double> VoiceTicketToFirstAudio = Meter.CreateHistogram<double>("jarvis.voice.ticket_to_first_audio", "s");
    public static readonly Counter<long> VoiceEnded = Meter.CreateCounter<long>("jarvis.voice.ended");

    private static readonly HashSet<string> EndReasons = new(StringComparer.Ordinal) {
        "stopped", "window_complete", "paid_ai_paused", "owner_closed", "idle", "device_locked", "serious_daily_cap",
        "gbp_budget_ceiling", "voice_mode_declined", "serious_voice_unavailable", "rate_limited" };
    public static void RecordVoiceEnded(string? reason) =>
        VoiceEnded.Add(1, new KeyValuePair<string, object?>("reason", reason is not null && EndReasons.Contains(reason) ? reason : "other"));
    public static readonly Counter<long> WakeCandidates = Meter.CreateCounter<long>("jarvis.voice.wake_candidates");
    public static readonly Counter<long> WakeSilenceStops = Meter.CreateCounter<long>("jarvis.voice.wake_silence_stops");
    public static readonly Histogram<double> RuntimeLockWait = Meter.CreateHistogram<double>("jarvis.runtime.lock_wait", "s");
    public static readonly Histogram<long> RuntimeBytes = Meter.CreateHistogram<long>("jarvis.runtime.bytes", "By");
    public static readonly Counter<long> RuntimeWrites = Meter.CreateCounter<long>("jarvis.runtime.writes");
    public static readonly Counter<long> RuntimeNoOpWrites = Meter.CreateCounter<long>("jarvis.runtime.no_op_writes");
    public static readonly Histogram<double> StoragePhaseDuration = Meter.CreateHistogram<double>("jarvis.storage.phase.duration", "s");

    public static long RecordStoragePhase(long started, string phase)
    {
        var now = Stopwatch.GetTimestamp();
        if (StoragePhaseDuration.Enabled)
            StoragePhaseDuration.Record(Stopwatch.GetElapsedTime(started, now).TotalSeconds, new KeyValuePair<string, object?>("phase", phase));
        return now;
    }
}
