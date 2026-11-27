using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Lab;

/// <summary>Bounded teaching simulation on fake payments. All transitions execute by (tick, sequence);
/// integer amounts and SplitMix64 are deterministic. No I/O, AI, runtime state or real payment integration.</summary>
public static class PaymentQueueSimulator
{
    public const string ScenarioKey = "payment-queue";
    public const string EngineVersion = "payment-queue-v2";
    public const int MaxPayments = 50, MaxAttempts = 10, MaxFaults = 50;
    private const int VisibilityTimeout = 30;

    public static LabScenario Scenario { get; } = new(ScenarioKey, "Payment queue: outbox, broker, worker, ledger", EngineVersion,
        MaxPayments, MaxAttempts, MaxFaults, Array.AsReadOnly(new LabFaultKind[]
        {
            new("duplicate-delivery", "Duplicate delivery", "At-least-once delivery needs idempotency; every copy consumes the bounded delivery budget."),
            new("crash-before-commit", "Worker crash before the ledger write", "A redelivered message can be posted once when the earlier attempt did not commit."),
            new("crash-after-commit", "Worker crash after the ledger write, before ack", "A lost ack does not undo committed money; idempotency prevents a second post."),
            new("delayed-ack", "Ack arrives after the visibility timeout", "A timeout can redeliver a message that was already processed."),
            new("poison", "Persistent poison message", "Always fails from the first attempt, then reaches the dead-letter queue at the delivery limit.")
        }));
    private static readonly HashSet<string> Kinds = Scenario.Faults.Select(f => f.Kind).ToHashSet(StringComparer.Ordinal);

