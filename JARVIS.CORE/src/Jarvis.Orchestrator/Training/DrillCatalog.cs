using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Training;

public static class DrillCatalog
{
    public static IReadOnlyList<DrillModeInfo> Modes { get; } = new List<DrillModeInfo>
    {
        new(TrainingMode.RapidDrill, "Rapid drill", "One focused answer; aim for 20–40 seconds. Timing is a target, not a measurement."),
        new(TrainingMode.DeepDive, "Deep dive", "Answer a core question, then two follow-ups on the same concept."),
        new(TrainingMode.ExplainSimply, "Explain simply", "Teach a junior colleague using plain language and a limited analogy."),
        new(TrainingMode.ArchitectureChallenge, "Architecture challenge", "Clarify constraints, defend a design, then handle its failure paths."),
        new(TrainingMode.FailureInjection, "Failure injection", "Describe the normal path, then reason through an explicit injected failure."),
        new(TrainingMode.ProductionIncident, "Production incident", "Triage a fictional incident, propose bounded mitigation, and verify recovery."),
        new(TrainingMode.CodeReview, "Code review", "Review a concrete C# fragment, then propose a correction and tests. Code is not executed."),
        new(TrainingMode.RandomMixedInterview, "Mixed interview", "Three questions from different domains, shuffled once and saved for reliable resume."),
        new(TrainingMode.SpacedReview, "Spaced review", "Retrieve a currently due technical question using the shared Core schedule.")
    }.AsReadOnly();
    private static int Number(TrainingQuestion question) => int.Parse(question.Id.ToString("D")[24..], System.Globalization.CultureInfo.InvariantCulture);
    public static bool Supports(TrainingMode mode, TrainingQuestion question) => mode switch
    {
        TrainingMode.ArchitectureChallenge => Number(question) is 13 or 14,
        TrainingMode.FailureInjection => Number(question) is 3 or 4 or 8 or 11 or 14,
        TrainingMode.ProductionIncident => Number(question) is 9 or 10,
        TrainingMode.CodeReview => Number(question) is 1 or 2 or 5 or 7,
        _ => Enum.IsDefined(mode)
    };
    public static IReadOnlyList<TrainingDrillStep> Build(TrainingMode mode, IReadOnlyList<TrainingQuestion> questions)
    {
        var result = new List<TrainingDrillStep>(); var q = questions[0];
        void Add(string title, string task, string? code = null, int? seconds = null, TrainingQuestion? question = null)
        {
            var source = question ?? q;
            result.Add(new(result.Count, source.Id, source.Version, title, task, source.Reference, source.SourceUrl, code, seconds));
        }
        switch (mode)
        {
            case TrainingMode.DeepDive:
                Add(q.Title, q.Prompt);
                foreach (var followUp in q.FollowUps.Take(2)) Add("Follow-up " + result.Count, q.Prompt + "\nNow focus on: " + followUp);
                break;
            case TrainingMode.ExplainSimply:
                Add("Teach the concept", q.Prompt + "\nExplain this to a junior colleague. Use plain language, one concrete example or analogy, and state where that analogy stops being accurate."); break;
            case TrainingMode.ArchitectureChallenge:
                Add("Requirements and constraints", q.Prompt + "\nFirst: ask clarifying questions and state assumptions. Separate requirements from design choices.");
                Add("Design and trade-offs", q.Prompt + "\nNow propose boundaries, data ownership and request/event flow. Defend one trade-off against an alternative.");
                Add("Failure and recovery", q.Prompt + "\nWalk through one dependency failure and recovery. Explain how you would detect incorrect or stale effects."); break;
            case TrainingMode.FailureInjection:
                Add("Normal path", q.Prompt + "\nFirst outline the normal state transitions and the point where the business effect becomes durable.");
                var fault = Number(q) switch
                {
                    3 => "The consumer dies immediately after its database commit but before acknowledgement.",
                    4 => "The broker accepts publication, then the relay dies before marking the outbox row sent.",
                    8 => "The server commits the command but the client never receives the response.",
                    11 => "The payment provider times out and currently offers neither status lookup nor a documented safe replay contract.",
                    _ => "The source device loses connectivity with buffered audio while the target is taking over."
                };
                Add("Injected failure", "Fictional failure exercise: " + fault + "\nContext: " + q.Prompt + "\nWhat is known, what remains uncertain, and which recovery steps are safe? Explain how you prevent duplicate or stale effects."); break;
            case TrainingMode.ProductionIncident:
                var incident = Number(q) == 9
                    ? "Fictional incident: old application instances report a missing-column error during a mixed-version schema rollout."
                    : "Fictional incident: after a release, p95 API latency rises from 100 ms to 1,800 ms and timeouts increase. Temporal correlation alone does not establish the cause.";
                Add("Triage", incident + "\n" + q.Prompt + "\nIdentify impact, hypotheses, and the evidence you would collect before choosing a cause.");
                Add("Bounded mitigation", incident + "\nPropose a reversible, bounded mitigation. State its risks, approval boundary, rollback criteria and what you would avoid changing blindly.");
                Add("Verify and learn", incident + "\nHow will you verify recovery and data correctness? Identify evidence to retain and a concrete prevention or detection improvement."); break;
            case TrainingMode.CodeReview:
                var code = Number(q) switch
                {
                    1 => "// Review fragment; client is a shared HttpClient.\npublic IActionResult Read() {\n    var first = client.GetStringAsync(\"https://provider.example/a\").Result;\n    var second = client.GetStringAsync(\"https://provider.example/b\").Result;\n    return Ok(new { first, second });\n}",
                    2 => "// Poller is registered as a singleton hosted service.\nclass Poller(AppDbContext db) : BackgroundService {\n    protected override async Task ExecuteAsync(CancellationToken stop) {\n        while (!stop.IsCancellationRequested) {\n            await db.SaveChangesAsync(stop);\n            await Task.Delay(1000, stop);\n        }\n    }\n}",
                    5 => "// Two requests can execute this fragment concurrently.\nvar account = await db.Accounts.FindAsync(accountId);\nif (account.Balance >= amount) {\n    account.Balance -= amount;\n    await db.SaveChangesAsync();\n}",
                    _ => "[Authorize]\n[HttpGet(\"orders/{id}\")]\npublic async Task<IActionResult> Get(Guid id) {\n    var order = await db.Orders.FindAsync(id);\n    return order is null ? NotFound() : Ok(order);\n}"
                };
                Add("Review the fragment", q.Prompt + "\nReview the fragment below. Identify a concrete correctness or security risk, quote the relevant line, and explain the conditions that trigger it.", code);
                Add("Correct and verify", q.Prompt + "\nPropose corrected code or precise pseudocode and tests that reproduce the original risk. Explain a remaining trade-off. This is reasoning practice; the code will not be executed.", code); break;
            case TrainingMode.RandomMixedInterview:
                foreach (var item in questions) Add("Mixed: " + item.Title, item.Prompt, question: item); break;
            default:
                Add(q.Title, q.Prompt, seconds: mode == TrainingMode.RapidDrill ? 40 : null); break;
        }
        Guard.Require(result.Count is >= 1 and <= 3, 500, "Invalid authored drill shape.");
        return result;
    }
}
