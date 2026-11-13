namespace Jarvis.Api.Endpoints;

public static class RuntimeEndpoints
{
    public static void MapRuntimeEndpoints(this WebApplication app)
    {
        app.MapDeviceEndpoints();
        app.MapLearningEndpoints();
        app.MapToolEndpoints();
        app.MapIntegrationEndpoints();
    }
}
