using System.Text.Json.Serialization;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Forge;

public enum ForgeCheckInObservationMode { OwnerReported = 1, CanonicalReference }

// Private plaintext in an authorized request, never a transport/agent/display contract.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ForgeCheckInObservation(string MetricKey, ForgeCheckInObservationMode Mode,
    decimal? NumericValue, string? TextValue, bool? BooleanValue, string? Unit, AgentSourceRef? Source,
    DateTimeOffset? ObservedAt, string? Note);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ForgeCheckInPayload(int Version, string DivisionKey, IReadOnlyList<ForgeCheckInObservation>? Observations,
    bool OwnerConsent, DateTimeOffset? ObservedAt, string? Note)
{
    public const int SchemaVersion = 1;
    private static readonly HashSet<string> SourceKinds = ["training", "coaching", "learning", "story", "session", "import", "recording", "source-claim"];
    private static bool TimeValid(DateTimeOffset? value, DateTimeOffset now) => value is null || value > DateTimeOffset.MinValue && value <= now;
    private static string? Optional(string? value, string label, int max) => string.IsNullOrWhiteSpace(value) ? null : Guard.Text(value, label, max);

    private static decimal? NormalizeNumber(decimal? input)
    {
        if (input is not { } value) return null;
        Guard.Require(value is >= -1_000_000_000_000m and <= 1_000_000_000_000m && decimal.Round(value, 6) == value,
            400, "Reported number exceeds the supported range or precision.");
        // Strip only trailing zeros: preserve the exact value without rounding or adding precision.
        // Decimal scale affects JSON bytes, so equivalent retries need one representation.
        var bits = decimal.GetBits(value);
        UInt128 coefficient = (uint)bits[0] | ((UInt128)(uint)bits[1] << 32) | ((UInt128)(uint)bits[2] << 64);
        var scale = (bits[3] >> 16) & 0xff;
        if (coefficient == 0) return 0m;
        while (scale > 0 && coefficient % 10 == 0) { coefficient /= 10; scale--; }
        return new decimal(unchecked((int)(uint)coefficient), unchecked((int)(uint)(coefficient >> 32)),
            unchecked((int)(uint)(coefficient >> 64)), bits[3] < 0, (byte)scale);
    }

    // Structural normalization only. Future commands must authorize the owner, resolve the installed
    // division/metric rules and recheck every canonical source's scope/current version/status in transaction.
    public ForgeCheckInPayload Normalize(ForgeProgramme programme, DateTimeOffset now)
    {
        Guard.Require(programme is { DeletedAt: null } && programme.Id != Guid.Empty, 409, "Forge programme is unavailable.");
        Guard.Require(Version == SchemaVersion && OwnerConsent && TimeValid(ObservedAt, now), 400, "Check-in version, consent or provenance is invalid.");
        var division = Guard.Text(DivisionKey, "Division key", 60);
        Guard.Require(DomainPolicy.IsValidKey(division), 400, "Check-in division key is invalid.");
        Guard.Require(Observations is null || Observations.Count <= 16, 400, "Too many check-in observations.");
        var normalized = Observations?.Select(item => {
            Guard.Require(item is not null && Enum.IsDefined(item.Mode) && TimeValid(item.ObservedAt, now), 400, "Check-in observation is invalid.");
            var key = Guard.Text(item.MetricKey, "Metric key", 60);
            Guard.Require(DomainPolicy.IsValidKey(key), 400, "Check-in metric key is invalid.");
            var text = Optional(item.TextValue, "Reported value", 256); var unit = Optional(item.Unit, "Reported unit", 32);
            var count = (item.NumericValue.HasValue ? 1 : 0) + (text is not null ? 1 : 0) + (item.BooleanValue.HasValue ? 1 : 0);
            Guard.Require(count <= 1 && (!item.NumericValue.HasValue || unit is not null) &&
                (text is null && !item.BooleanValue.HasValue || unit is null), 400, "Reported value forms or units are inconsistent.");
            AgentSourceRef? source = null;
            if (item.Mode == ForgeCheckInObservationMode.CanonicalReference) {
                Guard.Require(item.Source is not null && count == 0 && unit is null, 400, "Canonical observations must reference their source without copied values.");
                var kind = Guard.Text(item.Source.Kind, "Source kind", 32); var version = Guard.Text(item.Source.Version, "Source version", 128);
                Guard.Require(SourceKinds.Contains(kind) && item.Source.Id != Guid.Empty && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':'),
                    400, "Canonical source reference is invalid.");
                source = item.Source with { Kind = kind, Version = version };
            } else Guard.Require(item.Source is null, 400, "Owner reports cannot impersonate canonical source data.");
            return item with { MetricKey = key, NumericValue = NormalizeNumber(item.NumericValue), TextValue = text, Unit = unit, Source = source, Note = Optional(item.Note, "Observation note", 128) };
        }).OrderBy(x => x.MetricKey, StringComparer.Ordinal).ToArray();
        Guard.Require(normalized is null || normalized.Select(x => x.MetricKey).Distinct(StringComparer.Ordinal).Count() == normalized.Length,
            400, "Duplicate check-in metric keys are invalid.");
        return this with { DivisionKey = division, Observations = normalized is null ? null : Array.AsReadOnly(normalized), Note = Optional(Note, "Check-in note", 512) };
    }
}
