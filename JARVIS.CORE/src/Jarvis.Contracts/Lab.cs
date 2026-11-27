namespace Jarvis.Contracts;

/// <summary>
/// Core Engineering Lab: a bounded, deterministic teaching simulation on fake transactions.
/// Faults target one payment and, where relevant, one delivery attempt (1-based).
/// Kinds: duplicate-delivery, crash-before-commit, crash-after-commit, delayed-ack, poison.
/// Poison persists from attempt 1. Every queued delivery copy consumes the per-payment attempt budget.
/// </summary>
public sealed record LabSimulationFault(string Kind, int Payment, int Attempt = 1);

/// <summary>Seeded run request. The same request always produces the same run (engine version permitting).</summary>
public sealed record LabSimulationRequest(int Seed, int Payments = 5, int MaxAttempts = 3, bool IdempotentWorker = true,
    IReadOnlyList<LabSimulationFault>? Faults = null, string Scenario = "payment-queue");

/// <summary>One step of the timeline with the visible state after it, so a client can step and rewind without recomputing.</summary>
public sealed record LabSimulationEvent(int Step, int Tick, string Kind, int Payment, int Attempt, string Detail,
    long LedgerTotalMinor, int LedgerEntries, int QueueDepth, int DeadLetters);

public sealed record LabSimulationInvariant(string Key, string Description, bool Held, string Evidence);

public sealed record LabLedgerEntry(int Payment, string IdempotencyKey, long AmountMinor, int Attempt, int Tick);

public sealed record LabSimulationRun(string Scenario, string EngineVersion, LabSimulationRequest Request,
    IReadOnlyList<long> AmountsMinor, IReadOnlyList<LabSimulationEvent> Events, IReadOnlyList<LabLedgerEntry> Ledger,
    IReadOnlyList<int> DeadLettered, long ExpectedTotalMinor, IReadOnlyList<LabSimulationInvariant> Invariants, bool AllInvariantsHeld, string Note);

public sealed record LabFaultKind(string Kind, string Title, string Teaches);
public sealed record LabScenario(string Key, string Title, string EngineVersion, int MaxPayments, int MaxAttempts, int MaxFaults,
    IReadOnlyList<LabFaultKind> Faults);
