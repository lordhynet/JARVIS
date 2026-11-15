using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Domains;

public sealed record WorkflowDescriptor(string Key, IReadOnlyList<AgentRole> Roles, bool RequiresCloud,
    bool UsesPersonalContext = false, bool Deterministic = false, string? DomainContext = null);
public interface IWorkflowCatalog
{
    IReadOnlyList<string> Keys { get; }
    WorkflowDescriptor Get(string key);
}
public sealed class WorkflowCatalog : IWorkflowCatalog
{
    public static IReadOnlyList<WorkflowDescriptor> BuiltIns { get; } = Array.AsReadOnly(new WorkflowDescriptor[] {
        new("interview-preparation", [AgentRole.InterviewCoach, AgentRole.TechnicalTrainer, AgentRole.Review], false, true),
        new("daily-coaching", [AgentRole.CommunicationCoach, AgentRole.TechnicalTrainer, AgentRole.Review], false, true),
        new("research-review", [AgentRole.Research, AgentRole.Review], true),
        new("career-plan", [AgentRole.CareerPlanner, AgentRole.Review], true),
        new("personal-plan", [AgentRole.PersonalPlanner, AgentRole.Review], true),
        new("practice-reminder", [AgentRole.TechnicalTrainer], false, true, true)
    }.Select(Freeze).ToArray());
    private readonly ConcurrentDictionary<string, WorkflowDescriptor> entries = new(StringComparer.Ordinal);
    public WorkflowCatalog(IEnumerable<WorkflowDescriptor>? extensions = null)
    {
        foreach (var descriptor in BuiltIns.Concat(extensions ?? [])) Register(descriptor);
    }
    public IReadOnlyList<string> Keys => entries.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    public WorkflowDescriptor Get(string key) => entries.TryGetValue(key, out var descriptor) ? descriptor : throw new DomainException(400, "Unknown or disabled workflow.");
    public void Register(WorkflowDescriptor descriptor)
    {
        Guard.Require(descriptor is not null && !string.IsNullOrWhiteSpace(descriptor.Key) && descriptor.Key.Length <= 100 &&
            Regex.IsMatch(descriptor.Key, "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant) && descriptor.Roles is not null && descriptor.Roles.Count is > 0 and <= 6 &&
            descriptor.Roles.Distinct().Count() == descriptor.Roles.Count && descriptor.Roles.All(Enum.IsDefined), 400, "Invalid workflow descriptor.");
        Guard.Require(descriptor.DomainContext is null || descriptor.DomainContext.Length <= 4000, 400, "Domain context exceeded its limit.");
        Guard.Require(entries.TryAdd(descriptor.Key, Freeze(descriptor)), 409, "Workflow is already registered.");
    }
    public bool Remove(string key) => !BuiltIns.Any(x => x.Key == key) && entries.TryRemove(key, out _);
    private static WorkflowDescriptor Freeze(WorkflowDescriptor descriptor) => descriptor with { Roles = Array.AsReadOnly(descriptor.Roles.ToArray()) };
    public static WorkflowPolicySnapshot Snapshot(WorkflowDescriptor descriptor) => new(
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(descriptor)))),
        descriptor.RequiresCloud, descriptor.UsesPersonalContext, descriptor.Deterministic, descriptor.DomainContext);
}
