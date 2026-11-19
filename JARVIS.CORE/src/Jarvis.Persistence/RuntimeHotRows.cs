using System.Text.Json;
using Jarvis.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

public sealed class RuntimeAudioRow
{
    public int Id { get; set; } = 1;
    public string LeaseJson { get; set; } = "null";
    public string DrainLeaseJson { get; set; } = "null";
    public string? DrainUntil { get; set; }
    public long NextFence { get; set; } = 1;
    public string ControlsJson { get; set; } = "{}";
    public string DevicesJson { get; set; } = "{}";
    public string CredentialsJson { get; set; } = "{}";
    public string WakeAttemptsJson { get; set; } = "{}";
}
public sealed class RuntimeVoiceTicketRow
{
    public string Hash { get; set; } = "";
    public string Json { get; set; } = "{}";
}
public sealed class RuntimeVoiceTurnRow
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
    public double? SpeechSeconds { get; set; }
    public DateTimeOffset At { get; set; }
    // Preserve legacy sub-microsecond ticks and offsets used in provenance hashes.
    public string AtText { get; set; } = "";
    public bool Restricted { get; set; }
    public string? TimingJson { get; set; }
    public VoiceTurn Contract() => new(Id, SessionId, Role, Text, SpeechSeconds, DateTimeOffset.Parse(AtText, System.Globalization.CultureInfo.InvariantCulture), Restricted,
        TimingJson is null ? null : JsonSerializer.Deserialize<SpeechTiming>(TimingJson));
    public static RuntimeVoiceTurnRow From(VoiceTurn value) => new() { Id = value.Id, SessionId = value.SessionId, Role = value.Role, Text = value.Text,
        SpeechSeconds = value.SpeechSeconds, At = value.At.ToUniversalTime(), AtText = value.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture), Restricted = value.Restricted, TimingJson = value.Timing is null ? null : JsonSerializer.Serialize(value.Timing) };
}
internal static class RuntimeHotModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<RuntimeAudioRow>(b => {
            b.ToTable("RuntimeAudioState"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.LeaseJson).HasColumnType("jsonb"); b.Property(x => x.DrainLeaseJson).HasColumnType("jsonb");
            b.Property(x => x.ControlsJson).HasColumnType("jsonb").HasDefaultValue("{}");
            b.Property(x => x.DevicesJson).HasColumnType("jsonb").HasDefaultValue("{}");
            b.Property(x => x.CredentialsJson).HasColumnType("jsonb").HasDefaultValue("{}");
            b.Property(x => x.WakeAttemptsJson).HasColumnType("jsonb").HasDefaultValue("{}");
            b.HasData(new RuntimeAudioRow());
        });
        model.Entity<RuntimeVoiceTicketRow>(b => { b.ToTable("RuntimeVoiceTickets"); b.HasKey(x => x.Hash); b.Property(x => x.Hash).HasMaxLength(128); b.Property(x => x.Json).HasColumnType("jsonb"); });
        model.Entity<RuntimeVoiceTurnRow>(b => {
            b.ToTable("RuntimeVoiceTurns"); b.HasKey(x => x.Id); b.HasIndex(x => new { x.SessionId, x.At });
            b.Property(x => x.Role).HasMaxLength(20); b.Property(x => x.Text).HasMaxLength(8000); b.Property(x => x.TimingJson).HasColumnType("jsonb");
        });
    }
}
