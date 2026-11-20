using System.Text.Json;
using System.Text.Json.Serialization;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Forge;

// These are decrypted, authorised in-process views, never API/event/model-context contracts.
[JsonConverter(typeof(PrivateCapacityJsonConverter<ForgeCapacityLocalInterval>))]
public sealed class ForgeCapacityLocalInterval(DateTime localStart, DateTime localEnd, string timeZone, TimeSpan startOffset, TimeSpan endOffset)
{
    public DateTime LocalStart { get; } = localStart;
    public DateTime LocalEnd { get; } = localEnd;
    public string TimeZone { get; } = timeZone;
    public TimeSpan StartOffset { get; } = startOffset;
    public TimeSpan EndOffset { get; } = endOffset;
}

[JsonConverter(typeof(PrivateCapacityJsonConverter<ForgeCapacityProjection>))]
public sealed class ForgeCapacityProjection
{
    private readonly string ownerScope;
    public Guid ProgrammeId { get; }
    public int ProgrammeRevision { get; }
    public int CapacityRevision { get; }
    public DateOnly LocalDate { get; }
    public string TimeZone { get; }
    public int DayMinutes { get; }
    // NET discretionary capacity: fixed blocks and BufferMinutes have already been excluded.
    public int? UsableMinutes { get; }
    public int BufferMinutes { get; }
    public IReadOnlyList<ForgeCapacityLocalInterval> FixedBlocks { get; }
    public bool Known => UsableMinutes.HasValue;
    public bool ProgrammePaused { get; }
    public bool CanAllocate => AllocatableMinutes is > 0;
    public int? AllocatableMinutes => Known ? (ProgrammePaused ? 0 : UsableMinutes) : null;