    public static LabSimulationRun Run(LabSimulationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var faults = (request.Faults ?? []).ToArray();
        Guard.Require(request.Scenario == ScenarioKey, 400, "Unknown simulation scenario.");
        Guard.Require(request.Payments is >= 1 and <= MaxPayments, 400, $"Payments must be between 1 and {MaxPayments}.");
        Guard.Require(request.MaxAttempts is >= 1 and <= MaxAttempts, 400, $"Max attempts must be between 1 and {MaxAttempts}.");
        Guard.Require(faults.Length <= MaxFaults, 400, $"At most {MaxFaults} faults.");
        foreach (var fault in faults)
            Guard.Require(fault is not null && Kinds.Contains(fault.Kind) && fault.Payment >= 1 && fault.Payment <= request.Payments &&
                fault.Attempt >= 1 && fault.Attempt <= request.MaxAttempts && (fault.Kind != "poison" || fault.Attempt == 1), 400,
                "Each fault needs a known kind, a payment and attempt within bounds; persistent poison uses attempt 1.");
        Guard.Require(faults.Select(f => (f.Kind, f.Payment, f.Attempt)).Distinct().Count() == faults.Length, 400, "Faults must not repeat.");

        var random = new SplitMix64(request.Seed);
        var amounts = Enumerable.Range(0, request.Payments).Select(_ => 100 + (long)random.Next(49_901)).ToArray();
        bool Has(string kind, int payment, int attempt) => faults.Any(f => f.Kind == kind && f.Payment == payment && f.Attempt == attempt);
        bool Poison(int payment) => faults.Any(f => f.Kind == "poison" && f.Payment == payment);
        var events = new List<LabSimulationEvent>(); var ledger = new List<LabLedgerEntry>();
        var processedKeys = new HashSet<string>(StringComparer.Ordinal); var deadLetters = new HashSet<int>();
        var received = new int[request.Payments + 1]; var scheduled = new int[request.Payments + 1];
        var queue = new PriorityQueue<Action, (int Tick, int Seq)>();
        var tick = 0; var seq = 0; var queuedCopies = 0; long total = 0;

        void Emit(string kind, int payment, int attempt, string detail) => events.Add(new(events.Count + 1, tick, kind,
            payment, attempt, detail, total, ledger.Count, queuedCopies, deadLetters.Count));
        void Schedule(int at, Action action)
        {
            if (at < tick) throw new InvalidOperationException("A simulation transition cannot run in the past.");
            queue.Enqueue(action, (at, seq++));
        }
        bool Deliver(int p, int readyAt, bool duplicate = false)
        {
            if (scheduled[p] >= request.MaxAttempts) return false;
            scheduled[p]++; queuedCopies++;
            Schedule(readyAt + 1 + (int)random.Next(3), () => Process(p, duplicate));
            return true;
        }
        void Process(int p, bool duplicate)
        {
            queuedCopies--; var attempt = ++received[p]; var key = $"pay-{p}";
            Emit("worker.received", p, attempt, duplicate ? "Received a duplicate copy." : "Received the message.");
            if (Has("duplicate-delivery", p, attempt))
            {
                if (Deliver(p, tick, true)) Emit("broker.duplicated", p, attempt, "Another copy was queued within the delivery limit.");
                else Emit("broker.duplicate-limit", p, attempt, "No extra copy: the delivery budget is already reserved.");
            }
            void RetryOrDeadLetter(string why, int delay)
            {
                if (attempt >= request.MaxAttempts)
                {
                    if (deadLetters.Add(p)) Emit("dlq.moved", p, attempt, $"{why} Delivery limit reached; acknowledgement failure does not undo a ledger commit.");
                }
                else if (Deliver(p, tick + delay)) Emit("broker.redeliver-scheduled", p, attempt, $"{why} Redelivery scheduled.");
                else Emit("broker.retry-coalesced", p, attempt, $"{why} Remaining delivery budget is already queued or consumed.");
            }
            if (Poison(p))
            {
                Emit("worker.failed", p, attempt, "Persistent poison: the message cannot be handled.");
                Schedule(tick + 1, () => RetryOrDeadLetter("Nacked.", VisibilityTimeout)); return;
            }
            if (Has("crash-before-commit", p, attempt))
            {
                Emit("worker.crashed", p, attempt, "Crashed before commit; nothing posted.");
                Schedule(tick + 1, () => RetryOrDeadLetter("No ack.", VisibilityTimeout)); return;
            }
            if (request.IdempotentWorker && processedKeys.Contains(key))
                Emit("worker.duplicate-skipped", p, attempt, $"Idempotency key {key} already processed; nothing posted.");
            else
            {
                processedKeys.Add(key); total += amounts[p - 1]; ledger.Add(new(p, key, amounts[p - 1], attempt, tick));
                Emit("ledger.posted", p, attempt, $"Posted {amounts[p - 1]} minor units.");
            }
            if (Has("crash-after-commit", p, attempt))
            {
                Schedule(tick + 1, () => { Emit("worker.crashed", p, attempt, "Crashed after commit, before ack."); RetryOrDeadLetter("No ack.", VisibilityTimeout); }); return;
            }
            if (Has("delayed-ack", p, attempt))
            {
                Schedule(tick + VisibilityTimeout, () => RetryOrDeadLetter("Ack timeout.", 1));
                Schedule(tick + VisibilityTimeout + 1, () => Emit("broker.ack-late", p, attempt, "Ack arrived after timeout; committed money remains posted.")); return;
            }
            Schedule(tick + 1, () => Emit("broker.acked", p, attempt, "Acked this delivery; an already queued copy may still arrive."));
        }
        for (var payment = 1; payment <= request.Payments; payment++)
        {
            var p = payment;
            Schedule(p * 2, () => {
                Emit("api.accepted", p, 0, $"Fake payment pay-{p} for {amounts[p - 1]} minor units; payment and outbox accepted together.");
                Schedule(tick + 1, () => { Deliver(p, tick); Emit("broker.published", p, 0, "Outbox relay published the message."); });
            });
        }
        while (queue.TryDequeue(out var transition, out var at)) { tick = at.Tick; transition(); }

        var settled = Enumerable.Range(1, request.Payments).Where(p => !deadLetters.Contains(p)).ToArray();
        var expected = Enumerable.Range(1, request.Payments).Where(p => !deadLetters.Contains(p) || processedKeys.Contains($"pay-{p}")).Sum(p => amounts[p - 1]);
        var postedTwice = ledger.GroupBy(e => e.Payment).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        var unposted = settled.Where(p => ledger.All(e => e.Payment != p)).ToArray();
        var invariants = new[]
        {
            new LabSimulationInvariant("no-double-post", "No payment is posted to the ledger more than once.", postedTwice.Length == 0,
                postedTwice.Length == 0 ? "Every payment has at most one ledger entry." : $"Posted more than once: {string.Join(", ", postedTwice.Select(p => $"pay-{p}"))}."),
            new LabSimulationInvariant("ledger-total", "Each committed or non-dead-lettered payment contributes its amount exactly once.", total == expected,
                $"Ledger {total}, expected {expected} minor units; lost acknowledgement cannot reverse committed money."),
            new LabSimulationInvariant("settled-or-dead-lettered", "Every payment is either posted or in the dead-letter queue.", unposted.Length == 0,
                unposted.Length == 0 ? "No payment was lost." : $"Lost: {string.Join(", ", unposted.Select(p => $"pay-{p}"))}."),
            new LabSimulationInvariant("dlq-after-retries", "Messages reach the dead-letter queue only at the delivery limit.",
                deadLetters.All(p => received[p] == request.MaxAttempts), $"{deadLetters.Count} distinct payments dead-lettered; limit {request.MaxAttempts} deliveries.")
        };
        return new(ScenarioKey, EngineVersion, request with { Faults = Array.AsReadOnly(faults) }, Array.AsReadOnly(amounts), events.AsReadOnly(), ledger.AsReadOnly(),
            Array.AsReadOnly(deadLetters.Order().ToArray()), expected, Array.AsReadOnly(invariants), invariants.All(i => i.Held),
            "Bounded educational model on fake payments; not a production broker or payment system. No AI, I/O or saved state.");
    }
    private sealed class SplitMix64(int seed)
    {
        private ulong state = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL + 0xD1B54A32D192ED03UL);
        public ulong Next(int bound)
        {
            var z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return (z ^ (z >> 31)) % (ulong)bound;
        }
    }
}
