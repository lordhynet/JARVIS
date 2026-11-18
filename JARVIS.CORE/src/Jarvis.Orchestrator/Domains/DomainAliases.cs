using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Domains;

// Compatibility aliases are code-owned. They do not rewrite immutable manifests or persisted enum values.
public static class DomainAliases
{
    public static string LearningKey(LearningDomain domain) => domain switch {
        LearningDomain.LeetCode => "algorithms", LearningDomain.SystemDesign => "system-design", LearningDomain.French => "languages",
        LearningDomain.InterviewStory => "interview-coaching", LearningDomain.Communication => "social-intelligence",
        LearningDomain.CSharpDotNet => "csharp-dotnet", LearningDomain.DistributedSystems => "distributed-systems", LearningDomain.Databases => "databases",
        LearningDomain.ApiSecurity => "api-security", LearningDomain.ProductionDevOps => "production-devops", LearningDomain.PaymentsFintech => "payments-fintech",
        _ => throw new DomainException(400, "Unknown legacy learning domain.")
    };
    public static string ActivityKey(ActivityType activity) => DomainCatalog.BuiltIns.SingleOrDefault(d => d.Activities.Contains(activity))?.Key
        ?? throw new DomainException(400, "Unknown legacy activity.");
}
