using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

// Direct tool fixtures bypass the socket, but still model its canonical audio lease.
internal static class VoiceToolFixture
{
    public static Task OpenAsync(IServiceProvider services, Guid session) => services.GetRequiredService<IRuntimeStore>().UpdateAsync(state => {
        if (state.AudioLease?.SessionId != session)
            state.AudioLease = new(session, "tool-fixture", (state.AudioLease?.Fence ?? 0) + 1, DateTimeOffset.UtcNow.AddMinutes(5));
        return true;
    });
}
