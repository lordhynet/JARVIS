using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task ForgeCapacity_payload_protected_storage_restart_preserves_unknown_constraints_and_paused_owner_edits()
    {
        await using var database = await TestDatabase.CreateAsync(); var programme = ForgeProtectedStorageFixture.Programme();
        var records = new List<ForgeProtectedRecord>(); var now = ForgeProtectedStorageFixture.At;
        var protector = new ProtectedRecordProtector(() => new() { ActiveKeyId = "synthetic-capacity",
            Keys = new Dictionary<string, string> { ["synthetic-capacity"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()) } });
        using (var original = new PostgresApiFactory(database.ConnectionString, manualAgents: true))
        using (var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()))) {
            var transaction = host.Services.GetRequiredService<ICoreTransaction>();
            await transaction.ExecuteAsync(async writes => {
                await writes.SaveForgeProgrammeAsync(programme);
                foreach (var known in new[] { false, true }) {
                    var payload = new ForgeCapacityPayload(1, programme.Start, programme.TimeZone, 120, known ? 30 : null, known ? [] : null, true, null, null, null)
                        .Prepare(programme, 1, now).Payload;
                    var record = new ForgeProtectedRecord(ForgeProtectedKind.Capacity, programme.OwnerScope, programme.Id, Guid.NewGuid(), 0, 1, Guid.NewGuid(), 0,
                        now, new(1, "", [], [], []), new(1, "", []));
                    var canonical = JsonSerializer.SerializeToUtf8Bytes(new { record.RequestId, record.ExpectedRevision, Payload = payload });
                    try { record = record with { Envelope = ForgeProtectedJson.Protect(protector, record.ValueContext, payload), RetryProof = protector.CreateRetryProof(record.RetryContext, canonical) }; }
                    finally { CryptographicOperations.ZeroMemory(canonical); }
                    await writes.AppendForgeProtectedRecordAsync(record); records.Add(record);
                }
                return true;
            });
            Assert.Equal(503, (await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<IPrivacyStore>().ExportAsync(now))).StatusCode);
        }
        using var restartedBase = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var restarted = restartedBase.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        foreach (var record in records) {
            var restored = await restarted.Services.GetRequiredService<ICoreTransaction>().ExecuteAsync(w =>
                w.ReadForgeProtectedRequestAsync(record.Kind, record.OwnerScope, record.ProgrammeId, record.RequestId));
            var prepared = ForgeProtectedJson.Read<ForgeCapacityPayload>(protector, restored!.ValueContext, restored.Envelope).Prepare(programme, 1, now);
            if (prepared.Payload.BufferMinutes is null) { Assert.Null(prepared.Planning); Assert.Null(prepared.Payload.FixedBlocks); }
            else { Assert.NotNull(prepared.Planning); Assert.Equal(0, prepared.Planning.AllocatableMinutes); Assert.False(prepared.Planning.CanAllocate); }
        }
    }
}
