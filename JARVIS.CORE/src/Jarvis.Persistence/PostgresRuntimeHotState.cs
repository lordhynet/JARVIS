using System.Text.Json;
using System.Diagnostics;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

// The existing runtime document lock serializes all writers, including these hot rows.
// Voice hydration is deferred until a callback actually reads it, within the same D2 transaction.
internal sealed class PostgresRuntimeHotState
{
    private readonly SessionDbContext db;
    private readonly RuntimeAudioRow audio;
    private readonly AudioLease? lease;
    private readonly AudioLease? drain;
    private readonly DateTimeOffset? drainUntil;
    private readonly Dictionary<string, VoiceTicketRecord> tickets;
    private string originalColdHash;
    private readonly Dictionary<string, string> smallBefore = [];
    private Dictionary<Guid, VoiceTurn>? voices;
    private readonly PostgresRuntimeLearningState learning;
    private PostgresRuntimeHotState(SessionDbContext db, RuntimeAudioRow audio, RuntimeState state,
        Dictionary<string, VoiceTicketRecord> tickets, string fingerprint, bool deferCold)
    {
        this.db = db; this.audio = audio; this.tickets = tickets;
        originalColdHash = fingerprint;
        if (deferCold) state.DeferColdProperties(() => {
            var phase = Stopwatch.GetTimestamp();
            var json = db.RuntimeDocuments.AsNoTracking().Where(r => r.Id == 1).Select(r => r.Json).Single();
            CoreTelemetry.RuntimeBytes.Record(System.Text.Encoding.UTF8.GetByteCount(json));
            var cold = JsonSerializer.Deserialize<RuntimeState>(json)!;
            foreach (var property in typeof(RuntimeState).GetProperties().Where(p => p.CanWrite && !PostgresRuntimeCodec.IsHot(p.Name)))
                property.SetValue(state, property.GetValue(cold));
            if (originalColdHash.Length == 0) originalColdHash = PostgresRuntimeCodec.Fingerprint(PostgresRuntimeCodec.Serialize(state));
            CoreTelemetry.RecordStoragePhase(phase, "runtime.cold.hydrate");
        });
        else if (originalColdHash.Length == 0) originalColdHash = PostgresRuntimeCodec.Fingerprint(PostgresRuntimeCodec.Serialize(state));
        state.AudioLease = lease = JsonSerializer.Deserialize<AudioLease>(audio.LeaseJson);
        state.AudioDrainLease = drain = JsonSerializer.Deserialize<AudioLease>(audio.DrainLeaseJson);
        state.AudioDrainUntil = drainUntil = audio.DrainUntil is null ? null : DateTimeOffset.Parse(audio.DrainUntil, System.Globalization.CultureInfo.InvariantCulture);
        state.NextAudioFence = audio.NextFence;
        state.VoiceTickets = new(tickets);
        state.AgentControls = JsonSerializer.Deserialize<AgentControls>(audio.ControlsJson)!;
        state.Devices = JsonSerializer.Deserialize<Dictionary<string, RegisteredDevice>>(audio.DevicesJson)!;
        state.DeviceCredentials = JsonSerializer.Deserialize<Dictionary<string, DeviceCredential>>(audio.CredentialsJson)!;
        state.WakeCandidateAttempts = JsonSerializer.Deserialize<Dictionary<string, List<DateTimeOffset>>>(audio.WakeAttemptsJson)!;
        foreach (var (key, value) in Small(state)) smallBefore[key] = value;
        state.DeferVoiceTurns(() => new(LoadVoices()));
        learning = new PostgresRuntimeLearningState(db, state);
    }
    private Dictionary<Guid, VoiceTurn> LoadVoices() => voices ??=
        db.RuntimeVoiceTurns.AsNoTracking().ToArray().Select(x => x.Contract()).ToDictionary(x => x.Id);
    public static async Task<PostgresRuntimeHotState> LoadAsync(SessionDbContext db, RuntimeState state, string fingerprint, CancellationToken ct, bool deferCold = false)
    {
        var audio = await db.RuntimeAudioState.SingleAsync(x => x.Id == 1, ct);
        var tickets = (await db.RuntimeVoiceTickets.AsNoTracking().ToArrayAsync(ct))
            .ToDictionary(x => x.Hash, x => JsonSerializer.Deserialize<VoiceTicketRecord>(x.Json)!);
        return new(db, audio, state, tickets, fingerprint, deferCold);
    }
    public void Apply(RuntimeDocument row, RuntimeState state)
    {
        if (state.AreColdPropertiesLoaded) {
            var phase = Stopwatch.GetTimestamp();
            var json = PostgresRuntimeCodec.Serialize(state);
            var hash = PostgresRuntimeCodec.Fingerprint(json);
            if (hash != originalColdHash) { row.Json = json; row.Fingerprint = hash; }
            CoreTelemetry.RecordStoragePhase(phase, "runtime.cold.serialize_hash");
        }
        foreach (var (key, value) in Small(state)) {
            if (smallBefore[key] == value) continue;
            switch (key) {
                case nameof(RuntimeState.AgentControls): audio.ControlsJson = value; break;
                case nameof(RuntimeState.Devices): audio.DevicesJson = value; break;
                case nameof(RuntimeState.DeviceCredentials): audio.CredentialsJson = value; break;
                case nameof(RuntimeState.WakeCandidateAttempts): audio.WakeAttemptsJson = value; break;
            }
        }
        if (lease != state.AudioLease) audio.LeaseJson = JsonSerializer.Serialize(state.AudioLease);
        if (drain != state.AudioDrainLease) audio.DrainLeaseJson = JsonSerializer.Serialize(state.AudioDrainLease);
        if (drainUntil != state.AudioDrainUntil) audio.DrainUntil = state.AudioDrainUntil?.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        audio.NextFence = state.NextAudioFence;
        foreach (var old in tickets.Keys.Except(state.VoiceTickets.Keys)) db.RuntimeVoiceTickets.Remove(new() { Hash = old });
        foreach (var (id, value) in state.VoiceTickets) {
            if (tickets.TryGetValue(id, out var previous) && previous == value) continue;
            var changed = new RuntimeVoiceTicketRow { Hash = id, Json = JsonSerializer.Serialize(value) };
            if (previous is null) db.RuntimeVoiceTickets.Add(changed); else db.RuntimeVoiceTickets.Update(changed);
        }
        learning.Apply(state);
        if (!state.AreVoiceTurnsLoaded) return;
        var before = LoadVoices();
        foreach (var old in before.Keys.Except(state.VoiceTurns.Keys)) db.RuntimeVoiceTurns.Remove(new() { Id = old });
        foreach (var (id, value) in state.VoiceTurns) {
            if (before.TryGetValue(id, out var previous) && previous == value) continue;
            var changed = RuntimeVoiceTurnRow.From(value);
            if (previous is null) db.RuntimeVoiceTurns.Add(changed); else db.RuntimeVoiceTurns.Update(changed);
        }
    }
    // Identity reads are used for exports and detached snapshots; never return a live DB loader.
    public static void Detach<T>(RuntimeState state, T result)
    {
        if (ReferenceEquals(state, result)) { state.LoadColdProperties(); state.LoadLearningProperties(); _ = state.VoiceTurns; }
        state.CloseColdProperties();
        state.CloseVoiceTurns();
        state.CloseLearningProperties();
    }
    private static Dictionary<string, string> Small(RuntimeState state) => new() {
        [nameof(RuntimeState.AgentControls)] = JsonSerializer.Serialize(state.AgentControls),
        [nameof(RuntimeState.Devices)] = JsonSerializer.Serialize(state.Devices),
        [nameof(RuntimeState.DeviceCredentials)] = JsonSerializer.Serialize(state.DeviceCredentials),
        [nameof(RuntimeState.WakeCandidateAttempts)] = JsonSerializer.Serialize(state.WakeCandidateAttempts)
    };
}
