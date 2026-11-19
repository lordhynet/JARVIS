using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class WakePreparationRaceTests
{
    [Fact]
    public async Task Preparation_remains_exclusive_while_the_first_ticket_admission_is_waiting()
    {
        using var factory = new ProviderFactory(_ => throw new InvalidOperationException("Wake admission must not call a provider."));
        var services = factory.Services;
        var devices = services.GetRequiredService<DeviceService>();
        foreach (var id in new[] { "admission-first", "admission-second" }) {
            await devices.RegisterAsync(new(id, "Admission fixture", DeviceKind.AndroidCompanion, new(true, true, false, false, [CompanionVoiceProtocol.AudioOnly])));
            await devices.VerifyAsync(id);
        }
        var runtime = services.GetRequiredService<IRuntimeStore>();
        var sessions = services.GetRequiredService<ISessionReader>();
        var delayed = new FirstTicketBarrier(services.GetRequiredService<ICoreTransaction>());
        var ownership = new AudioOwnership(runtime, sessions, devices, services.GetRequiredService<TimeProvider>(), delayed);
        var arbiter = new WakeCandidateArbiter(services.GetRequiredService<CompanionVoiceService>(), ownership, runtime, services.GetRequiredService<TimeProvider>());
        var first = arbiter.ClaimAsync(new(Guid.NewGuid(), "admission-first", true, false), default);
        await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<WakeCandidateResponse>? second = null;
        try {
            // The memory services complete synchronously until they reach the held preparation
            // semaphore. The barrier is before ticket creation, so there is no lease yet.
            second = arbiter.ClaimAsync(new(Guid.NewGuid(), "admission-second", true, false), default);
            Assert.False(second.IsCompleted);
            Assert.Null(await runtime.ReadAsync(s => s.AudioLease));
            Assert.Single(await sessions.ListPreparationAsync(default));
        } finally { delayed.Release.TrySetResult(); }
        var winner = await first;
        Assert.NotNull(second);
        var loser = await Assert.ThrowsAsync<DomainException>(async () => await second!);
        Assert.Equal(409, loser.StatusCode);
        Assert.Single(await sessions.ListPreparationAsync(default));
        Assert.Equal(winner.Voice.Lease, await runtime.ReadAsync(s => s.AudioLease));
        Assert.Empty(await runtime.ReadAsync(s => s.UsageReservations));
    }

    private sealed class FirstTicketBarrier(ICoreTransaction inner) : ICoreTransaction
    {
        private int tickets;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<T> ExecuteAsync<T>(Func<ICoreTransactionWrites, Task<T>> operation, CancellationToken ct = default)
        {
            if (typeof(T) == typeof(VoiceTicket) && Interlocked.Increment(ref tickets) == 1) {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return await inner.ExecuteAsync(operation, ct);
        }
    }
}
