using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;

namespace Jarvis.Api.Security;

public static class ProtectedRecordsRegistration
{
    public static IServiceCollection AddProtectedRecords(this IServiceCollection services)
    {
        // Empty/missing configuration grants no key; historical reads resolve their retained key ID.
        services.AddSingleton<IProtectedRecordProtector>(provider => {
            var config = provider.GetRequiredService<IConfiguration>();
            return new ProtectedRecordProtector(() => config.GetSection(ConfigKeys.ProtectedRecords).Get<ProtectedRecordKeyOptions>() ?? new());
        });
        return services;
    }
}
