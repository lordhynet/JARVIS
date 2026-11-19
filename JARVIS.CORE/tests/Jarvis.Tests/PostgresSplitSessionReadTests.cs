using System.Data.Common;
using Jarvis.Contracts;
using Jarvis.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    private sealed class SplitReadFactory(DbContextOptions<SessionDbContext> options) : IDbContextFactory<SessionDbContext>
    {
        public SessionDbContext CreateDbContext() => new(options);
    }
    private sealed class SplitReadGate : DbCommandInterceptor
    {
        public TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int armed = 1;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("\"Checkpoints\"", StringComparison.Ordinal) && command.CommandText.Contains("JOIN", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref armed, 0) == 1) {
                Arrived.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            return result;
        }
    }
    [PostgresFact]
    public Task Split_session_get_keeps_one_snapshot_during_a_concurrent_checkpoint_save() => VerifySplitRead(false);
    [PostgresFact]
    public Task Split_session_list_keeps_one_snapshot_during_a_concurrent_checkpoint_save() => VerifySplitRead(true);
    private static async Task VerifySplitRead(bool list)
    {
        await using var database = await TestDatabase.CreateAsync(); using var host = new PostgresApiFactory(database.ConnectionString);
        _ = host.Services; var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var plain = new DbContextOptionsBuilder<SessionDbContext>().UseNpgsql(database.ConnectionString).Options;
        await using var writer = new SessionDbContext(plain);
        writer.Sessions.Add(new() { Id = id, Activity = ActivityType.MockInterview, Title = "Split fixture", Status = SessionStatus.Active,
            CurrentTask = "before", CreatedAt = now, UpdatedAt = now, Checkpoints = [new() { Id = Guid.NewGuid(), SessionId = id, Sequence = 1, Summary = "before", CreatedAt = now }] });
        await writer.SaveChangesAsync();
        var gate = new SplitReadGate(); var options = new DbContextOptionsBuilder<SessionDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(gate).Options;
        var store = new PostgresSessionStore(new SplitReadFactory(options));
        async Task<ConversationSession?> Read() => list ? (await store.ListAsync()).Single(s => s.Id == id) : await store.GetAsync(id);
        var reading = Read();
        try {
            await gate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var row = await writer.Sessions.Include(s => s.Checkpoints).SingleAsync(s => s.Id == id);
            row.CurrentTask = "after"; row.Checkpoints.Single().Summary = "after"; await writer.SaveChangesAsync();
        } finally { gate.Release.TrySetResult(); }
        var result = await reading.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("before", result!.CurrentTask); Assert.Equal("before", Assert.Single(result.Checkpoints).Summary);
        var fresh = await store.GetAsync(id); Assert.Equal("after", fresh!.CurrentTask); Assert.Equal("after", Assert.Single(fresh.Checkpoints).Summary);
    }
}
