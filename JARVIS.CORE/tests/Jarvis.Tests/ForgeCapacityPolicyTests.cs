using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;

namespace Jarvis.Tests;

public sealed class ForgeCapacityPolicyTests
{
    private static readonly DateOnly Day = new(2026, 12, 1);
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-20T12:00:00Z");
    private static ForgeProgramme Programme(bool paused = false) => new(Guid.NewGuid(), ForgeProgrammePolicy.OwnerScope, 1, 1,
        ForgeCalendar.DefaultStart, ForgeCalendar.DefaultGraduation, "Europe/London", paused, At, At);
    private static ForgeCapacityLocalInterval Interval(int start, int end) => new(Day.ToDateTime(TimeOnly.MinValue).AddMinutes(start),
        Day.ToDateTime(TimeOnly.MinValue).AddMinutes(end), "Europe/London", TimeSpan.Zero, TimeSpan.Zero);

    [Fact]
    public void ForgeCapacity_policy_identical_owner_quiet_is_counted_once()
    {
        var programme = Programme(); var owner = ForgeCapacityProjection.ForDay(programme, 2, Day, 1290, 30, [Interval(1320, 1440)]);
        var composed = ForgeCapacityProjection.WithPolicyQuietHours(programme, owner, [Interval(1320, 1440)]);
        Assert.Equal(1290, composed.UsableMinutes); Assert.Single(composed.FixedBlocks); Assert.Equal(1290, owner.UsableMinutes);
        Assert.Equal(30, composed.BufferMinutes); Assert.Equal(2, composed.CapacityRevision);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(composed));
    }

    [Fact]
    public void ForgeCapacity_policy_partial_overlap_caps_only_additional_unavailable_time()
    {
        var programme = Programme(); var owner = ForgeCapacityProjection.ForDay(programme, 1, Day, 1290, 30, [Interval(1320, 1440)]);
        var composed = ForgeCapacityProjection.WithPolicyQuietHours(programme, owner, [Interval(1260, 1380)]);
        Assert.Equal(1230, composed.UsableMinutes); Assert.Equal(21, Assert.Single(composed.FixedBlocks).LocalStart.Hour);
        Assert.Equal(1290, owner.UsableMinutes); Assert.Equal(22, owner.FixedBlocks[0].LocalStart.Hour);
    }

    [Fact]
    public void ForgeCapacity_policy_unions_nested_touching_and_owner_work_without_subtracting_net_twice()
    {
        var programme = Programme(); var owner = ForgeCapacityProjection.ForDay(programme, 1, Day, 300, 60, [Interval(540, 1020)]);
        var composed = ForgeCapacityProjection.WithPolicyQuietHours(programme, owner,
            [Interval(660, 780), Interval(720, 840), Interval(960, 1080), Interval(1080, 1140)]);
        var block = Assert.Single(composed.FixedBlocks); Assert.Equal(9, block.LocalStart.Hour); Assert.Equal(19, block.LocalEnd.Hour);
        Assert.Equal(300, composed.UsableMinutes); Assert.Equal(60, composed.BufferMinutes);
        var emptyPolicy = ForgeCapacityProjection.WithPolicyQuietHours(programme, owner, []); Assert.Equal(300, emptyPolicy.UsableMinutes);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(300, true)]
    public void ForgeCapacity_policy_preserves_unknown_zero_and_paused_semantics(int? net, bool paused)
    {
        var programme = Programme(paused); var owner = ForgeCapacityProjection.ForDay(programme, 1, Day, net, 30, []);
        var composed = ForgeCapacityProjection.WithPolicyQuietHours(programme, owner, [Interval(0, 480)]);
        Assert.Equal(net, composed.UsableMinutes); Assert.False(composed.CanAllocate);
        if (net is null) Assert.Null(composed.AllocatableMinutes); else Assert.Equal(0, composed.AllocatableMinutes);
    }

    [Fact]
    public void ForgeCapacity_policy_uses_elapsed_DST_minutes_and_clips_cross_day_intervals()
    {
        var programme = Programme(); var springDay = new DateOnly(2027, 3, 28);
        var owner = ForgeCapacityProjection.ForDay(programme, 1, springDay, 1380, 0, []);
        var quiet = new ForgeCapacityLocalInterval(new(2027, 3, 27, 23, 0, 0), new(2027, 3, 28, 3, 0, 0), "Europe/London", TimeSpan.Zero, TimeSpan.FromHours(1));
        var composed = ForgeCapacityProjection.WithPolicyQuietHours(programme, owner, [quiet]);
        Assert.Equal(1380, composed.DayMinutes); Assert.Equal(1260, composed.UsableMinutes);
        Assert.Equal(springDay.ToDateTime(TimeOnly.MinValue), Assert.Single(composed.FixedBlocks).LocalStart);
        var gap = new ForgeCapacityLocalInterval(new(2027, 3, 28, 1, 10, 0), new(2027, 3, 28, 2, 10, 0), "Europe/London", TimeSpan.Zero, TimeSpan.FromHours(1));
        Assert.Equal(400, Assert.Throws<DomainException>(() => ForgeCapacityProjection.WithPolicyQuietHours(programme, owner, [gap])).StatusCode);
    }

    [Fact]
    public void ForgeCapacity_policy_distinguishes_both_autumn_folds_and_unions_by_real_instants()
    {
        var programme = Programme() with { Start = new(2026, 10, 1), Graduation = new(2027, 11, 1) }; var day = new DateOnly(2027, 10, 31);
        var early = new ForgeCapacityLocalInterval(new(2027, 10, 31, 1, 10, 0), new(2027, 10, 31, 1, 20, 0), "Europe/London", TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        var late = new ForgeCapacityLocalInterval(early.LocalStart, early.LocalEnd, early.TimeZone, TimeSpan.Zero, TimeSpan.Zero);
        var overlap = new ForgeCapacityLocalInterval(new(2027, 10, 31, 1, 15, 0), new(2027, 10, 31, 1, 25, 0), "Europe/London", TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        var owner = ForgeCapacityProjection.ForDay(programme, 1, day, 1490, 0, [early]);
        var composed = ForgeCapacityProjection.WithPolicyQuietHours(programme, owner, [late, overlap]);
        Assert.Equal(1500, composed.DayMinutes); Assert.Equal(1475, composed.UsableMinutes); Assert.Equal(2, composed.FixedBlocks.Count);
    }

    [Fact]
    public void ForgeCapacity_policy_requires_available_current_scope_and_a_feasible_original_buffer()
    {
        var programme = Programme(); var owner = ForgeCapacityProjection.ForDay(programme, 1, Day, 0, 30, []);
        Assert.Equal(409, Assert.Throws<DomainException>(() => ForgeCapacityProjection.WithPolicyQuietHours(programme, owner, null)).StatusCode);
        foreach (var changed in new[] { programme with { Id = Guid.NewGuid() }, programme with { OwnerScope = "other-owner" },
            programme with { Revision = 2 }, programme with { DeletedAt = At }, programme with { TimeZone = "UTC" } })
            Assert.Equal(409, Assert.Throws<DomainException>(() => ForgeCapacityProjection.WithPolicyQuietHours(changed, owner, [])).StatusCode);
        Assert.Equal(409, Assert.Throws<DomainException>(() => ForgeCapacityProjection.WithPolicyQuietHours(programme, owner, [Interval(0, 1440)])).StatusCode);
        Assert.Equal(400, Assert.Throws<DomainException>(() => ForgeCapacityProjection.WithPolicyQuietHours(programme, owner, Enumerable.Repeat(Interval(0, 60), 65).ToArray())).StatusCode);
        Assert.Equal(30, owner.BufferMinutes); Assert.Empty(owner.FixedBlocks);
    }

    [Fact]
    public void ForgeCapacity_retry_comparison_uses_canonical_normalized_bytes_not_list_reference_equality()
    {
        var programme = Programme(); var firstBlock = new ForgeCapacityFixedBlock(Interval(540, 600).LocalStart, Interval(540, 600).LocalEnd, TimeSpan.Zero, TimeSpan.Zero, ForgeCapacityBlockKind.Work);
        var secondBlock = firstBlock with { LocalStart = Interval(720, 780).LocalStart, LocalEnd = Interval(720, 780).LocalEnd };
        var first = new ForgeCapacityPayload(1, Day, programme.TimeZone, 120, 30, [secondBlock, firstBlock], true, null, null, " Note ")
            .Prepare(programme, 1, At).Payload;
        var retry = (first with { FixedBlocks = [firstBlock, secondBlock], Note = "Note" }).Prepare(programme, 1, At).Payload;
        Assert.NotEqual(first, retry); // List references differ; this is deliberately NOT the replay comparison.
        var protector = new ProtectedRecordProtector(() => new() { ActiveKeyId = "synthetic-only",
            Keys = new Dictionary<string, string> { ["synthetic-only"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()) } });
        var requestId = Guid.NewGuid(); var context = new ProtectedRecordContext("forge.capacity.save", programme.OwnerScope, programme.Id, Guid.NewGuid(), 0, 1);
        byte[] Canonical(Guid request, ForgeCapacityPayload payload) => JsonSerializer.SerializeToUtf8Bytes(new { RequestId = request, ExpectedRevision = 0, Payload = payload });
        var originalBytes = Canonical(requestId, first); var retryBytes = Canonical(requestId, retry);
        var changedBytes = Canonical(requestId, retry with { NetUsableMinutes = 121 }); var otherRequest = Canonical(Guid.NewGuid(), retry);
        try {
            var proof = protector.CreateRetryProof(context, originalBytes); Assert.Equal(originalBytes, retryBytes);
            Assert.True(protector.MatchesRetryProof(context, proof, retryBytes));
            Assert.False(protector.MatchesRetryProof(context, proof, changedBytes)); Assert.False(protector.MatchesRetryProof(context, proof, otherRequest));
            Assert.False(protector.MatchesRetryProof(context with { Revision = 2 }, proof, retryBytes));
        } finally {
            CryptographicOperations.ZeroMemory(originalBytes); CryptographicOperations.ZeroMemory(retryBytes);
            CryptographicOperations.ZeroMemory(changedBytes); CryptographicOperations.ZeroMemory(otherRequest);
        }
    }
}
