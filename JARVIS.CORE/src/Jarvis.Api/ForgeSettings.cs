using System.Globalization;
using Jarvis.Orchestrator.Forge;

namespace Jarvis.Api;

public sealed class ForgeSettings(IConfiguration configuration) : IForgeAvailability
{
    public bool Enabled => configuration.GetValue("Forge:Enabled", false);

    // Owner-set sub-allowance inside the global GBP ceiling. Missing or malformed means no paid Forge work,
    // because UsageBudget refuses Forge-attributed reservations without a positive allowance.
    public static decimal? MonthlyAllowanceGbp(IConfiguration configuration) =>
        decimal.TryParse(configuration[ConfigKeys.ForgeMonthlyAllowanceGbp], NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var allowance) ? allowance : null;
}
