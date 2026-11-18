using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Speech_timing_and_delivery_survive_restart_and_are_erased_with_their_source()
    {
        await using var database = await TestDatabase.CreateAsync(); Guid sessionId; Guid turnId;
        using (var first = new PostgresApiFactory(database.ConnectionString)) {
            sessionId = (await first.Services.GetRequiredService<SessionService>().CreateAsync(new(ActivityType.Communication, "Measured fixture", null))).Id;
            await VoiceToolFixture.OpenAsync(first.Services, sessionId); turnId = Guid.NewGuid();
            await first.Services.GetRequiredService<VoiceTranscriptService>().RecordAsync(sessionId, turnId, ChatRoles.User,
                "I chose queues and we tested recovery", 3, default, timing: new(3, 1, SpeechDelivery.PcmQuietMethod));
        }
        using var restarted = new PostgresApiFactory(database.ConnectionString); using var owner = restarted.AuthorisedClient();
        var report = (await owner.GetFromJsonAsync<SpeechDeliverySession[]>("/api/coaching/delivery", Json))!;
        Assert.Equal(turnId, Assert.Single(Assert.Single(report).Trend).TurnId); Assert.Equal(.333, report[0].PauseRatio);
        var status = (await owner.GetFromJsonAsync<PrivacyStatus>("/api/privacy/status", Json))!;
        await DomainRegistryTests.Post<PrivacyStatus>(owner, "/api/privacy/erase", new EraseCoreRequest(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase));
        Assert.Empty((await restarted.Services.GetRequiredService<IPrivacyReader>().ExportAsync(DateTimeOffset.UtcNow)).VoiceTurns);
    }
}
