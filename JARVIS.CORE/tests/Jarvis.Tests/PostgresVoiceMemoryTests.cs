using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Voice_memory_survives_a_restart_and_retention_removes_derived_voice_text()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var voiceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-45);
        const string expiredVoice = "This old transcript must be removed from memory.";
        const string retainedTyped = "This earlier typed detail may remain.";
        string persisted;
        using (var original = new PostgresApiFactory(database.ConnectionString))
        {
            var sessions = original.Services.GetRequiredService<ISessionStore>();
            var runtime = original.Services.GetRequiredService<IRuntimeStore>();
            var recent = Enumerable.Range(1, 20).Select(i =>
                new ChatMessage(Guid.NewGuid(), ChatRoles.User, $"Typed turn {i}", now));
            var messages = new List<ChatMessage>
            {
                new(voiceId, ChatRoles.User, expiredVoice, old, true),
                new(Guid.NewGuid(), ChatRoles.Assistant, retainedTyped, old)
            };
            messages.AddRange(recent);
            await sessions.AddAsync(new(sessionId, ActivityType.General, "Voice memory fixture", SessionStatus.Active,
                null, null, now, now, [], Messages: messages));
            await runtime.UpdateAsync(s => { s.VoiceTurns.Add(voiceId, new(voiceId, sessionId, ChatRoles.User, expiredVoice, null, old)); return true; });
            var voice = original.Services.GetRequiredService<VoiceTranscriptService>();
            var newTurn = Guid.NewGuid();
            await voice.RecordAsync(sessionId, newTurn, ChatRoles.Assistant, "Captured turn", null, default);
            var saved = await sessions.GetAsync(sessionId);
            persisted = JsonSerializer.Serialize(saved!.VoiceSummary);
            Assert.Contains(expiredVoice, persisted);
            Assert.Contains(retainedTyped, persisted);
        }

        using var restarted = new PostgresApiFactory(database.ConnectionString);
        var store = restarted.Services.GetRequiredService<ISessionStore>();
        Assert.Equal(persisted, JsonSerializer.Serialize((await store.GetAsync(sessionId))!.VoiceSummary));
        var privacy = restarted.Services.GetRequiredService<IPrivacyStore>();
        var policy = new RetentionPolicy();
        var preview = await privacy.PreviewAsync(policy, now);
        Assert.Equal(1, preview.Eligible.VoiceTurns);
        await privacy.RetainAsync(new(Guid.NewGuid(), now, preview.PolicyFingerprint, PrivacyData.RetentionPhrase), policy, now);
        var retained = await store.GetAsync(sessionId);
        Assert.Null(retained!.VoiceSummary);
        Assert.DoesNotContain(retained.Messages!, turn => turn.RequestId == voiceId);
        var rebuilt = await store.UpdateAsync(sessionId, VoiceConversationMemory.RefreshSummary);
        var summary = JsonSerializer.Serialize(rebuilt!.VoiceSummary);
        Assert.DoesNotContain(expiredVoice, summary);
        Assert.Contains(retainedTyped, summary);
    }
}