    private ForgeCapacityProjection(ForgeProgramme programme, int revision, DateOnly day, int dayMinutes, int? usable, int buffer, ForgeCapacityLocalInterval[] blocks)
    {
        ownerScope = programme.OwnerScope;
        ProgrammeId = programme.Id; ProgrammeRevision = programme.Revision; ProgrammePaused = programme.Paused;
        CapacityRevision = revision; LocalDate = day; TimeZone = programme.TimeZone; DayMinutes = dayMinutes;
        UsableMinutes = usable; BufferMinutes = buffer; FixedBlocks = Array.AsReadOnly(blocks);
    }
    // The protected reader supplies this only after owner authorization/decryption, within
    // the current programme/privacy boundary. Construction itself confers no authority.
    public static ForgeCapacityProjection ForDay(ForgeProgramme programme, int capacityRevision, DateOnly day,
        int? netUsableMinutes, int bufferMinutes, IReadOnlyList<ForgeCapacityLocalInterval> fixedBlocks)
    {
        Guard.Require(programme is not null && programme.Id != Guid.Empty && programme.DeletedAt is null, 409, "Programme is unavailable.");
        Guard.Require(capacityRevision > 0 && programme.Revision > 0 && day > DateOnly.MinValue && day < DateOnly.MaxValue && day >= programme.Start && day <= programme.Graduation &&
            fixedBlocks is { Count: <= 64 } && bufferMinutes >= 0 && (netUsableMinutes is null or >= 0), 400, "Capacity configuration is invalid.");
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(programme.TimeZone); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) {
            throw new DomainException(400, "Capacity timezone is invalid.");
        }
        var localStart = day.ToDateTime(TimeOnly.MinValue); var localEnd = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        // London midnight is unambiguous. Future zones with midnight transitions must
        // supply an explicit day-boundary design instead of silently choosing a fold.
        Guard.Require(!zone.IsInvalidTime(localStart) && !zone.IsAmbiguousTime(localStart) && !zone.IsInvalidTime(localEnd) && !zone.IsAmbiguousTime(localEnd),
            400, "Capacity day boundary is ambiguous or unavailable.");
        var start = new DateTimeOffset(localStart, zone.GetUtcOffset(localStart)); var end = new DateTimeOffset(localEnd, zone.GetUtcOffset(localEnd));
        var dayMinutes = checked((int)(end - start).TotalMinutes);
        var blocks = fixedBlocks.ToArray(); var intervals = new List<(DateTimeOffset Start, DateTimeOffset End, ForgeCapacityLocalInterval View)>();
        foreach (var block in blocks) {
            Guard.Require(block is not null && block.TimeZone == programme.TimeZone &&
                block.LocalStart.Kind == DateTimeKind.Unspecified && block.LocalEnd.Kind == DateTimeKind.Unspecified &&
                block.LocalStart.Ticks % TimeSpan.TicksPerMinute == 0 && block.LocalEnd.Ticks % TimeSpan.TicksPerMinute == 0 &&
                block.LocalStart >= localStart.AddDays(-1) && block.LocalStart <= localEnd.AddDays(1) &&
                block.LocalEnd >= localStart.AddDays(-1) && block.LocalEnd <= localEnd.AddDays(1), 400, "Capacity interval is invalid.");
            static bool ValidOffset(TimeZoneInfo zone, DateTime local, TimeSpan offset) => !zone.IsInvalidTime(local) &&
                (zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Contains(offset) : zone.GetUtcOffset(local) == offset);
            Guard.Require(ValidOffset(zone, block.LocalStart, block.StartOffset) && ValidOffset(zone, block.LocalEnd, block.EndOffset),
                400, "Capacity interval offset is invalid or falls in a clock gap.");
            var from = new DateTimeOffset(block.LocalStart, block.StartOffset); var until = new DateTimeOffset(block.LocalEnd, block.EndOffset);
            Guard.Require(until > from && until - from <= TimeSpan.FromDays(2) && until > start && from < end, 400, "Capacity interval is outside its day.");
            var clippedStart = from < start ? start : from; var clippedEnd = until > end ? end : until;
            var viewStart = TimeZoneInfo.ConvertTime(clippedStart, zone); var viewEnd = TimeZoneInfo.ConvertTime(clippedEnd, zone);
            intervals.Add((clippedStart, clippedEnd, new(viewStart.DateTime, viewEnd.DateTime, programme.TimeZone, viewStart.Offset, viewEnd.Offset)));
        }
        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 1; i < intervals.Count; i++) Guard.Require(intervals[i].Start >= intervals[i - 1].End, 400, "Fixed capacity intervals overlap.");
        var fixedMinutes = (int)intervals.Sum(x => (x.End - x.Start).TotalMinutes);
        var remaining = dayMinutes - fixedMinutes - bufferMinutes;
        Guard.Require(remaining >= 0 && (netUsableMinutes is null || netUsableMinutes <= remaining), 400, "Capacity exceeds the available local day.");
        return new(programme, capacityRevision, day, dayMinutes, netUsableMinutes, bufferMinutes, intervals.Select(x => x.View).ToArray());
    }

    // PF-08 supplies current-policy intervals resolved into the programme's zone/explicit offsets.
    // This composes constraints only; it changes neither owner configuration nor global JARVIS policy.
    public static ForgeCapacityProjection WithPolicyQuietHours(ForgeProgramme programme, ForgeCapacityProjection capacity,
        IReadOnlyList<ForgeCapacityLocalInterval>? policyQuiet)
    {
        Guard.Require(programme is { DeletedAt: null } && capacity is not null &&
            capacity.ownerScope == programme.OwnerScope && capacity.ProgrammeId == programme.Id &&
            capacity.ProgrammeRevision == programme.Revision && capacity.TimeZone == programme.TimeZone,
            409, "Current capacity programme scope is required.");
        Guard.Require(policyQuiet is not null, 409, "Resolved quiet-hours policy is unavailable.");
        Guard.Require(policyQuiet.Count <= 64, 400, "Too many quiet-hours policy intervals.");
        var blocked = capacity.FixedBlocks.ToList();
        // Validate/clip each policy interval using the same approved temporal rules. They may
        // overlap each other or owner blocks; only the resulting union goes to the final validator.
        foreach (var quiet in policyQuiet) blocked.AddRange(ForDay(programme, capacity.CapacityRevision, capacity.LocalDate, null, 0, [quiet]).FixedBlocks);
        var ordered = blocked.Select(x => (Start: new DateTimeOffset(x.LocalStart, x.StartOffset), End: new DateTimeOffset(x.LocalEnd, x.EndOffset)))
            .OrderBy(x => x.Start).ThenBy(x => x.End).ToArray();
        var union = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var interval in ordered) {
            if (union.Count == 0 || interval.Start > union[^1].End) union.Add(interval);
            else if (interval.End > union[^1].End) union[^1] = (union[^1].Start, interval.End);
        }
        var blockedMinutes = checked((int)union.Sum(x => (x.End - x.Start).TotalMinutes));
        var remaining = capacity.DayMinutes - blockedMinutes - capacity.BufferMinutes;
        Guard.Require(remaining >= 0, 409, "Quiet-hours policy leaves no room for the declared capacity buffer.");
        int? net = capacity.UsableMinutes is int known ? Math.Min(known, remaining) : null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(programme.TimeZone);
        var merged = union.Select(x => {
            var from = TimeZoneInfo.ConvertTime(x.Start, zone); var until = TimeZoneInfo.ConvertTime(x.End, zone);
            return new ForgeCapacityLocalInterval(from.DateTime, until.DateTime, programme.TimeZone, from.Offset, until.Offset);
        }).ToArray();
        return ForDay(programme, capacity.CapacityRevision, capacity.LocalDate, net, capacity.BufferMinutes, merged);
    }
}

public sealed class PrivateCapacityJsonConverter<T> : JsonConverter<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Private capacity projections must remain in process.");
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Private capacity projections must remain in process.");
}
