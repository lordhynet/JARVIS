using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Workflows;

public static class AgentCapabilityIds
{
    public const string Template = "core_template_practice", Reasoning = "core_agent_reasoning", Research = "core_public_research";
    public const string AgentProfile = "Agent", ResearchProfile = "Research";
    public const string EmptySchema = """{"type":"object","properties":{},"additionalProperties":false}""";
    public const string TemplateSchema = """{"type":"object","properties":{"artifact":{"type":"string","maxLength":16000}},"required":["artifact"],"additionalProperties":false}""";
    public const string ReasoningSchema = """{"type":"object","properties":{"request":{"type":"string","maxLength":180000}},"required":["request"],"additionalProperties":false}""";
    public const string SpecialistInputSchema = """{"type":"object","properties":{"input":{"type":"string","maxLength":4000}},"required":["input"],"additionalProperties":false}""";
    public static IReadOnlyList<CapabilityDescriptor> BuiltIns { get; } = Array.AsReadOnly(new CapabilityDescriptor[] {
        new(Template, "Deliver an existing authored or deterministic practice artifact.", CapabilityRisk.ReadOnly, InputSchemaJson: TemplateSchema, DataClasses: [ReasoningDataClass.Public, ReasoningDataClass.Personal]),
        new(Reasoning, "Create a scoped specialist artifact through the existing reasoning provider.", CapabilityRisk.ReadOnly, InputSchemaJson: ReasoningSchema, DataClasses: [ReasoningDataClass.Public, ReasoningDataClass.Personal], RequiresBudget: true),
        new(Research, "Research a scoped goal through the existing grounded-search provider.", CapabilityRisk.ReadOnly, InputSchemaJson: ReasoningSchema, DataClasses: [ReasoningDataClass.Public, ReasoningDataClass.Personal], RequiresBudget: true)
    });
}

public sealed record AgentSpecialistDescriptor(AgentRole Role, IReadOnlyList<string> AllowedCapabilities, string ProviderProfile,
    IReadOnlyList<ReasoningDataClass> DataClasses, string InputSchemaJson, string OutputSchemaJson,
    int MaximumCalls = 4, int MaximumInputBytes = 400000, int MaximumOutputBytes = 64000, decimal MaximumReservationUsd = 2m);

public sealed class AgentSpecialistCatalog
{
    private readonly IReadOnlyDictionary<AgentRole, AgentSpecialistDescriptor> entries;
    public AgentSpecialistCatalog(IEnumerable<AgentSpecialistDescriptor>? installed = null)
    {
        entries = (installed ?? Enum.GetValues<AgentRole>().Select(role => new AgentSpecialistDescriptor(role,
            [AgentCapabilityIds.Template, role == AgentRole.Research ? AgentCapabilityIds.Research : AgentCapabilityIds.Reasoning,
                ..(role is AgentRole.PersonalPlanner or AgentRole.CareerPlanner ? BuiltInDeviceCapabilities.All.Concat(WindowsDeviceCapabilities.All).Select(c => c.Id) : [])],
            role == AgentRole.Research ? AgentCapabilityIds.ResearchProfile : AgentCapabilityIds.AgentProfile,
            [ReasoningDataClass.Public, ReasoningDataClass.Personal], AgentCapabilityIds.SpecialistInputSchema, AgentCapabilityIds.TemplateSchema)))
            .Select(Freeze).ToDictionary(x => x.Role);
        Guard.Require(Enum.GetValues<AgentRole>().All(entries.ContainsKey), 400, "Every installed agent role needs a specialist descriptor.");
    }
    private static AgentSpecialistDescriptor Freeze(AgentSpecialistDescriptor descriptor)
    {
        Guard.Require(Enum.IsDefined(descriptor.Role) && descriptor.AllowedCapabilities.Count is > 0 and <= 24 &&
            descriptor.DataClasses.Count > 0 && descriptor.DataClasses.All(Enum.IsDefined) && descriptor.MaximumCalls is > 0 and <= 100 &&
            descriptor.MaximumInputBytes is > 0 and <= 400000 && descriptor.MaximumOutputBytes is > 0 and <= 64000 &&
            descriptor.MaximumReservationUsd is > 0 and <= 10 && !string.IsNullOrWhiteSpace(descriptor.ProviderProfile), 400, "Invalid specialist policy.");
        _ = DeviceJsonSchema.ReadCapability(descriptor.InputSchemaJson); _ = DeviceJsonSchema.ReadCapability(descriptor.OutputSchemaJson);
        return descriptor with { AllowedCapabilities = Array.AsReadOnly(descriptor.AllowedCapabilities.Distinct().ToArray()), DataClasses = Array.AsReadOnly(descriptor.DataClasses.Distinct().ToArray()) };
    }
    public IReadOnlyList<AgentSpecialistDescriptor> Installed => entries.Values.OrderBy(x => x.Role).ToArray();
    public AgentSpecialistDescriptor Get(AgentRole role) => entries.TryGetValue(role, out var descriptor) ? descriptor : throw new DomainException(403, "Specialist role is not installed.");
}
