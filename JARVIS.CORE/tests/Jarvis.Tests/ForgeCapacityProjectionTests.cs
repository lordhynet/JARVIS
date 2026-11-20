using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Tests;

public sealed class ForgeCapacityProjectionTests
{
    private static readonly DateOnly Day = new(2026, 12, 1);
    private static ForgeProgramme Programme() => new(Guid.NewGuid(), "owner", 1, 1, Day, new(2027, 8, 18), "Europe/London", false,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private static ForgeCapacityLocalInterval Block(DateTime start, DateTime end, int startHours = 0, int endHours = 0) =>
        new(start, end, "Europe/London", TimeSpan.FromHours(startHours), TimeSpan.FromHours(endHours));
    private static void Invalid(Action action) => Assert.Equal(400, Assert.Throws<DomainException>(action).StatusCode);

    [Fact]
    public void Unknown_capacity_remains_unknown_and_net_minutes_do_not_deduct_buffer_twice()
    {
        var programme = Programme();
        var unknown = ForgeCapacityProjection.ForDay(programme, 1, Day, null, 60, []);
        Assert.False(unknown.Known); Assert.False(unknown.CanAllocate); Assert.Null(unknown.UsableMinutes); Assert.Null(unknown.AllocatableMinutes);
        var known = ForgeCapacityProjection.ForDay(programme, 2, Day, 120, 60, [Block(new(2026, 12, 1, 9, 0, 0), new(2026, 12, 1, 17, 0, 0))]);
        Assert.True(known.Known); Assert.True(known.CanAllocate); Assert.Equal(120, known.AllocatableMinutes); Assert.Equal(1440, known.DayMinutes);
    }

    [Fact]
    public void Paused_programme_accepts_configuration_edits_but_exposes_no_allocatable_minutes()
    {
        var paused = Programme() with { Paused = true, Revision = 2 };
        var old = ForgeCapacityProjection.ForDay(paused, 1, Day, 90, 30, []);
        var edited = ForgeCapacityProjection.ForDay(paused, 2, Day, 120, 30, []);
        Assert.Equal(90, old.UsableMinutes); Assert.Equal(120, edited.UsableMinutes); Assert.Equal(2, edited.CapacityRevision);
        Assert.True(edited.ProgrammePaused); Assert.False(edited.CanAllocate); Assert.Equal(0, edited.AllocatableMinutes);
        var resumed = ForgeCapacityProjection.ForDay(paused with { Paused = false, Revision = 3 }, 2, Day, 120, 30, []);
        Assert.Equal(120, resumed.AllocatableMinutes); Assert.True(resumed.CanAllocate);
        Assert.True(paused.Paused); // The projection never changes programme state or starts an allocator.
    }

    [Fact]
    public void Spring_clock_change_uses_the_actual_23_hour_day_and_rejects_gap_times()
    {
        var day = new DateOnly(2027, 3, 28); var programme = Programme();
        var capacity = ForgeCapacityProjection.ForDay(programme, 1, day, 1380, 0, []);
        Assert.Equal(1380, capacity.DayMinutes);
        Invalid(() => ForgeCapacityProjection.ForDay(programme, 1, day, 1381, 0, []));
        Invalid(() => ForgeCapacityProjection.ForDay(programme, 1, day, 0, 0,
            [Block(new(2027, 3, 28, 1, 15, 0), new(2027, 3, 28, 1, 45, 0))]));
    }

    [Fact]
    public void Fold_offsets_preserve_real_duration_without_silently_guessing_an_offset()
    {
        // Extended pure fixture isolates the timezone rule; programme creation still accepts only the official dates.
        var programme = Programme() with { Start = new(2026, 10, 1) }; var day = new DateOnly(2026, 10, 25);
        var block = Block(new(2026, 10, 25, 1, 15, 0), new(2026, 10, 25, 1, 45, 0), 1, 0);
        var capacity = ForgeCapacityProjection.ForDay(programme, 1, day, 1410, 0, [block]);
        Assert.Equal(1500, capacity.DayMinutes); Assert.Equal(1410, capacity.AllocatableMinutes);
        Invalid(() => ForgeCapacityProjection.ForDay(programme, 1, day, 1411, 0, [block]));
        Invalid(() => ForgeCapacityProjection.ForDay(programme, 1, day, 0, 0,
            [Block(block.LocalStart, block.LocalEnd, 2, 0)]));
    }

    [Fact]
    public void Overnight_blocks_are_clipped_to_the_local_day_and_overlaps_are_rejected()
    {
        var programme = Programme(); var overnight = Block(new(2026, 11, 30, 22, 0, 0), new(2026, 12, 1, 6, 0, 0));
        Assert.Equal(1020, ForgeCapacityProjection.ForDay(programme, 1, Day, 1020, 60, [overnight]).AllocatableMinutes);
        Invalid(() => ForgeCapacityProjection.ForDay(programme, 1, Day, 1021, 60, [overnight]));
        var first = Block(new(2026, 12, 1, 9, 0, 0), new(2026, 12, 1, 12, 0, 0));
        var overlapping = Block(new(2026, 12, 1, 11, 0, 0), new(2026, 12, 1, 14, 0, 0));
        Invalid(() => ForgeCapacityProjection.ForDay(programme, 1, Day, 0, 0, [first, overlapping]));
        var adjacent = Block(new(2026, 12, 1, 12, 0, 0), new(2026, 12, 1, 14, 0, 0));
        Assert.Equal(1140, ForgeCapacityProjection.ForDay(programme, 1, Day, 1140, 0, [adjacent, first]).AllocatableMinutes);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("date")]
    [InlineData("negative-usable")]
    [InlineData("negative-buffer")]
    [InlineData("oversized-buffer")]
    [InlineData("too-many-blocks")]
    [InlineData("null-blocks")]
    [InlineData("offset")]
    [InlineData("zone")]
    [InlineData("subminute")]
    [InlineData("utc-kind")]
    public void Invalid_capacity_is_rejected_instead_of_clamped_or_fabricated(string field)
    {
        var programme = Programme(); var start = new DateTime(2026, 12, 1, 9, 0, 0); var end = start.AddHours(1);
        var blocks = field switch {
            "too-many-blocks" => Enumerable.Repeat(Block(start, end), 65).ToArray(),
            "null-blocks" => null!,
            "offset" => new[] { Block(start, end, 1, 1) },
            "zone" => new[] { new ForgeCapacityLocalInterval(start, end, "UTC", TimeSpan.Zero, TimeSpan.Zero) },
            "subminute" => new[] { Block(start.AddSeconds(1), end) },
            "utc-kind" => new[] { Block(DateTime.SpecifyKind(start, DateTimeKind.Utc), end) },
            _ => Array.Empty<ForgeCapacityLocalInterval>()
        };
        Invalid(() => ForgeCapacityProjection.ForDay(programme, field == "revision" ? 0 : 1, field == "date" ? Day.AddDays(-1) : Day,
            field == "negative-usable" ? -1 : 0, field == "negative-buffer" ? -1 : field == "oversized-buffer" ? 1441 : 0, blocks));
    }

    [Fact]
    public void Private_projection_cannot_be_serialized_and_owns_a_read_only_collection_snapshot()
    {
        var blocks = new List<ForgeCapacityLocalInterval> { Block(new(2026, 12, 1, 9, 0, 0), new(2026, 12, 1, 17, 0, 0)) };
        var projection = ForgeCapacityProjection.ForDay(Programme(), 1, Day, 120, 60, blocks); blocks.Clear();
        Assert.Single(projection.FixedBlocks);
        Assert.Throws<NotSupportedException>(() => ((IList<ForgeCapacityLocalInterval>)projection.FixedBlocks).Clear());
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(projection));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(projection.FixedBlocks[0]));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<ForgeCapacityProjection>("{}"));
        Assert.DoesNotContain("120", projection.ToString());
    }
}
