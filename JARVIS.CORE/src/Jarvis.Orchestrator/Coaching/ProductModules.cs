using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Coaching;

public static class ProductModules
{
    public static IReadOnlyList<ProductModule> All { get; } = Array.AsReadOnly(new[]
    {
        M("sessions", "Sessions & memory", "Incremental", ["Canonical sessions", "Checkpoints and device handoff", "PostgreSQL history", "On-demand session reviews"], ["OIDC and user isolation", "Complete retention/deletion policy"]),
        M("voice", "Voice & earphones", "Incremental", ["User-started realtime audio and barge-in", "Global expiring audio ownership", "Core-owned conversation preparation and context tools", "Transcript-backed technical practice and headset Stop controls"], ["Additional voice feature workflows", "Live provider acceptance", "Earbud/locked-screen and away-network acceptance"]),
        M("communication", "Communication coach", "Initial", ["Eight structured communication exercises", "Versioned answers", "Evidence-linked text observations", "Optional explicitly reported elapsed timing"], ["Measured acoustic pauses and false starts", "Calibrated speech feedback"]),
        M("interview", "Interview & CV coach", "Initial", ["Story retelling", "Saved CV claim defence", "Interviewer role prompts", "Session reviews and follow-up suggestions"], ["Adaptive provider questioning", "Calibrated technical/factual evaluation"]),
        M("technical", "Technical trainer", "Incremental", ["16 questions across eight domains", "Nine guided modes with saved stages and authored scenarios", "Saved answers and self/AI assessments", "Correction-aware shared review schedule"], ["Complete technical catalog and broader scenario coverage", "Calibrated adaptive interview evaluation", "Code execution sandbox"]),
        M("stories", "Story Bank", "Incremental", ["Real user-authored facts", "Version-checked edits", "Text CV import and source-quote provenance", "Deletion purges linked coaching answers"], ["Binary document extraction", "Automatic story coaching"]),
        M("learning", "Learning & weakness tracking", "Incremental", ["Shared due cards", "Core-owned voice recall with explicit spoken ratings", "1/3/7/14-day content self-rating intervals", "Self-rated domain progress"], ["Calibrated multidimensional assessment", "Scheduler evaluation"]),
        M("programme", "12-week programme", "Initial", ["Twelve weekly focus plans", "Time-zone-aware calendar", "Persistent explicit weekly check-ins"], ["Reminders and adaptive replanning"]),
        M("agents", "Governed AI workflows", "Incremental", ["Durable specialist tasks and leases", "Daily schedules, quiet-hour inbox and calendar trigger sync", "Unpaid practice plans and consented AI drafts", "Shared budgets and usage reconciliation API"], ["Dedicated specialist task tables", "Configured event/provider acceptance", "Provider billing reconciliation UI"]),
        M("integrations", "Daily briefing & home", "Incremental", ["Calendar/transit adapters", "Allowlisted confirmed home actions", "Audit and partial-failure reporting"], ["User-configured provider live acceptance", "Proactive delivery"]),
        M("android", "Android companion", "Incremental", ["Signed audio-only APK", "Encrypted Core connection", "Foreground live microphone and playback", "Core-owned conversations and practice; no native feature editors"], ["OIDC sign-in", "Persistent away-from-home connection", "Full native hardware acceptance"]),
        M("archive", "Real-interview archive", "Incremental", ["Explicit-consent text transcript import", "Provided segment timing and speaker validation", "Transcript review and consented AI assessment"], ["Audio file archive/import", "Automatic diarization", "Acoustic and retention acceptance"]),
        M("research", "Research & review specialists", "Incremental", ["Budgeted public web search", "Citation-bearing research artifacts", "Separate review task"], ["Claim-level source evidence", "Independent source verification", "Live provider acceptance"]),
        M("career", "Career & personal planning", "Incremental", ["Consented goal briefs", "Persistent revision-checked goals", "Daily schedules and specialist review"], ["Voice goal editing", "Policy-gated external tools", "Adaptive planning evaluation"]),
        M("quest", "Quest & room voice", "Planned", [], ["Quest native client", "Far-field satellite firmware", "Hardware routing and interruption acceptance"])
    });
    private static ProductModule M(string id, string title, string status, string[] available, string[] remaining) => new(id, title, status, available, remaining);
}
