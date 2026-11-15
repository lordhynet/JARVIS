using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Domains;

public sealed record DomainDescriptor(string Key, string Instructions, IReadOnlyList<ActivityType> Activities,
    IReadOnlyList<string> Workflows, IReadOnlyList<string> Capabilities);
public interface IDomainCatalog
{
    IReadOnlyList<string> Keys { get; }
    DomainDescriptor Get(string key);
    DomainDescriptor ForActivity(ActivityType activity);
}

// Trusted code modules supply instructions. Future user-authored manifests remain untrusted context data.
public sealed class DomainCatalog : IDomainCatalog
{
    public static IReadOnlyList<DomainDescriptor> BuiltIns { get; } = Array.AsReadOnly(new DomainDescriptor[] {
        new("general", "Help the user with their task and ask a focused question when needed.", [ActivityType.General], [], ["core_context"]),
        new("algorithms", "Coach algorithms Socratically. Ask about constraints, then hints, complexity and edge cases before full solutions.", [ActivityType.LeetCode], [], ["core_practice_start"]),
        new("system-design", "Coach system design: requirements, estimates, API, data model, tradeoffs and failure recovery.", [ActivityType.SystemDesign], [], ["core_practice_start"]),
        new("interview-coaching", "Conduct a mock interview one question at a time. Give actionable feedback after the answer.", [ActivityType.MockInterview], ["interview-preparation", "interview-brief", "practice-plan"], ["core_coaching"]),
        new("languages", "Practice French at the user's level. Offer a short correction and one follow-up question.", [ActivityType.French], [], ["core_recall"]),
        new("social-intelligence", "Coach clear communication. Help organize the user's point and rehearse a concise version.", [ActivityType.Communication], ["daily-coaching"], ["core_coaching"]),
        new("daily-planning", "Help plan the day. Only describe live calendar or transport facts if the user supplies verified data.", [ActivityType.Briefing], ["personal-plan"], ["core_context"]),
        new("reflection", "Help reflect on the day and choose a small next step for tomorrow. Keep the tone calm.", [ActivityType.WindDown], [], ["core_context"])
    }.Select(Freeze).ToArray());
    private readonly IReadOnlyDictionary<string, DomainDescriptor> domains;
    private readonly IReadOnlyDictionary<ActivityType, DomainDescriptor> activities;
    public DomainCatalog(ICapabilityCatalog capabilities, IWorkflowCatalog workflows, IEnumerable<DomainDescriptor>? extensions = null)
    {
        var entries = new Dictionary<string, DomainDescriptor>(StringComparer.Ordinal); var aliases = new Dictionary<ActivityType, DomainDescriptor>();
        foreach (var descriptor in BuiltIns.Concat(extensions ?? [])) {
            Guard.Require(Regex.IsMatch(descriptor.Key, "^[a-z][a-z0-9-]{0,99}$") && !string.IsNullOrWhiteSpace(descriptor.Instructions) &&
                descriptor.Instructions.Length <= 4000 && descriptor.Activities.All(Enum.IsDefined), 400, "Invalid domain descriptor.");
            foreach (var capability in descriptor.Capabilities) capabilities.Get(capability);
            foreach (var workflow in descriptor.Workflows) {
                if (workflows.LegacyKeys.Contains(workflow)) workflows.GetLegacy(workflow);
                else workflows.Get(workflow);
            }
            var frozen = Freeze(descriptor);
            Guard.Require(entries.TryAdd(frozen.Key, frozen), 409, "Duplicate domain key.");
            foreach (var activity in frozen.Activities) Guard.Require(aliases.TryAdd(activity, frozen), 409, "Activity alias is already registered.");
        }
        domains = new ReadOnlyDictionary<string, DomainDescriptor>(entries); activities = new ReadOnlyDictionary<ActivityType, DomainDescriptor>(aliases);
    }
    private static DomainDescriptor Freeze(DomainDescriptor d) => d with { Activities = Array.AsReadOnly(d.Activities.ToArray()), Workflows = Array.AsReadOnly(d.Workflows.ToArray()), Capabilities = Array.AsReadOnly(d.Capabilities.ToArray()) };
    public IReadOnlyList<string> Keys => domains.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    public DomainDescriptor Get(string key) => domains.TryGetValue(key, out var descriptor) ? descriptor : throw new DomainException(404, "Domain is not installed.");
    public DomainDescriptor ForActivity(ActivityType activity) => activities.GetValueOrDefault(activity) ?? Get("general");
}
