using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;

namespace Jarvis.Tests;

public sealed class ForgeCheckInPayloadTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-20T13:00:00Z");
    private static ForgeProgramme Programme() => new(Guid.NewGuid(), ForgeProgrammePolicy.OwnerScope, 1, 1,
        ForgeCalendar.DefaultStart, ForgeCalendar.DefaultGraduation, ForgeCalendar.DefaultTimeZone, true, Now, Now);
    private static ForgeCheckInObservation Report(string key = "practice-count") => new(key, ForgeCheckInObservationMode.OwnerReported, null, null, null, null, null, null, null);
    private static ForgeCheckInPayload Payload() => new(1, "engineering", [Report()], true, null, " Private note ");
    private static ProtectedRecordProtector Protector() => new(() => new() { ActiveKeyId = "synthetic-only",
        Keys = new Dictionary<string, string> { ["synthetic-only"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()) } });

    [Fact]
    public void ForgeCheckIn_unknown_empty_zero_and_false_are_distinct_without_invented_scores()
    {
        var programme = Programme(); var unknown = (Payload() with { Observations = null }).Normalize(programme, Now);
        var empty = (Payload() with { Observations = [] }).Normalize(programme, Now);
        Assert.Null(unknown.Observations); Assert.Empty(empty.Observations!);
        var observations = new[] { Report("unknown"), Report("zero") with { NumericValue = 0, Unit = " count " }, Report("false") with { BooleanValue = false } };
        var normalized = (Payload() with { Observations = observations }).Normalize(programme, Now);
        var values = normalized.Observations!;
        Assert.Null(values.Single(x => x.MetricKey == "unknown").NumericValue);
        Assert.Equal(0, values.Single(x => x.MetricKey == "zero").NumericValue);
        Assert.False(values.Single(x => x.MetricKey == "false").BooleanValue);
        Assert.All(values, x => Assert.Null(x.ObservedAt));
        observations[0] = Report("changed"); Assert.DoesNotContain(values, x => x.MetricKey == "changed");
        Assert.Equal("Private note", normalized.Note); Assert.True(programme.Paused);
    }

    [Fact]
    public void ForgeCheckIn_canonical_observation_is_only_an_unresolved_existing_source_reference()
    {
        var reference = new AgentSourceRef(" training ", Guid.NewGuid(), " 2 ");
        var input = Report() with { Mode = ForgeCheckInObservationMode.CanonicalReference, Source = reference };
        var result = (Payload() with { Observations = [input] }).Normalize(Programme(), Now).Observations!.Single();
        Assert.Equal(reference.Id, result.Source!.Id); Assert.Equal("training", result.Source.Kind); Assert.Equal("2", result.Source.Version);
        Assert.Null(result.NumericValue); Assert.Null(result.TextValue); Assert.Null(result.BooleanValue); Assert.Null(result.Unit);
    }

    [Theory]
    [InlineData("1000000000000")]
    [InlineData("-1000000000000")]
    [InlineData("1000000000000.0000000000000000")]
    [InlineData("999999999999.999999")]
    [InlineData("-999999999999.999999")]
    [InlineData("0.000001")]
    [InlineData("-0.000001")]
    [InlineData("1.2300000000000000000000000000")]
    [InlineData("-1.2300000000000000000000000000")]
    [InlineData("-0.0000000000000000000000000000")]
    public void ForgeCheckIn_numbers_preserve_exact_values_with_canonical_scale(string text)
    {
        var value = decimal.Parse(text, CultureInfo.InvariantCulture); var programme = Programme();
        var normalized = (Payload() with { Observations = [Report() with { NumericValue = value, Unit = "count" }] }).Normalize(programme, Now);
        var result = normalized.Observations!.Single();
        Assert.Equal(value, result.NumericValue); Assert.Equal("count", result.Unit);
        var bits = decimal.GetBits(result.NumericValue!.Value); var scale = (bits[3] >> 16) & 0xff;
        Assert.InRange(scale, 0, 6);
        if (value == 0) Assert.Equal(new int[4], bits);
        else if (scale > 0) Assert.NotEqual(value, decimal.Round(result.NumericValue.Value, scale - 1));
        Assert.Equal(JsonSerializer.Serialize(normalized), JsonSerializer.Serialize(normalized.Normalize(programme, Now)));
    }

    [Theory]
    [InlineData("1000000000000.000001")]
    [InlineData("-1000000000000.000001")]
    [InlineData("1000000000001")]
    [InlineData("-1000000000001")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335")]
    [InlineData("1.0000001")]
    [InlineData("-1.0000001")]
    [InlineData("0.0000000000000000000000000001")]
    public void ForgeCheckIn_out_of_range_or_over_precise_numbers_are_rejected_without_rounding(string text)
    {
        var value = decimal.Parse(text, CultureInfo.InvariantCulture);
        var input = Payload() with { Observations = [Report() with { NumericValue = value, Unit = "count" }] };
        Assert.Equal(400, Assert.Throws<DomainException>(() => input.Normalize(Programme(), Now)).StatusCode);
    }

    [Theory]
    [InlineData("1", "1.0000000000000000000000000000")]
    [InlineData("-1.23", "-1.2300000000000000000000000000")]
    [InlineData("0", "-0.0000000000000000000000000000")]
    public void ForgeCheckIn_equivalent_decimal_scales_have_identical_retry_proofs(string original, string retry)
    {
        var programme = Programme();
        byte[] Encode(string value) => JsonSerializer.SerializeToUtf8Bytes((Payload() with {
            Observations = [Report() with { NumericValue = decimal.Parse(value, CultureInfo.InvariantCulture), Unit = "count" }]
        }).Normalize(programme, Now));
        var left = Encode(original); var right = Encode(retry); var changed = Encode("2");
        try {
            var protector = Protector(); var context = new ProtectedRecordContext("forge.check-in.save", programme.OwnerScope, programme.Id, Guid.NewGuid(), 0, 1);
            var proof = protector.CreateRetryProof(context, left);
            Assert.Equal(left, right); Assert.True(protector.MatchesRetryProof(context, proof, right));
            Assert.False(protector.MatchesRetryProof(context, proof, changed));
        } finally { CryptographicOperations.ZeroMemory(left); CryptographicOperations.ZeroMemory(right); CryptographicOperations.ZeroMemory(changed); }
    }

    [Theory]
    [InlineData("version")]
    [InlineData("consent")]
    [InlineData("division")]
    [InlineData("future-time")]
    [InlineData("sentinel-time")]
    [InlineData("metric")]
    [InlineData("mode")]
    [InlineData("mixed-value")]
    [InlineData("numeric-unit")]
    [InlineData("boolean-unit")]
    [InlineData("owner-source")]
    [InlineData("canonical-value")]
    [InlineData("missing-source")]
    [InlineData("source-id")]
    [InlineData("source-kind")]
    [InlineData("source-version")]
    [InlineData("text-bound")]
    [InlineData("note-bound")]
    [InlineData("duplicate")]
    [InlineData("count")]
    [InlineData("null-item")]
    public void ForgeCheckIn_invalid_or_ambiguous_observations_are_rejected(string field)
    {
        var input = Payload(); var owner = Report();
        var canonical = owner with { Mode = ForgeCheckInObservationMode.CanonicalReference, Source = new("training", Guid.NewGuid(), "1") };
        input = field switch {
            "version" => input with { Version = 2 }, "consent" => input with { OwnerConsent = false }, "division" => input with { DivisionKey = "not a domain" },
            "future-time" => input with { ObservedAt = Now.AddTicks(1) }, "sentinel-time" => input with { ObservedAt = DateTimeOffset.MinValue },
            "metric" => input with { Observations = [owner with { MetricKey = "Invalid Metric" }] },
            "mode" => input with { Observations = [owner with { Mode = (ForgeCheckInObservationMode)999 }] },
            "mixed-value" => input with { Observations = [owner with { NumericValue = 0, BooleanValue = false, Unit = "count" }] },
            "numeric-unit" => input with { Observations = [owner with { NumericValue = 0 }] },
            "boolean-unit" => input with { Observations = [owner with { BooleanValue = false, Unit = "count" }] },
            "owner-source" => input with { Observations = [owner with { Source = canonical.Source }] },
            "canonical-value" => input with { Observations = [canonical with { TextValue = "Copied canonical answer" }] },
            "missing-source" => input with { Observations = [canonical with { Source = null }] },
            "source-id" => input with { Observations = [canonical with { Source = canonical.Source! with { Id = Guid.Empty } }] },
            "source-kind" => input with { Observations = [canonical with { Source = canonical.Source! with { Kind = "unknown-executable" } }] },
            "source-version" => input with { Observations = [canonical with { Source = canonical.Source! with { Version = " " } }] },
            "text-bound" => input with { Observations = [owner with { TextValue = new string('a', 257) }] },
            "note-bound" => input with { Note = new string('a', 513) }, "duplicate" => input with { Observations = [owner, owner] },
            "count" => input with { Observations = Enumerable.Range(0, 17).Select(i => Report("metric-" + i)).ToArray() }, _ => input with { Observations = [null!] }
        };
        Assert.Equal(400, Assert.Throws<DomainException>(() => input.Normalize(Programme(), Now)).StatusCode);
    }

    [Fact]
    public void ForgeCheckIn_padded_roundtrip_preserves_provenance_and_rejects_nested_unknown_private_fields()
    {
        var programme = Programme(); var input = Payload() with { ObservedAt = Now.ToOffset(TimeSpan.FromHours(5.5)).AddTicks(-7), Observations = [Report() with { TextValue = " " }] };
        var normalized = input.Normalize(programme, Now); var protector = Protector();
        var context = new ProtectedRecordContext("forge.check-in.value", programme.OwnerScope, programme.Id, Guid.NewGuid(), 0, 1);
        var envelope = ForgeProtectedJson.Protect(protector, context, normalized); Assert.Equal(0, envelope.Ciphertext.Length % 512);
        var restored = ForgeProtectedJson.Read<ForgeCheckInPayload>(protector, context, envelope).Normalize(programme, Now);
        Assert.Equal(input.ObservedAt!.Value.Offset, restored.ObservedAt!.Value.Offset); Assert.Equal(input.ObservedAt.Value.Ticks, restored.ObservedAt.Value.Ticks);
        Assert.Null(restored.Observations!.Single().TextValue);
        Assert.Equal(409, Assert.Throws<DomainException>(() => ForgeProtectedJson.Read<ForgeCheckInPayload>(protector, context with { RecordId = Guid.NewGuid() }, envelope)).StatusCode);
        var future = ForgeProtectedJson.Protect(protector, context, new { Version = 1, DivisionKey = "engineering", OwnerConsent = true,
            Observations = new[] { new { MetricKey = "reference", Mode = 2, Source = new { Kind = "learning", Id = Guid.NewGuid(), Version = "1", ExtraPrivateField = "Must not be discarded" } } } });
        Assert.Equal(409, Assert.Throws<DomainException>(() => ForgeProtectedJson.Read<ForgeCheckInPayload>(protector, context, future)).StatusCode);
    }

    [Fact]
    public void ForgeCheckIn_normalization_produces_equivalent_canonical_retry_bytes()
    {
        var programme = Programme(); var first = (Payload() with { Observations = [Report("z-last"), Report("a-first") with { BooleanValue = false }] }).Normalize(programme, Now);
        var retry = (first with { DivisionKey = " engineering ", Note = "Private note", Observations = first.Observations!.Reverse().ToArray() }).Normalize(programme, Now);
        var requestId = Guid.NewGuid(); byte[] Encode(ForgeCheckInPayload payload) => JsonSerializer.SerializeToUtf8Bytes(new { RequestId = requestId, ExpectedRevision = 0, Payload = payload });
        var left = Encode(first); var right = Encode(retry); var changed = Encode(retry with { Note = "Changed" });
        try {
            var protector = Protector(); var context = new ProtectedRecordContext("forge.check-in.save", programme.OwnerScope, programme.Id, Guid.NewGuid(), 0, 1);
            var proof = protector.CreateRetryProof(context, left); Assert.Equal(left, right);
            Assert.True(protector.MatchesRetryProof(context, proof, right)); Assert.False(protector.MatchesRetryProof(context, proof, changed));
        } finally { CryptographicOperations.ZeroMemory(left); CryptographicOperations.ZeroMemory(right); CryptographicOperations.ZeroMemory(changed); }
    }
}
