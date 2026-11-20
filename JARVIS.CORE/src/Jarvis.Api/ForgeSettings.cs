using Jarvis.Orchestrator.Forge;

namespace Jarvis.Api;

public sealed class ForgeSettings(IConfiguration configuration) : IForgeAvailability
{
    public bool Enabled => configuration.GetValue("Forge:Enabled", false);
}
