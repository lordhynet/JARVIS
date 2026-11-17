using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Api.Integrations;

public sealed class BudgetSettings(IConfiguration config, TimeProvider? clock = null, FxQuoteCache? cache = null) : IBudgetPolicySource
{
    public GbpBudgetPolicy Read() => ReadPolicy(cache?.Current);
    public GbpBudgetPolicy Read(RuntimeState state) => ReadPolicy(state.FxRefresh);

    private GbpBudgetPolicy ReadPolicy(FxRefreshState? automatic)
    {
        try {
            var section = config.GetSection(ConfigKeys.Budget); var fx = section.GetSection(ConfigKeys.BudgetFx);
            string[] allowed = ["Enabled", "ScopeId", "Environment", "Version", ConfigKeys.BudgetFx, ConfigKeys.BudgetFxAuto, "TaxMultiplier", "SafetyMultiplier"];
            string[] fxAllowed = ["Source", "UsdToGbp", "ObservedAt", "ExpiresAt", "PublishedOn"];
            Guard.Require(section.GetChildren().All(c => allowed.Contains(c.Key, StringComparer.OrdinalIgnoreCase)) &&
                fx.GetChildren().All(c => fxAllowed.Contains(c.Key, StringComparer.OrdinalIgnoreCase)), 503,
                "Unsupported budget settings. Development £10 and production £100/£200 authorization limits cannot be overridden in configuration.");
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            var configured = ConfiguredQuote(fx);
            var manualIgnored = fx.Exists() && (configured is null || configured.ObservedAt > now ||
                !GbpBudgetRules.IsCurrentQuote(configured, configured.ObservedAt));
            if (manualIgnored) configured = null;
            var autoEnabled = FxAutoSettings.IsEnabled(section.GetSection(ConfigKeys.BudgetFxAuto));
            var useAutomatic = !GbpBudgetRules.IsCurrentQuote(configured, now) && autoEnabled &&
                GbpBudgetRules.IsCurrentQuote(automatic?.Quote, now);
            var quote = useAutomatic ? automatic!.Quote : configured;
            var status = new BudgetFxStatus(autoEnabled, useAutomatic, automatic?.LastAttemptAt, automatic?.LastError,
                automatic?.Quote, manualIgnored);
            return new(section.GetValue<bool>("Enabled"), section["ScopeId"] ?? "", section["Environment"] ?? "",
                section["Version"] ?? "1", quote, section.GetValue<decimal?>("TaxMultiplier"), section.GetValue<decimal?>("SafetyMultiplier"), status);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException)
        { throw new DomainException(503, "GBP budget configuration is malformed; paid dispatch is disabled."); }
    }

    private static BudgetFxQuote? ConfiguredQuote(IConfigurationSection fx)
    {
        if (!fx.Exists()) return null;
        try
        {
            return new(fx["Source"] ?? "", fx.GetValue<decimal>("UsdToGbp"), fx.GetValue<DateTimeOffset>("ObservedAt"),
                fx.GetValue<DateTimeOffset>("ExpiresAt"), fx.GetValue<DateOnly?>("PublishedOn"));
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
