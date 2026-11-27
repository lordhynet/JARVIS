using Jarvis.Contracts;
using Jarvis.Orchestrator.Lab;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Tests;

public sealed class LabTimelineTests
{
    [Fact]
    public void Version_two_has_a_pinned_seeded_timeline_and_state_fixture()
    {
        var run = PaymentQueueSimulator.Run(new(7, 1));
        Assert.Equal("payment-queue-v2", run.EngineVersion); Assert.Equal([22536L], run.AmountsMinor.ToArray());
        Assert.Equal(new[] { (2, "api.accepted", 0L, 0, 0), (3, "broker.published", 0L, 0, 1),
            (6, "worker.received", 0L, 0, 0), (6, "ledger.posted", 22536L, 1, 0), (7, "broker.acked", 22536L, 1, 0) },
            run.Events.Select(e => (e.Tick, e.Kind, e.LedgerTotalMinor, e.LedgerEntries, e.QueueDepth)));
    }

    private static void AssertChronological(LabSimulationRun run) =>
        Assert.Equal(run.Events.Select(e => e.Tick).Order(), run.Events.Select(e => e.Tick));

    [Fact]
    public void Clean_timeline_is_chronological() => AssertChronological(PaymentQueueSimulator.Run(new(7, 6)));

    [Fact]
    public void Late_ack_and_interleaved_deliveries_stay_chronological() => AssertChronological(
        PaymentQueueSimulator.Run(new(42, 4, Faults: [new("delayed-ack", 1), new("crash-after-commit", 2)])));

    [Fact]
    public void Duplicate_poison_delivery_cannot_exceed_attempt_limit_or_duplicate_dlq()
    {
        var run = PaymentQueueSimulator.Run(new(42, 1, 1, Faults: [new("duplicate-delivery", 1), new("poison", 1)]));
        Assert.Single(run.Events, e => e.Kind == "worker.received");
        Assert.Equal([1], run.DeadLettered.ToArray()); Assert.True(run.AllInvariantsHeld);
    }

    [Fact]
    public void Lost_ack_after_final_commit_does_not_erase_committed_money_from_expectation()
    {
        var run = PaymentQueueSimulator.Run(new(42, 1, 1, Faults: [new("crash-after-commit", 1)]));
        Assert.Single(run.Ledger); Assert.Equal([1], run.DeadLettered.ToArray());
        Assert.Equal(run.AmountsMinor[0], run.ExpectedTotalMinor); Assert.True(run.AllInvariantsHeld);
    }

    [Fact]
    public void Persistent_poison_cannot_claim_to_start_at_a_later_attempt() =>
        Assert.Equal(400, Assert.Throws<DomainException>(() => PaymentQueueSimulator.Run(
            new(42, 1, 3, Faults: [new("poison", 1, 2)]))).StatusCode);

    [Fact]
    public void Mixed_faults_preserve_chronology_snapshots_and_bounded_idempotent_accounting()
    {
        foreach (var seed in Enumerable.Range(0, 20))
        foreach (var limit in new[] { 1, 2, 3, 10 })
        {
            var run = PaymentQueueSimulator.Run(new(seed, 4, limit, true, [new("poison", 1), new("duplicate-delivery", 2),
                new("crash-after-commit", 2), new("delayed-ack", 3), new("crash-before-commit", 4)]));
            AssertChronological(run); Assert.True(run.AllInvariantsHeld);
            Assert.Equal(Enumerable.Range(1, run.Events.Count), run.Events.Select(e => e.Step));
            Assert.Equal(run.DeadLettered.Distinct().Count(), run.DeadLettered.Count);
            foreach (var group in run.Events.Where(e => e.Kind == "worker.received").GroupBy(e => e.Payment))
            { Assert.InRange(group.Count(), 1, limit); Assert.Equal(Enumerable.Range(1, group.Count()), group.Select(e => e.Attempt)); }
            long total = 0; var entries = 0; var dead = 0; var queued = 0;
            foreach (var e in run.Events)
            {
                if (e.Kind == "ledger.posted") { total += run.AmountsMinor[e.Payment - 1]; entries++; }
                if (e.Kind == "dlq.moved") dead++;
                if (e.Kind is "broker.published" or "broker.duplicated" or "broker.redeliver-scheduled") queued++;
                if (e.Kind == "worker.received") queued--;
                Assert.Equal(total, e.LedgerTotalMinor); Assert.Equal(entries, e.LedgerEntries);
                Assert.Equal(dead, e.DeadLetters); Assert.Equal(queued, e.QueueDepth); Assert.True(queued >= 0);
            }
            Assert.Equal(0, queued); Assert.Equal(run.Ledger.Count, entries); Assert.Equal(run.DeadLettered.Count, dead);
            Assert.Equal(run.ExpectedTotalMinor, total);
        }
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Maximum_poison_workload_is_bounded_and_settles_each_payment_once(int seed)
    {
        var run = PaymentQueueSimulator.Run(new(seed, PaymentQueueSimulator.MaxPayments, PaymentQueueSimulator.MaxAttempts,
            Faults: Enumerable.Range(1, PaymentQueueSimulator.MaxPayments).Select(p => new LabSimulationFault("poison", p)).ToArray()));
        AssertChronological(run); Assert.True(run.AllInvariantsHeld); Assert.Empty(run.Ledger);
        Assert.Equal(PaymentQueueSimulator.MaxPayments, run.DeadLettered.Count);
        Assert.Equal(PaymentQueueSimulator.MaxPayments * PaymentQueueSimulator.MaxAttempts, run.Events.Count(e => e.Kind == "worker.received"));
        Assert.Equal(0, run.Events[^1].QueueDepth);
    }
}
