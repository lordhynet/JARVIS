using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Api.Integrations;

public sealed class BudgetSettings(IConfiguration config) : IBudgetPolicySource
{
    public GbpBudgetPolicy Read()
    {
        try {
            var section = config.GetSection("Budget"); var fx = section.GetSection("Fx");
            string[] allowed = ["Enabled", "ScopeId", "Environment", "Version", "Fx", "TaxMultiplier", "SafetyMultiplier", ConfigKeys.VoiceSeriousDailyGbpField];
            string[] fxAllowed = ["Source", "UsdToGbp", "ObservedAt", "ExpiresAt"];
            Guard.Require(section.GetChildren().All(c => allowed.Contains(c.Key, StringComparer.OrdinalIgnoreCase)) &&
                fx.GetChildren().All(c => fxAllowed.Contains(c.Key, StringComparer.OrdinalIgnoreCase)), 503,
                "Unsupported budget settings. Development £10 and production £100/£200 authorization limits cannot be overridden in configuration.");
            var quote = fx.Exists() ? new BudgetFxQuote(fx["Source"] ?? "", fx.GetValue<decimal>("UsdToGbp"),
                fx.GetValue<DateTimeOffset>("ObservedAt"), fx.GetValue<DateTimeOffset>("ExpiresAt")) : null;
            return new(section.GetValue<bool>("Enabled"), section["ScopeId"] ?? "", section["Environment"] ?? "",
                section["Version"] ?? "1", quote, section.GetValue<decimal?>("TaxMultiplier"), section.GetValue<decimal?>("SafetyMultiplier"));
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException)
        { throw new DomainException(503, "GBP budget configuration is malformed; paid dispatch is disabled."); }
    }
}
