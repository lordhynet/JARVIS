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
}

public sealed class PrivateCapacityJsonConverter<T> : JsonConverter<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Private capacity projections must remain in process.");
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Private capacity projections must remain in process.");
}
