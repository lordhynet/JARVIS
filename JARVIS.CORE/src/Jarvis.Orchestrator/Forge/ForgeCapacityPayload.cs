using System.Text.Json.Serialization;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Forge;

public enum ForgeCapacityBlockKind { Work = 1, Sleep, Quiet, Other }
public enum ForgeCapacityOrigin { OwnerEstimate = 1, OwnerObserved }

// Plaintext within the authorized request only. Serialize for protection or explicit owner export,
// never into generic events, model context or device/display snapshots. No public API contract.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ForgeCapacityFixedBlock(DateTime LocalStart, DateTime LocalEnd,
    TimeSpan StartOffset, TimeSpan EndOffset, ForgeCapacityBlockKind? Kind);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ForgeCapacityPayload(int Version, DateOnly LocalDate, string TimeZone,
    int? NetUsableMinutes, int? BufferMinutes, IReadOnlyList<ForgeCapacityFixedBlock>? FixedBlocks,
    bool OwnerConsent, ForgeCapacityOrigin? Origin, DateTimeOffset? ObservedAt, string? Note)
{
    public const int SchemaVersion = 1;

    // Validation/normalization grants no authority or recovery readiness. Callers must check
    // owner access and the current programme/epoch inside the existing transaction before storage.
    public ForgePreparedCapacity Prepare(ForgeProgramme programme, int capacityRevision, DateTimeOffset now)
    {
        Guard.Require(programme is { DeletedAt: null }, 409, "Forge programme is unavailable.");
        Guard.Require(Version == SchemaVersion && OwnerConsent && TimeZone == programme.TimeZone &&
            (Origin is null || Enum.IsDefined(Origin.Value)) &&
            (ObservedAt is null || ObservedAt > DateTimeOffset.MinValue && ObservedAt <= now),
            400, "Capacity payload version, consent or provenance is invalid.");
        Guard.Require(FixedBlocks is null || FixedBlocks.Count <= 64, 400, "Too many fixed capacity blocks.");
        var blocks = FixedBlocks?.ToArray(); // Typed records only; no raw plaintext buffer copy.
        if (blocks is not null) Guard.Require(blocks.All(x => x is not null && (x.Kind is null || Enum.IsDefined(x.Kind.Value))),
            400, "Capacity block classification is invalid.");
        var note = string.IsNullOrWhiteSpace(Note) ? null : Guard.Text(Note, "Capacity note", 512);
        // Missing constraints use validation-only lower bounds. Never return this view if either
        // constraint is unknown: zero buffer / empty obligations would invent allocatable time.
        var validation = ForgeCapacityProjection.ForDay(programme, capacityRevision, LocalDate,
            NetUsableMinutes, BufferMinutes ?? 0, blocks?.Select(x => new ForgeCapacityLocalInterval(
                x.LocalStart, x.LocalEnd, TimeZone, x.StartOffset, x.EndOffset)).ToArray() ?? []);
        var sorted = blocks?.OrderBy(x => new DateTimeOffset(x.LocalStart, x.StartOffset))
            .ThenBy(x => new DateTimeOffset(x.LocalEnd, x.EndOffset)).ToArray();
        var normalized = this with { Note = note, FixedBlocks = sorted is null ? null : Array.AsReadOnly(sorted) };
        return new(normalized, BufferMinutes.HasValue && FixedBlocks is not null ? validation : null);
    }
}

// A request-scoped preparation is not a transport/display/AI object, like the existing projection.
[JsonConverter(typeof(PrivateCapacityJsonConverter<ForgePreparedCapacity>))]
public sealed class ForgePreparedCapacity
{
    public ForgeCapacityPayload Payload { get; }
    public ForgeCapacityProjection? Planning { get; }
    internal ForgePreparedCapacity(ForgeCapacityPayload payload, ForgeCapacityProjection? planning) { Payload = payload; Planning = planning; }
}
