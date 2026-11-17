using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Training;

public static class QuestionBank
{
    private static TrainingQuestion Q(int number, LearningDomain domain, string title, string prompt,
        string reference, string source, params string[] followUps) =>
        new(Guid.Parse($"735a3100-44bf-4f9b-8db5-{number:000000000000}"), 1, domain, title, prompt, reference, followUps, source);

    public static IReadOnlyList<TrainingQuestion> All { get; } = Array.AsReadOnly(new[]
    {
        Q(1, LearningDomain.CSharpDotNet, "Async without extra threads",
            "An ASP.NET Core endpoint waits for two independent remote reads. Explain how you would implement it and what await does to the request thread.",
            "Separate I/O waiting from CPU work. Await asynchronous I/O rather than blocking. Start independent operations before awaiting their combined completion; discuss cancellation, exceptions and provider limits. Task.Run is not a general remedy for asynchronous network I/O.",
            "https://learn.microsoft.com/en-us/dotnet/csharp/asynchronous-programming/async-scenarios", "What changes if the second read depends on the first?", "How would you bound fan-out?"),
        Q(2, LearningDomain.CSharpDotNet, "Dependency lifetime ownership",
            "A singleton background worker needs an EF Core DbContext. Explain the lifetime problem and a safe design.",
            "A singleton must not capture a scoped context for its entire lifetime. Create a scope per unit of work or use an appropriate context factory, dispose each context, and avoid concurrently using the same DbContext instance.",
            "https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection", "What is a captive dependency?", "Where would you put the transaction boundary?"),
        Q(3, LearningDomain.DistributedSystems, "Acknowledgement is not exactly once",
            "A RabbitMQ consumer commits a database change, then loses its connection before acknowledging the message. What can happen and how do you prevent duplicate effects?",
            "The message can be delivered again. Make the business effect idempotent, commonly with a durable message identity and a unique inbox entry committed with the effect. Acknowledge after the commit. Publisher confirms and consumer acknowledgements cover different parts of delivery.",
            "https://www.rabbitmq.com/docs/confirms", "What if the process fails before committing?", "How would poison messages be handled?"),
        Q(4, LearningDomain.DistributedSystems, "The dual-write gap",
            "A service must commit an order and publish an event. Why can two ordinary writes lose an event, and how does an outbox help?",
            "A failure between the database commit and broker publish can leave inconsistent state. Persist the order and an outbox intent in one transaction, then relay the intent. Relay retries can duplicate delivery, so consumers still need idempotency and operational recovery.",
            "https://learn.microsoft.com/en-us/azure/architecture/databases/guide/transactional-outbox-cosmos", "What if publication succeeds but marking sent fails?", "How would you monitor relay lag?"),
        Q(5, LearningDomain.Databases, "Isolation under concurrency",
            "Two requests both read an available balance before updating it. Explain why a transaction alone may not prevent an invalid result.",
            "Correctness depends on isolation and the update strategy, not merely opening a transaction. Consider a conditional atomic update, a suitable row lock, or serializable execution with retry handling. Define the invariant and test competing transactions.",
            "https://www.postgresql.org/docs/current/transaction-iso.html", "Which failures are safe to retry?", "What changes when multiple rows are involved?"),
        Q(6, LearningDomain.Databases, "Index with evidence",
            "A frequently used query becomes slow as a table grows. Explain how you would investigate before adding an index.",
            "Inspect the actual query shape and execution plan, selectivity, row counts, joins and ordering. Consider a suitable index and measure its read benefit against write, storage and maintenance costs. Recheck the plan and latency with representative data.",
            "https://www.postgresql.org/docs/current/using-explain.html", "Why might an existing index be unused?", "How could an index hurt writes?"),
        Q(7, LearningDomain.ApiSecurity, "Authorization at the resource",
            "An authenticated caller changes an order ID in a URL. Explain the checks needed before returning or changing the order.",
            "Authentication does not establish access to every resource. Check the authenticated identity's authorization for the specific order and action, scope data access appropriately, and avoid trusting a user ID supplied by the caller. Test cross-user access and safe error responses.",
            "https://owasp.org/API-Security/editions/2023/en/0xa1-broken-object-level-authorization/", "What would you enforce at the query boundary?", "How would background jobs retain the correct ownership scope?"),
        Q(8, LearningDomain.ApiSecurity, "Idempotent command contract",
            "A client times out after submitting a command. Design a retry contract that distinguishes the same request from reuse of its key for different input.",
            "Use a stable operation identity, validate its owner and payload, and durably associate it with a canonical result. Matching retries return that result; conflicting payload reuse must fail. A process-local cache alone does not provide restart-safe deduplication.",
            "https://aws.amazon.com/builders-library/making-retries-safe-with-idempotent-APIs/", "What if the response is lost after commit?", "When may operation identity expire?"),
        Q(9, LearningDomain.ProductionDevOps, "Deploy without a schema cliff",
            "You need to rename a database column while old and new application versions may run together. Describe a safe rollout.",
            "Plan an expand-and-contract rollout: introduce compatible schema, migrate and verify data, deploy compatible readers and writers, then remove the old shape only after old versions are gone. Define observable checks and recovery before each irreversible step.",
            "https://learn.microsoft.com/en-us/azure/well-architected/operational-excellence/safe-deployments", "Which part can safely roll back?", "How would you detect incomplete backfill?"),
        Q(10, LearningDomain.ProductionDevOps, "Symptoms before restart",
            "A release increases API latency and timeouts. Explain your first investigation and how you would decide whether to roll back.",
            "Establish impact and timing, compare the release with a known baseline, inspect saturation and dependency latency, and use correlated telemetry. Choose a bounded mitigation or rollback based on evidence, then verify recovery; preserve enough evidence for follow-up.",
            "https://learn.microsoft.com/en-us/azure/well-architected/operational-excellence/observability", "What if only one tenant is affected?", "How would you separate a database bottleneck from a provider outage?"),
        Q(11, LearningDomain.PaymentsFintech, "Timeout is not failure",
            "A payment provider times out after receiving a request. What state should your system use and what must happen before sending another payment?",
            "Treat an ambiguous outcome as unknown or pending rather than automatically failed. Persist the provider operation identity, use status lookup or reconciliation, and reuse an idempotency key where its contract supports safe retry. Prevent a second business payment from being created accidentally.",
            "https://aws.amazon.com/builders-library/making-retries-safe-with-idempotent-APIs/", "What if the provider offers no lookup or safe replay?", "How would the customer see the pending state?"),
        Q(12, LearningDomain.PaymentsFintech, "Reconciliation beyond happy paths",
            "Your internal ledger and a provider report disagree about a transaction. Explain a reconciliation process that does not silently rewrite history.",
            "Match stable transaction identities and amounts, classify missing or inconsistent outcomes, retain an audit trail and separate automatic safe corrections from cases requiring review. Make rerunning the reconciliation idempotent and verify accounting invariants.",
            "https://aws.amazon.com/builders-library/making-retries-safe-with-idempotent-APIs/", "What if a report arrives late?", "Which correction needs approval?"),
        Q(13, LearningDomain.SystemDesign, "Requirements before components",
            "Design a notification service. Start by clarifying requirements and constraints before naming infrastructure.",
            "Clarify recipients, channels, urgency, throughput, ordering, duplicate tolerance, privacy and failure expectations. Then explain ownership, durable intents, dispatch limits, retries, deduplication and observability, with explicit trade-offs rather than a list of products.",
            "https://learn.microsoft.com/en-us/azure/architecture/guide/design-principles/", "What happens when a provider is unavailable?", "How do you stop stale notifications?"),
        Q(14, LearningDomain.SystemDesign, "One owner across devices",
            "A voice session moves between two devices. Design ownership transfer so that a disconnected old device does not keep speaking indefinitely.",
            "Use authoritative ownership, expiring leases and fencing tokens. Persist the checkpoint and transfer state, require the source to stop or its lease to expire, and enforce local expiry for already buffered audio. Define recovery before and after the transfer commit.",
            "https://learn.microsoft.com/en-us/azure/architecture/guide/design-principles/", "Why is a server token alone insufficient?", "What if the target fails after commit?"),
        Q(15, LearningDomain.LeetCode, "Two Sum without hand-waving",
            "Explain a linear-time approach to finding two distinct array entries that sum to a target, including duplicates and complexity trade-offs.",
            "Track previously seen values and their indices. Before adding the current value, look up its complement, which prevents using the same entry twice. Discuss duplicate values, no-solution behavior, arithmetic overflow and the extra memory cost of the lookup structure.",
            "https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2", "How does a sorted input change your options?", "What assumptions underlie expected constant-time lookup?"),
        Q(16, LearningDomain.LeetCode, "Binary search invariant",
            "Explain binary search using an explicit interval invariant. What causes off-by-one errors, and how would you test the implementation?",
            "Choose consistent closed or half-open bounds and explain which candidates remain after each comparison. Ensure the interval shrinks, calculate the midpoint safely, and test empty, single-entry, boundary, absent-target and duplicate cases. State the required sorted-order assumption.",
            "https://learn.microsoft.com/en-us/dotnet/api/system.array.binarysearch", "How do you find the first matching duplicate?", "Why does the loop always terminate?")
    });

    public static TrainingQuestion Get(Guid id) => All.FirstOrDefault(x => x.Id == id)
        ?? throw new Runtime.DomainException(404, "Training question not found.");
}
