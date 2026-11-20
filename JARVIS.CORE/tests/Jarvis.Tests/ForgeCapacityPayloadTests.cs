using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;

namespace Jarvis.Tests;

public sealed class ForgeCapacityPayloadTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-20T12:00:00Z");
    private static ForgeProgramme Programme(bool paused = false) => new(Guid.NewGuid(), ForgeProgrammePolicy.OwnerScope, 1, 1,
        ForgeCalendar.DefaultStart, ForgeCalendar.DefaultGraduation, ForgeCalendar.DefaultTimeZone, paused, Now, Now);
    private static ForgeCapacityPayload Payload() => new(1, new(2026, 12, 1), "Europe/London", 120, 30, [], true, null, null, " Synthetic note ");
    private static ForgeCapacityFixedBlock Block(int start, int end) => new(new(2026, 12, 1, start, 0, 0), new(2026, 12, 1, end, 0, 0), TimeSpan.Zero, TimeSpan.Zero, ForgeCapacityBlockKind.Work);
    private static ProtectedRecordProtector Protector() => new(() => new() { ActiveKeyId = "synthetic-only",
        Keys = new Dictionary<string, string> { ["synthetic-only"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()) } });

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(120, null, false)]
    [InlineData(120, 30, false)]
    [InlineData(120, null, true)]
    public void ForgeCapacity_payload_unknown_constraints_never_invent_planning_capacity(int? net, int? buffer, bool blocksKnown)
    {
        var input = Payload() with { NetUsableMinutes = net, BufferMinutes = buffer, FixedBlocks = blocksKnown ? [] : null };
        var prepared = input.Prepare(Programme(), 1, Now);
        Assert.Null(prepared.Planning); Assert.Equal(net, prepared.Payload.NetUsableMinutes); Assert.Equal(buffer, prepared.Payload.BufferMinutes);
        Assert.Equal(blocksKnown, prepared.Payload.FixedBlocks is not null); Assert.Null(prepared.Payload.ObservedAt); Assert.Null(prepared.Payload.Origin);
    }

    [Fact]
    public void ForgeCapacity_payload_known_constraints_preserve_unknown_net_and_explicit_zero()
    {
        var programme = Programme(); var unknown = (Payload() with { NetUsableMinutes = null }).Prepare(programme, 1, Now);
        Assert.NotNull(unknown.Planning); Assert.False(unknown.Planning.Known); Assert.Null(unknown.Planning.AllocatableMinutes); Assert.False(unknown.Planning.CanAllocate);
        var zero = (Payload() with { NetUsableMinutes = 0, BufferMinutes = 0 }).Prepare(programme, 2, Now);
        Assert.True(zero.Planning!.Known); Assert.Equal(0, zero.Planning.AllocatableMinutes); Assert.Empty(zero.Payload.FixedBlocks!); Assert.False(zero.Planning.CanAllocate);
    }

    [Fact]
    public void ForgeCapacity_payload_paused_edit_does_not_resume_allocation()
    {
        var prepared = Payload().Prepare(Programme(paused: true), 2, Now);
        Assert.Equal(120, prepared.Payload.NetUsableMinutes); Assert.Equal(0, prepared.Planning!.AllocatableMinutes); Assert.False(prepared.Planning.CanAllocate);
        Assert.Equal(2, prepared.Planning.CapacityRevision);
    }

    [Fact]
    public void ForgeCapacity_payload_defensively_orders_blocks_and_retains_unclipped_intent()
    {
        var before = new ForgeCapacityFixedBlock(new(2026, 11, 30, 23, 0, 0), new(2026, 12, 1, 2, 0, 0), TimeSpan.Zero, TimeSpan.Zero, ForgeCapacityBlockKind.Sleep);
        var later = Block(9, 17); var blocks = new List<ForgeCapacityFixedBlock> { later, before };
        var prepared = (Payload() with { FixedBlocks = blocks }).Prepare(Programme(), 1, Now); blocks.Clear();
        Assert.Equal(2, prepared.Payload.FixedBlocks!.Count); Assert.Equal(before, prepared.Payload.FixedBlocks[0]);
        Assert.Equal(new DateTime(2026, 12, 1), prepared.Planning!.FixedBlocks[0].LocalStart);
        Assert.Equal("Synthetic note", prepared.Payload.Note);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("consent")]
    [InlineData("origin")]
    [InlineData("future-observation")]
    [InlineData("sentinel-observation")]
    [InlineData("zone")]
    [InlineData("date")]
    [InlineData("net")]
    [InlineData("buffer")]
    [InlineData("excess")]
    [InlineData("note")]
    [InlineData("overlap")]
    [InlineData("offset")]
    [InlineData("utc-kind")]
    [InlineData("block-kind")]
    [InlineData("null-block")]
    [InlineData("too-many-blocks")]
    public void ForgeCapacity_payload_rejects_invalid_or_unsupported_configuration(string field)
    {
        var original = Payload(); var input = field switch {
            "version" => original with { Version = 2 }, "consent" => original with { OwnerConsent = false },
            "origin" => original with { Origin = (ForgeCapacityOrigin)999 }, "future-observation" => original with { ObservedAt = Now.AddTicks(1) },
            "sentinel-observation" => original with { ObservedAt = DateTimeOffset.MinValue }, "zone" => original with { TimeZone = "UTC" },
            "date" => original with { LocalDate = new(2026, 11, 30) }, "net" => original with { NetUsableMinutes = -1 },
            "buffer" => original with { BufferMinutes = -1 }, "excess" => original with { NetUsableMinutes = 1440 },
            "note" => original with { Note = new string('a', 513) }, "overlap" => original with { FixedBlocks = [Block(9, 17), Block(16, 18)] },
            "offset" => original with { FixedBlocks = [Block(9, 17) with { StartOffset = TimeSpan.FromHours(1) }] },
            "utc-kind" => original with { FixedBlocks = [Block(9, 17) with { LocalStart = DateTime.SpecifyKind(Block(9, 17).LocalStart, DateTimeKind.Utc) }] },
            "block-kind" => original with { FixedBlocks = [Block(9, 17) with { Kind = (ForgeCapacityBlockKind)999 }] },
            "null-block" => original with { FixedBlocks = [null!] }, _ => original with { FixedBlocks = Enumerable.Repeat(Block(9, 17), 65).ToArray() }
        };
        Assert.Equal(400, Assert.Throws<DomainException>(() => input.Prepare(Programme(), 1, Now)).StatusCode);
    }

    [Fact]
    public void ForgeCapacity_payload_reuses_real_DST_day_lengths_gap_and_fold_validation()
    {
        // Wider synthetic dates exercise future calendars; shipped programme dates remain unchanged.
        var programme = Programme() with { Start = new(2026, 10, 1), Graduation = new(2027, 11, 1) };
        var spring = (Payload() with { LocalDate = new(2027, 3, 28), NetUsableMinutes = 1380, BufferMinutes = 0 }).Prepare(programme, 1, Now);
        Assert.Equal(1380, spring.Planning!.DayMinutes); Assert.Equal(1380, spring.Planning.AllocatableMinutes);
        var gap = new ForgeCapacityFixedBlock(new(2027, 3, 28, 1, 10, 0), new(2027, 3, 28, 2, 10, 0), TimeSpan.Zero, TimeSpan.FromHours(1), null);
        Assert.Equal(400, Assert.Throws<DomainException>(() => (spring.Payload with { NetUsableMinutes = 0, FixedBlocks = [gap] }).Prepare(programme, 1, Now)).StatusCode);
        var early = new ForgeCapacityFixedBlock(new(2027, 10, 31, 1, 10, 0), new(2027, 10, 31, 1, 20, 0), TimeSpan.FromHours(1), TimeSpan.FromHours(1), null);
        var late = early with { StartOffset = TimeSpan.Zero, EndOffset = TimeSpan.Zero };
        var fall = (Payload() with { LocalDate = new(2027, 10, 31), NetUsableMinutes = 0, BufferMinutes = 0, FixedBlocks = [late, early] }).Prepare(programme, 1, Now);
        Assert.Equal(1500, fall.Planning!.DayMinutes); Assert.Equal(early, fall.Payload.FixedBlocks![0]); Assert.Null(fall.Payload.FixedBlocks[0].Kind);
    }

    [Fact]
    public void ForgeCapacity_payload_padded_roundtrip_preserves_unknowns_provenance_and_rejects_future_fields()
    {
        var programme = Programme(); var input = Payload() with { NetUsableMinutes = null, BufferMinutes = null, FixedBlocks = null,
            ObservedAt = Now.ToOffset(TimeSpan.FromHours(5.5)).AddTicks(-7), Origin = ForgeCapacityOrigin.OwnerEstimate, Note = " " };
        var prepared = input.Prepare(programme, 1, Now); var protector = Protector();
        var context = new ProtectedRecordContext("forge.capacity.value", programme.OwnerScope, programme.Id, Guid.NewGuid(), 0, 1);
        var envelope = ForgeProtectedJson.Protect(protector, context, prepared.Payload); Assert.Equal(0, envelope.Ciphertext.Length % 512);
        var restored = ForgeProtectedJson.Read<ForgeCapacityPayload>(protector, context, envelope).Prepare(programme, 1, Now);
        Assert.Null(restored.Planning); Assert.Null(restored.Payload.Note); Assert.Null(restored.Payload.FixedBlocks);
        Assert.Equal(input.ObservedAt!.Value.Offset, restored.Payload.ObservedAt!.Value.Offset); Assert.Equal(input.ObservedAt.Value.Ticks, restored.Payload.ObservedAt.Value.Ticks);
        Assert.Equal(409, Assert.Throws<DomainException>(() => ForgeProtectedJson.Read<ForgeCapacityPayload>(protector, context with { RecordId = Guid.NewGuid() }, envelope)).StatusCode);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { input.Version, input.LocalDate, input.TimeZone, ExtraPrivateField = "Do not silently discard" });
        try { var future = protector.Protect(context, bytes); Assert.Equal(409, Assert.Throws<DomainException>(() => ForgeProtectedJson.Read<ForgeCapacityPayload>(protector, context, future)).StatusCode); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(prepared));
    }
}
