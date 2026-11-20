using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Jarvis.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task ForgeCheckIn_protected_restart_keeps_owner_unknowns_and_canonical_references_without_copying_history()
    {
        await using var database = await TestDatabase.CreateAsync(); var programme = ForgeProtectedStorageFixture.Programme(); var now = ForgeProtectedStorageFixture.At;
        var protector = new ProtectedRecordProtector(() => new() { ActiveKeyId = "synthetic-checkin",
            Keys = new Dictionary<string, string> { ["synthetic-checkin"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()) } });
        ForgeProtectedRecord saved; LearningItem card;
        using (var original = new PostgresApiFactory(database.ConnectionString, manualAgents: true))
        using (var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()))) {
            card = await host.Services.GetRequiredService<LearningService>().CreateAsync(new(LearningDomain.CSharpDotNet, "Canonical prompt", "Canonical answer remains here"), default);
            var payload = new ForgeCheckInPayload(1, "engineering", [
                new("unknown", ForgeCheckInObservationMode.OwnerReported, null, null, null, "count", null, null, null),
                new("zero", ForgeCheckInObservationMode.OwnerReported, 0, null, null, "count", null, null, null),
                new("canonical", ForgeCheckInObservationMode.CanonicalReference, null, null, null, null, new("learning", card.Id, AgentRuntime.LearningVersion(card)), null, null)
            ], true, null, "private-owner-baseline-fixture").Normalize(programme, now);
            var record = new ForgeProtectedRecord(ForgeProtectedKind.CheckIn, programme.OwnerScope, programme.Id, Guid.NewGuid(), 0, 1, Guid.NewGuid(), 0, now, new(1, "", [], [], []), new(1, "", []));
            var canonical = JsonSerializer.SerializeToUtf8Bytes(new { record.RequestId, record.ExpectedRevision, Payload = payload });
            try { saved = record with { Envelope = ForgeProtectedJson.Protect(protector, record.ValueContext, payload), RetryProof = protector.CreateRetryProof(record.RetryContext, canonical) }; }
            finally { CryptographicOperations.ZeroMemory(canonical); }
            await host.Services.GetRequiredService<ICoreTransaction>().ExecuteAsync(async w => { await w.SaveForgeProgrammeAsync(programme); await w.AppendForgeProtectedRecordAsync(saved); return true; });
            var root = await host.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => JsonSerializer.Serialize(s)); Assert.DoesNotContain("private-owner-baseline-fixture", root);
            Assert.Equal(503, (await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<IPrivacyStore>().ExportAsync(now))).StatusCode);
        }
        using var restartedBase = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var restarted = restartedBase.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var restored = await restarted.Services.GetRequiredService<ICoreTransaction>().ExecuteAsync(w => w.ReadForgeProtectedRequestAsync(saved.Kind, saved.OwnerScope, saved.ProgrammeId, saved.RequestId));
        var result = ForgeProtectedJson.Read<ForgeCheckInPayload>(protector, restored!.ValueContext, restored.Envelope).Normalize(programme, now);
        var values = result.Observations!;
        Assert.Null(values.Single(x => x.MetricKey == "unknown").NumericValue); Assert.Equal(0, values.Single(x => x.MetricKey == "zero").NumericValue);
        var reference = values.Single(x => x.MetricKey == "canonical"); Assert.Null(reference.TextValue); Assert.Equal(card.Id, reference.Source!.Id);
        Assert.Equal(card, await restarted.Services.GetRequiredService<IRuntimeReader>().ReadLearningItemAsync(card.Id));
    }
}
