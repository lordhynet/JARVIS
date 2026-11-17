using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Domains;

public static class DomainPolicy
{
    public const string Version = "domain-policy-v1", OwnerSubject = "core-owner";
    private static readonly HashSet<string> ReservedConfiguration = new(StringComparer.OrdinalIgnoreCase) {
        "permissions", "grants", "role", "script", "sql", "command", "code", "instructions", "systemPrompt", "memoryScope", "ownerKey", "apiKey", "provider", "model", "policy", "budget"
    };
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 10,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public static string Slug(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", "-").Trim('-');
    public static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16]);
    public static string WorkflowKey(string key) => "domain-" + key.Replace('.', '-') + "-practice";
    public static DomainManifest Parse(JsonElement json)
    {
        Guard.Require(json.ValueKind == JsonValueKind.Object && Encoding.UTF8.GetByteCount(json.GetRawText()) <= 16000, 400, "Domain manifest must be a bounded JSON object.");
        Guard.Require(json.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == json.EnumerateObject().Count(), 400, "Duplicate manifest properties are forbidden.");
        try { return Validate(json.Deserialize<DomainManifest>(Json) ?? throw new JsonException()); }
        catch (Exception error) when (error is JsonException or NotSupportedException) { throw new DomainException(400, "Domain manifest does not match the installed schema. Unknown permission or code fields are forbidden."); }
    }
    public static DomainManifest Validate(DomainManifest manifest)
    {
        Guard.Require(manifest is not null && manifest.SchemaVersion == 1 && !string.IsNullOrWhiteSpace(manifest.Key) && manifest.Key.Length <= 60 &&
            Regex.IsMatch(manifest.Key, "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$"), 400, "Domain key/schema is invalid.");
        var name = Guard.Text(manifest.DisplayName, "Domain name", 120); var description = Guard.Text(manifest.Description, "Domain description", 1200);
        Guard.Require(manifest.Aliases is { Count: <= 12 } && manifest.Roles is { Count: > 0 and <= 6 } &&
            manifest.Capabilities is { Count: > 0 and <= 24 } && manifest.PracticePrompts is { Count: > 0 and <= 3 } &&
            manifest.Roles.All(Enum.IsDefined) && manifest.Roles.Distinct().Count() == manifest.Roles.Count && Enum.IsDefined(manifest.ExecutionMode),
            400, "Domain roles, capabilities or practice template are invalid.");
        var aliases = manifest.Aliases.Select(a => Guard.Text(a, "Alias", 120)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var capabilities = manifest.Capabilities.Select(c => {
            Guard.Require(c is not null && !string.IsNullOrWhiteSpace(c.Id) && Regex.IsMatch(c.Id, "^[a-z][a-z0-9_.-]{0,99}$") &&
                c.VersionConstraint is { Length: > 0 and <= 30 }, 400, "Invalid capability dependency."); return c;
        }).OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
        Guard.Require(capabilities.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() == capabilities.Length, 400, "Duplicate capability dependencies.");
        var prompts = manifest.PracticePrompts.Select(p => Guard.Text(p, "Practice prompt", 300)).ToArray();
        var configuration = manifest.Configuration ?? new Dictionary<string, string>();
        Guard.Require(configuration.Count <= 8 && configuration.All(p => !ReservedConfiguration.Contains(p.Key) && Regex.IsMatch(p.Key, "^[a-zA-Z][a-zA-Z0-9_-]{0,39}$") && p.Value is { Length: <= 100 }), 400, "Domain configuration must contain small scalar values and cannot declare privileges or executable instructions.");
        Guard.Require(manifest.ParentKey is null || manifest.ParentKey.Length <= 60 && Regex.IsMatch(manifest.ParentKey, "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$") && manifest.ParentKey != manifest.Key, 400, "Invalid parent domain.");
        return manifest with { DisplayName = name, Description = description, Aliases = Array.AsReadOnly(aliases), Roles = Array.AsReadOnly(manifest.Roles.ToArray()),
            Capabilities = Array.AsReadOnly(capabilities), PracticePrompts = Array.AsReadOnly(prompts),
            Configuration = configuration.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) };
    }
    public static string Serialize(DomainManifest manifest) => JsonSerializer.Serialize(Validate(manifest), Json);
    public static string Hash(DomainManifest manifest) => PrivacyData.Hash(Serialize(manifest));
    public static IReadOnlySet<string> Names(DomainManifest manifest) => new[] { manifest.Key, manifest.DisplayName }.Concat(manifest.Aliases).Select(Slug).ToHashSet(StringComparer.Ordinal);
    public static DomainRiskClass Risk(DomainManifest manifest)
    {
        var text = string.Join(" ", new[] { manifest.Key, manifest.DisplayName, manifest.Description }.Concat(manifest.Aliases).Concat((manifest.Configuration ?? new Dictionary<string, string>()).Values));
        if (Regex.IsMatch(text, @"\b(law|legal|court|litigation|solicitor|jurisdiction)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return DomainRiskClass.Law;
        if (Regex.IsMatch(text, @"\b(health|medical|clinical|diagnosis|treatment|medicine)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return DomainRiskClass.Health;
        if (Regex.IsMatch(text, @"\b(finance|financial|investment|trading|stocks|tax|banking)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return DomainRiskClass.Finance;
        return DomainRiskClass.General;
    }
    public static bool CanRaiseRisk(DomainRiskClass inferred, DomainRiskClass approved) =>
        Enum.IsDefined(approved) && (inferred == DomainRiskClass.General || inferred == approved);
    public static IReadOnlyList<string> Dependencies(DomainManifest manifest, ICapabilityCatalog installed, DomainRiskClass? riskOverride = null)
    {
        var gaps = new List<string>(); var risk = riskOverride ?? Risk(manifest);
        foreach (var required in manifest.Capabilities) {
            var actual = installed.Installed.FirstOrDefault(c => c.Key == required.Id);
            if (actual is null) { gaps.Add("Capability is not installed: " + required.Id); continue; }
            if (required.VersionConstraint != actual.Version && required.VersionConstraint != actual.Version.Split('.')[0] + ".x") gaps.Add("Unsupported capability version: " + required.Id);
            if (actual.Risk == CapabilityRisk.ExternalAction) gaps.Add("External actions need a separately installed permission-controlled workflow; manifests cannot expose them automatically: " + required.Id);
            if (risk != DomainRiskClass.General && actual.Risk != CapabilityRisk.ReadOnly) gaps.Add("Sensitive domains are read-only: " + required.Id);
        }
        if (risk != DomainRiskClass.General && manifest.UsesPersonalContext) gaps.Add("Sensitive domains cannot access shared personal coaching context.");
        if (risk == DomainRiskClass.Law && !installed.Installed.Any(c => c.Key == "trusted_legal_sources" && c.Risk == CapabilityRisk.ReadOnly))
            gaps.Add("Trusted legal sources are not installed; the domain must remain a draft.");
        if (risk == DomainRiskClass.Health && !installed.Installed.Any(c => c.Key == "trusted_medical_sources" && c.Risk == CapabilityRisk.ReadOnly))
            gaps.Add("Trusted medical sources are not installed; the domain must remain a draft.");
        return gaps.Distinct(StringComparer.Ordinal).ToArray();
    }
    public static void RequireOwner(BudgetActor actor)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.ManualClient && !string.IsNullOrWhiteSpace(actor.SubjectId) && actor.Scopes.Contains("domain.manage"),
            403, "Domain activation requires a manual owner approval.");
    }
    public static DomainManifest BuiltIn(DomainDescriptor domain) => Validate(new(1, domain.Key,
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(domain.Key.Replace('-', ' ')), domain.Instructions,
        domain.Workflows.ToArray(), [AgentRole.TechnicalTrainer], [new("core_template_practice")],
        ["Describe your learning goal for " + domain.Key.Replace('-', ' ') + ".", "Explain one example in your own words.", "Choose one small next practice step."], DomainExecutionMode.Template));
}
