using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Forge;

/// <summary>One Forge division from Addendum V2 §3, expressed as an existing Domain Registry template.</summary>
public sealed record ForgeDivision(string Code, string Key, string Description, AgentRole Role, IReadOnlyList<string> Prompts,
    bool Protected = false, bool Optional = false);

/// <summary>
/// PF-02: the sixteen Forge divisions are code-owned Domain Registry <b>templates</b>, not a second domain system.
/// They are installed only as drafts through the existing registry, so each one still needs the owner's normal
/// approval/activation. Template mode with the installed template capability means no paid AI and no external action.
/// Protected divisions (privacy classes) never share personal context; RELATIONSHIPS is optional and off by default.
/// Prompts ask for practice, never for private values such as amounts, symptoms or partners.
/// </summary>
public static class ForgeDivisions
{
    public const string KeyPrefix = "forge.";
    public const string DraftActor = "forge-divisions-v1";

    public static IReadOnlyList<ForgeDivision> All { get; } = Array.AsReadOnly<ForgeDivision>([
        new("TECH", "forge.tech", "Engineering curriculum: C# basics, OOP, data structures, SQL, HTTP, testing, concurrency, runtime internals, distributed systems, SRE and architecture, evidenced by independent code, tests, benchmarks and ADRs.",
            AgentRole.TechnicalTrainer, ["Explain one concept from today's topic in your own words.", "Describe how you would test it.", "Choose one small implementation step."]),
        new("CAREER", "forge.career", "Vacancy-specific interview preparation, CV defence, behavioural stories, coding and system design, applications and interview debriefs.",
            AgentRole.InterviewCoach, ["Tell one behavioural story in under two minutes.", "Name the strongest evidence in it.", "Choose one thing to tighten next time."]),
        new("BUSINESS", "forge.business", "Customer discovery, sales, pricing, validation and distribution, evidenced by real customer conversations.",
            AgentRole.CareerPlanner, ["State the customer problem you are testing.", "Name one question for your next customer conversation.", "Choose one small validation step."]),
        new("WEALTH", "forge.wealth", "Personal money skills: tuition and debt planning, cash flow, emergency savings, tax and investing education, with a monthly owner-led review.",
            AgentRole.PersonalPlanner, ["Name one money concept you want to understand better (no amounts needed).", "Explain it in your own words.", "Choose one small learning step."], Protected: true),
        new("BODY", "forge.body", "Supervised swimming, progressive calisthenics and recreational tennis, tracked as sessions and safe skill milestones.",
            AgentRole.PersonalPlanner, ["Name the skill you practised.", "Describe one thing that went well.", "Choose one safe next step."]),
        new("HEALTH", "forge.health", "Sleep, recovery, meals and preventive care as owner-entered trends. Not medical advice or diagnosis.",
            AgentRole.PersonalPlanner, ["Name one routine you want to keep this week.", "Describe what helps you keep it.", "Choose one small adjustment."], Protected: true),
        new("MIND", "forge.mind", "Assertiveness, emotional regulation, independent decisions and deliberate practice through small real-world challenges.",
            AgentRole.CommunicationCoach, ["Describe one small challenge you chose.", "Say what you did and what you learned.", "Choose the next challenge."]),
        new("VOICE", "forge.voice", "English mastery, French, Spanish, public speaking, storytelling and negotiation.",
            AgentRole.CommunicationCoach, ["Say three sentences about your day in your target language.", "Retell a short story clearly.", "Choose one phrase to practise."]),
        new("PRESENCE", "forge.presence", "Wardrobe, grooming, natural walking, posture and etiquette in short daily practice.",
            AgentRole.CommunicationCoach, ["Name one presence habit you practised.", "Describe how it felt.", "Choose one small adjustment."]),
        new("SOCIAL", "forge.social", "Small talk, listening, humour, networking, hosting and conversation through real interactions.",
            AgentRole.CommunicationCoach, ["Practise a short opening line.", "Name one good follow-up question.", "Choose one interaction to try."]),
        new("RELATIONSHIPS", "forge.relationships", "Optional reflective review of boundaries and honest communication. No compatibility scores, quotas or required disclosures.",
            AgentRole.CommunicationCoach, ["Reflect on one conversation you handled well.", "Name one boundary you kept.", "Choose one communication habit to practise."], Protected: true, Optional: true),
        new("INTELLECT", "forge.intellect", "Reading across economics, history, philosophy, arts, science and world affairs.",
            AgentRole.Research, ["Summarise one idea from your reading.", "Connect it to something you know.", "Choose what to read next."]),
        new("INFLUENCE", "forge.influence", "Technical articles, demos, talks and open-source contributions as meaningful public outputs.",
            AgentRole.CommunicationCoach, ["State the one idea your next piece will teach.", "Name your audience.", "Choose the smallest publishable step."]),
        new("MENTORSHIP", "forge.mentorship", "Finding technical, business and career mentors, and mentoring junior engineers with consented feedback.",
            AgentRole.CareerPlanner, ["Name one question for a mentor.", "Describe one thing you could teach someone.", "Choose one outreach or session step."]),
        new("GLOBAL", "forge.global", "Global Talent and permanent-residence evidence preparation and English and French exam readiness. Owner-led; never automatic filing.",
            AgentRole.CareerPlanner, ["Name one piece of evidence you are building.", "Describe what it demonstrates.", "Choose one small preparation step."], Protected: true),
        new("LIFESTYLE", "forge.lifestyle", "Cooking, first aid, driving if needed, cultural experiences and recreation as rotating missions.",
            AgentRole.PersonalPlanner, ["Name this week's mission.", "Describe one step you took.", "Choose the next step."])
    ]);

    public static bool IsDivisionKey(string? key) => key is not null && All.Any(d => d.Key == key);

    public static string RequireKey(string key)
    {
        Guard.Require(IsDivisionKey(key), 400, "Unknown Forge division.");
        return key;
    }

    /// <summary>The validated existing-schema manifest for one division (Template mode, installed template capability).</summary>
    public static DomainManifest Manifest(ForgeDivision division) => DomainPolicy.Validate(new(1, division.Key, "Forge " + division.Code,
        division.Description, [], [division.Role], [new("core_template_practice")], division.Prompts.ToArray(), DomainExecutionMode.Template,
        UsesPersonalContext: false));
}
