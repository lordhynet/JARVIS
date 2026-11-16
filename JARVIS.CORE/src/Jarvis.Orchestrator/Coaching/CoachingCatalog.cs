using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Coaching;

public static class CoachingCatalog
{
    private static readonly string[] StoryFrame = ["Frame", "Problem", "Personal ownership", "Decision/action", "Outcome", "Stop"];
    public static IReadOnlyList<CoachingExercise> Exercises { get; } = Array.AsReadOnly(new[]
    {
        C("slow-warmup", "Slow-speech warm-up", "Describe your day deliberately. Pause before your opening, finish one idea, then stop.", 120),
        C("point-first", "Point first", "Give a workplace update: lead with the outcome, add one reason and finish with the next action.", 45),
        C("explain-simply", "Explain simply", "Explain one engineering concept to a non-technical listener. Define unfamiliar terms and use one concrete example.", 60),
        C("small-talk", "Natural small talk", "A colleague asks what you have been enjoying outside work. Give a natural reply and ask one genuine follow-up question.", 45),
        C("workplace-update", "Workplace update", "Explain a blocker, its impact and the specific help or decision you need.", 60),
        C("slower-repeat", "Say it again, slower", "Repeat a recent explanation with shorter sentences and deliberate pauses. Aim for a calmer delivery; no pace improvement is inferred from text.", 60),
        C("recovery", "Recover your structure", "You lose your train of thought in a meeting. Use a brief recovery phrase, state the key point and finish with the next step.", 45),
        C("daily-review", "End-of-day review", "Recall one conversation that went well, one moment you rushed and one deliberate change for tomorrow.", 120),
        I("story-30", "Story in 30 seconds", "Tell one real story using only the facts you can defend.", 30, true),
        I("story-60", "Story in 60 seconds", "Explain a real engineering decision, your contribution and its outcome.", 60, true),
        I("cv-defence", "CV claim defence", "Defend a saved CV claim: define its scope, your exact contribution, evidence and limitations.", 90, true, true),
        I("recruiter", "Recruiter screen", "Introduce your recent work, relevant strengths and what you want from your next role.", 60),
        I("hiring-manager", "Hiring manager", "Describe how you delivered a useful outcome under a difficult constraint.", 90, true),
        I("engineering-manager", "Engineering manager", "Explain a time you aligned people around a technical decision. Separate your contribution from the team's.", 90, true),
        I("technical-cv", "Technical CV deep dive", "Explain your exact service boundary, one technical decision and how you verified it worked.", 120, true),
        I("senior-engineer", "Senior engineer", "Defend a technical trade-off and explain the failure mode you deliberately accepted.", 120, true),
        I("staff-principal", "Staff/principal challenge", "Explain a cross-team architectural choice, its alternatives, operational consequences and how you would revisit it.", 120, true),
        I("behavioural", "Behavioural interview", "Describe a disagreement or setback, what you personally did and what you learned.", 90, true),
        new CoachingExercise("architecture", 1, CoachingArea.Interview, "System design structure", "Design a reliable notification service. Clarify requirements before choosing components.",
            ["Requirements", "Constraints", "Architecture", "Why", "Trade-off", "Failure mode", "Recovery", "Scale"],
            ["What happens when a provider is unavailable?", "Which component owns durable state?", "How will you know the design is failing?"], 120),
        I("code-review", "Code review reasoning", "A service retries every failed payment request without an operation identity. Explain the risks, the changes you would request and how you would test them.", 90),
        I("production-incident", "Production incident", "Describe an incident: evidence, containment, your action, recovery and prevention. Distinguish what you knew then from hindsight.", 120, true),
        I("payments", "Payments/fintech", "A provider request times out after dispatch. Explain safe handling of an unknown outcome, retries, reconciliation and customer-visible state.", 90),
        I("phone-screen", "Mock phone screen", "Introduce your role, explain one concrete service you owned and answer a follow-up without relying on diagrams.", 90, true),
        I("interview-debrief", "Interview debrief", "Record the questions you remember, what you said, where you hesitated and which facts or topics need verification.", 180)
    });
    private static CoachingExercise C(string id, string title, string prompt, int seconds) => new(id, 1, CoachingArea.Communication,
        title, prompt, ["Pause", "Point", "One supporting detail", "Stop"], ["Can you put the point in your first sentence?", "What can you remove without losing meaning?"], seconds);
    private static CoachingExercise I(string id, string title, string prompt, int seconds, bool story = false, bool claim = false) =>
        new(id, 1, CoachingArea.Interview, title, prompt, StoryFrame, ["What did you personally own?", "Which alternative did you reject and why?", "What evidence supports the outcome?"], seconds, story, claim);
    public static CoachingExercise Get(string id) => Exercises.FirstOrDefault(x => x.Id == id) ?? throw new DomainException(404, "Coaching exercise not found.");

    public static IReadOnlyList<ProgrammeWeek> Weeks { get; } = Enumerable.Range(1, 12).Select(n => new ProgrammeWeek(n,
        n <= 2 ? "Structure, pace and pauses" : n <= 4 ? "Real stories and personal ownership" : n <= 6 ? "CV defence and technical depth" : n <= 8 ? "Architecture and trade-offs" : n <= 10 ? "Full mock interview practice" : "Pressure, ambiguity and recovery",
        n <= 2 ? ["Practise one point-first update", "Retell one answer with shorter sentences", "Review what text cannot tell you about pace"] :
        n <= 4 ? ["Strengthen one real Story Bank entry", "Separate your action from the team's", "Practise a 30/60-second retelling"] :
        n <= 6 ? ["Defend one saved CV claim", "Answer two technical questions", "Verify one weak technical explanation"] :
        n <= 8 ? ["Explain requirements before architecture", "Defend one trade-off", "Describe failure and recovery"] :
        n <= 10 ? ["Practise recruiter, manager and technical prompts", "Review the session report", "Repeat the weakest answer"] :
        ["Practise an ambiguous prompt", "Recover after losing your structure", "Review a difficult follow-up"])).ToList().AsReadOnly();
}
