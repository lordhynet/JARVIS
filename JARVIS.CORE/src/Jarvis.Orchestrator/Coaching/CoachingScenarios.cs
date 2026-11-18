using System.Text.RegularExpressions;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Coaching;

public sealed record CoachingPersona(string Id, string Name, string Style);
public sealed record CoachingScenarioTurn(string PersonaId, string Question, string Pressure);
public sealed record CoachingRubric(string Dimension, string Description, IReadOnlyList<string> Markers);
public sealed record CoachingScenario(string Key, string Title, IReadOnlyList<CoachingPersona> Personas,
    IReadOnlyList<CoachingScenarioTurn> Turns, IReadOnlyList<CoachingRubric> Rubric);
public sealed record ScenarioCoverage(string Dimension, int Score, int Maximum, string Basis);
public sealed record ScenarioEvidence(Guid AttemptId, int Revision, Guid? VoiceTurnId, Guid? StoryId, int? StoryVersion);
public sealed record CoachingScenarioDebrief(string ScenarioKey, string Title, string Status, int Answers,
    IReadOnlyList<ScenarioCoverage> Scores, IReadOnlyList<string> FollowUps, IReadOnlyList<ScenarioEvidence> Evidence,
    IReadOnlyList<SpeechDeliverySession> Delivery, string AssessmentKind = "Authored text checklist coverage; not an independent competence grade");

public static class CoachingScenarios
{
    private static readonly CoachingRubric[] Rubric = [
        new("Structure", "State the point, explain a reason and finish with a next step.", ["point", "because", "next", "recommend"]),
        new("Ownership", "Separate personal contribution from team contribution.", ["I chose", "I owned", "I decided", "we"]),
        new("Trade-offs", "Explain an alternative, a constraint and a failure mode.", ["alternative", "constraint", "trade-off", "failure"]),
        new("Evidence", "Distinguish supplied evidence, a measurement and what is unknown.", ["evidence", "measured", "source", "unknown"])
    ];
    public static IReadOnlyList<CoachingScenario> All { get; } = Array.AsReadOnly(new CoachingScenario[] {
        new("scenario-panel", "Panel interview", [new("chair", "Hiring manager", "Calm and concise"), new("engineer", "Senior engineer", "Sceptical about assumptions")], [
            new("chair", "Describe one real delivery, your contribution and its outcome. Use only facts you can defend.", "Opening"),
            new("engineer", "Which alternative did you reject, and which failure mode did you accept?", "Defend a trade-off"),
            new("chair", "What evidence supports the result, and what would make you revisit the decision?", "Evidence challenge")], Rubric),
        new("scenario-recruiter", "Recruiter screen", [new("recruiter", "Recruiter", "Friendly, time limited and concrete")], [
            new("recruiter", "Introduce your current scope and what you want next, using a short factual example.", "Opening"),
            new("recruiter", "Why is this move right now? Distinguish what you know about a role from assumptions.", "Clarify motivation"),
            new("recruiter", "Give a concise reason to progress the conversation without adding unsupported achievements.", "Concise close")], Rubric),
        new("scenario-presentation", "Presentation rehearsal", [new("audience", "Audience member", "Needs a clear recommendation"), new("stakeholder", "Stakeholder", "Challenges cost and impact")], [
            new("audience", "Present one recommendation: lead with the point, give one reason and name the decision you need.", "Opening"),
            new("stakeholder", "Remove the jargon. What changes for the listener, and which constraint matters most?", "Simplify under pressure"),
            new("audience", "Which assumption is weakest, and what evidence or next step would resolve it?", "Question and close")], Rubric),
        new("scenario-hostile-qa", "Hostile Q&A rehearsal", [new("skeptic", "Sceptical stakeholder", "Firm and respectful; challenges claims, never insults")], [
            new("skeptic", "I am not convinced by your recommendation. State the point and the evidence you can defend.", "Sceptical opening"),
            new("skeptic", "Your preferred approach might fail. Explain a rejected alternative and the recovery plan.", "Escalating challenge"),
            new("skeptic", "What do you not know? Say what would change your recommendation without bluffing.", "Uncertainty challenge")], Rubric)
    }.Select(s => s with { Personas = Array.AsReadOnly(s.Personas.ToArray()), Turns = Array.AsReadOnly(s.Turns.ToArray()),
        Rubric = Array.AsReadOnly(s.Rubric.Select(r => r with { Markers = Array.AsReadOnly(r.Markers.ToArray()) }).ToArray()) }).ToArray());
    public static CoachingScenario Get(string key) => All.FirstOrDefault(s => s.Key == key) ?? throw new DomainException(404, "Coaching scenario is not installed.");
    public static bool Contains(string key) => All.Any(s => s.Key == key);
    public static string ExerciseId(string key, int index) => index == 0 ? key : key + "-turn-" + (index + 1);
    public static IReadOnlyList<CoachingExercise> Exercises { get; } = All.SelectMany(s => s.Turns.Select((turn, i) => new CoachingExercise(
        ExerciseId(s.Key, i), 1, s.Key == "scenario-presentation" ? CoachingArea.Communication : CoachingArea.Interview,
        s.Title + " · " + (i + 1), s.Personas.Single(p => p.Id == turn.PersonaId).Name + ": " + turn.Question,
        ["Point", "Reason", "Personal contribution", "Evidence or uncertainty", "Stop"], [turn.Pressure], 90))).ToArray();
    public static IReadOnlyList<ScenarioCoverage> Score(CoachingScenario scenario, IEnumerable<string> answers)
    {
        var text = string.Join("\n", answers);
        return scenario.Rubric.Select(r => new ScenarioCoverage(r.Dimension, r.Markers.Count(marker => Regex.IsMatch(text,
            @"(?<!\p{L})" + Regex.Escape(marker) + @"(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)), r.Markers.Count,
            "Presence of authored English text markers only; evidence and competence are not independently verified.")).ToArray();
    }
}
