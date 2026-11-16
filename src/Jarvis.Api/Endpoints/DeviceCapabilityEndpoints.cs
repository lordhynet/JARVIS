using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Endpoints;

public static class DeviceCapabilityEndpoints
{
    public static void MapDeviceCapabilityEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet(CoreRoutes.DeviceCapabilities, (IDeviceCapabilityCatalog catalog) => catalog.Installed)
            .WithTags("Device capabilities").RequireAuthorization(Jarvis.Api.Security.CorePolicies.OwnerOrCompanion);
}
