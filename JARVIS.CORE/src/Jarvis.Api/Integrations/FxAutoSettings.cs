using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Integrations;

internal sealed record FxAutoSettings(bool Enabled, int RefreshHours, decimal MarginPercent, int MaxPublicationAgeDays)
{
    internal const int DefaultRefreshHours = 6;
    internal const string ClientName = "ecb-fx";

    internal static bool IsEnabled(IConfigurationSection section) =>
        bool.TryParse(section["Enabled"], out var enabled) && enabled;

    internal static FxAutoSettings Read(IConfigurationSection section)
    {
        string[] allowed = ["Enabled", ConfigKeys.BudgetFxRefreshHours, ConfigKeys.BudgetFxMarginPercent, ConfigKeys.BudgetFxMaxPublicationAgeDays];
        Guard.Require(section.GetChildren().All(child => allowed.Contains(child.Key, StringComparer.OrdinalIgnoreCase)),
            503, "Unsupported automatic FX settings.");
        try
        {
            var settings = new FxAutoSettings(section.GetValue("Enabled", false),
                section.GetValue(ConfigKeys.BudgetFxRefreshHours, DefaultRefreshHours),
                section.GetValue(ConfigKeys.BudgetFxMarginPercent, 2m),
                section.GetValue(ConfigKeys.BudgetFxMaxPublicationAgeDays, 4));
            Guard.Require(settings.RefreshHours is >= 1 and <= 23 && settings.MarginPercent >= 0 && settings.MaxPublicationAgeDays >= 0,
                503, "Automatic FX needs a refresh interval from 1 to 23 hours and nonnegative margin and publication age.");
            return settings;
        }
        catch (InvalidOperationException)
        {
            throw new DomainException(503, "Automatic FX settings are malformed.");
        }
    }
}
