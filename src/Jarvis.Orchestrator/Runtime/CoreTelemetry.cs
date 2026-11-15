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
    public static readonly Counter<long> WakeCandidates = Meter.CreateCounter<long>("jarvis.voice.wake_candidates");
    public static readonly Counter<long> WakeSilenceStops = Meter.CreateCounter<long>("jarvis.voice.wake_silence_stops");
    public static readonly Histogram<double> RuntimeLockWait = Meter.CreateHistogram<double>("jarvis.runtime.lock_wait", "s");
    public static readonly Histogram<long> RuntimeBytes = Meter.CreateHistogram<long>("jarvis.runtime.bytes", "By");
    public static readonly Counter<long> RuntimeWrites = Meter.CreateCounter<long>("jarvis.runtime.writes");
    public static readonly Counter<long> RuntimeNoOpWrites = Meter.CreateCounter<long>("jarvis.runtime.no_op_writes");
}
