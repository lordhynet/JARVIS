using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Lab;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class LabSimulationTests
{
    private static LabSimulationRun Run(bool idempotent, params LabSimulationFault[] faults) =>
        PaymentQueueSimulator.Run(new(Seed: 42, Payments: 4, MaxAttempts: 3, IdempotentWorker: idempotent, Faults: faults));
    private static bool Held(LabSimulationRun run, string key) => run.Invariants.Single(i => i.Key == key).Held;

    [Fact]
    public void Same_request_gives_an_identical_run_and_a_different_seed_changes_amounts()
    {
        var request = new LabSimulationRequest(7, 6, 3, true, [new("duplicate-delivery", 2), new("crash-after-commit", 3)]);
        var a = JsonSerializer.Serialize(PaymentQueueSimulator.Run(request));
        var b = JsonSerializer.Serialize(PaymentQueueSimulator.Run(request with { Faults = [new("duplicate-delivery", 2), new("crash-after-commit", 3)] }));
        Assert.Equal(a, b);
        Assert.NotEqual(PaymentQueueSimulator.Run(request).AmountsMinor, PaymentQueueSimulator.Run(request with { Seed = 8 }).AmountsMinor);
        Assert.All(PaymentQueueSimulator.Run(request).AmountsMinor, amount => Assert.InRange(amount, 100, 50_000));
        // Pinned values guard against an accidental engine change without a version bump.
        Assert.Equal(new long[] { 22536, 34660, 45614, 21876, 21642, 7929 }, PaymentQueueSimulator.Run(request).AmountsMinor);
    }

    [Fact]
    public void A_clean_run_posts_every_payment_once_and_holds_every_invariant()
    {
        var run = Run(true);
        Assert.True(run.AllInvariantsHeld); Assert.Equal(4, run.Ledger.Count); Assert.Empty(run.DeadLettered);
        Assert.Equal(run.AmountsMinor.Sum(), run.ExpectedTotalMinor);
        Assert.Equal(run.Events.Select((_, i) => i + 1), run.Events.Select(e => e.Step));
        Assert.Equal(run.ExpectedTotalMinor, run.Events[^1].LedgerTotalMinor);
    }

    [Theory]
    [InlineData("duplicate-delivery")]
    [InlineData("crash-after-commit")]
    [InlineData("delayed-ack")]
    public void Redelivery_after_a_post_double_charges_only_without_idempotency(string kind)
    {
        var safe = Run(true, new LabSimulationFault(kind, 2));
        Assert.True(safe.AllInvariantsHeld); Assert.Contains(safe.Events, e => e.Kind == "worker.duplicate-skipped" && e.Payment == 2);
        var unsafeRun = Run(false, new LabSimulationFault(kind, 2));
        Assert.False(Held(unsafeRun, "no-double-post")); Assert.False(Held(unsafeRun, "ledger-total"));
        Assert.Equal(2, unsafeRun.Ledger.Count(e => e.Payment == 2));
        Assert.Contains("pay-2", unsafeRun.Invariants.Single(i => i.Key == "no-double-post").Evidence);
    }

    [Fact]
    public void A_crash_before_commit_is_safe_even_without_idempotency()
    {
        var run = Run(false, new LabSimulationFault("crash-before-commit", 1));
        Assert.True(run.AllInvariantsHeld);
        var posted = Assert.Single(run.Ledger, e => e.Payment == 1); Assert.Equal(2, posted.Attempt);
    }

    [Fact]
    public void A_poison_message_is_retried_to_the_limit_then_dead_lettered_without_breaking_the_ledger()
    {
        var run = Run(true, new LabSimulationFault("poison", 3));
        Assert.Equal([3], run.DeadLettered.ToArray());
        Assert.Equal(3, run.Events.Count(e => e.Kind == "worker.failed" && e.Payment == 3));
        Assert.DoesNotContain(run.Ledger, e => e.Payment == 3);
        Assert.True(run.AllInvariantsHeld); Assert.Equal(run.AmountsMinor.Sum() - run.AmountsMinor[2], run.ExpectedTotalMinor);
        Assert.Equal(1, run.Events[^1].DeadLetters);
    }

    [Fact]
    public void Requests_outside_the_bounds_are_refused()
    {
        LabSimulationRequest[] bad =
        [
            new(1, 0), new(1, PaymentQueueSimulator.MaxPayments + 1), new(1, 3, 0), new(1, 3, PaymentQueueSimulator.MaxAttempts + 1),
            new(1, 3, 3, true, [new("unplug-the-server", 1)]), new(1, 3, 3, true, [new("poison", 4)]),
            new(1, 3, 3, true, [new("poison", 1, 4)]), new(1, 3, 3, true, [new("poison", 1), new("poison", 1)]),
            new(1, 3, 3, true, null, "bank-transfer")
        ];
        foreach (var request in bad) Assert.Equal(400, Assert.Throws<DomainException>(() => PaymentQueueSimulator.Run(request)).StatusCode);
    }

}
