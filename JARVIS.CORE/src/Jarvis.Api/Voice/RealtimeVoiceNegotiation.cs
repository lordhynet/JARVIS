using System.Buffers.Binary;
using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Voice;

internal sealed class RealtimeVoiceNegotiation(RealtimeVoiceClient client, AudioLease lease, AudioOwnership ownership,
    IRuntimeReader runtime, OpenAiProvider provider, VoiceTranscriptService transcripts)
{
    private const int CaptureSeconds = 3;
    private const int CaptureBytes = CaptureSeconds * VoiceWindowLimits.BytesPerSecond;

    public async Task<bool> ConfirmPriceAsync(int window, VoiceModeEstimate estimate, CancellationToken ct)
    {
        using var consent = CancellationTokenSource.CreateLinkedTokenSource(ct);
        consent.CancelAfter(TimeSpan.FromSeconds(25));
        var token = consent.Token;
        Guard.Require(await CurrentAsync(token), 409, "Voice is no longer available on this device.");
        var offer = Guid.NewGuid();
        await client.SendAsync(new { type = "voice_mode_offer", window, offerId = offer, starting = true,
            mode = nameof(VoiceMode.Serious), maximumGbpForTenMinutes = estimate.MaximumGbpForTenMinutes,
            dailyCapGbp = estimate.DailyCapGbp }, token);
        while (await ReadAsync(token) is { } message)
        {
            if (!Matches(message, "offerId", offer)) continue;
            if (message.GetProperty("type").ValueEquals("confirm_voice_mode"))
            {
                await ownership.SetVoiceModeAsync(lease, new(VoiceMode.Serious, estimate.MaximumGbpForTenMinutes, FollowActivity: true), token);
                return true;
            }
            if (message.GetProperty("type").ValueEquals("voice_mode") && message.TryGetProperty("mode", out var mode) &&
                mode.ValueKind == JsonValueKind.String && mode.ValueEquals(nameof(VoiceMode.Everyday)))
            {
                await ownership.SetVoiceModeAsync(lease, new(VoiceMode.Everyday), token);
                return true;
            }
        }
        return false;
    }

    public async Task<bool> ConfirmEverydayAsync(string reason, CancellationToken ct)
    {
        using var consent = CancellationTokenSource.CreateLinkedTokenSource(ct);
        consent.CancelAfter(TimeSpan.FromSeconds(20));
        var token = consent.Token;
        Guard.Require(await CurrentAsync(token), 409, "Voice is no longer available on this device.");
        var capture = Guid.NewGuid();
        await client.SendAsync(new { type = "voice_fallback_offer", captureId = capture, detail = reason,
            prompt = "Serious voice is unavailable. Say yes to continue in Everyday voice." }, token);
        bool played = false;
        using var audio = new MemoryStream(CaptureBytes);
        while (await ReadAsync(token) is { } message)
        {
            if (!Matches(message, "captureId", capture)) continue;
            var type = message.GetProperty("type").GetString();
            if (type == "voice_fallback_cancelled") return false;
            if (type == "voice_fallback_played" && !played)
            {
                Guard.Require(await CurrentAsync(token), 409, "Audio ownership changed before your answer.");
                played = true;
                await client.SendAsync(new { type = "voice_fallback_capture", captureId = capture }, token);
            }
            if (!played || type != "voice_fallback_audio") continue;
            var pcm = Convert.FromBase64String(message.GetProperty("audio").GetString()!);
            Guard.Require(pcm.Length is > 0 and <= 24000 && pcm.Length % 2 == 0, 400, "Invalid voice confirmation audio.");
            await audio.WriteAsync(pcm.AsMemory(0, Math.Min(pcm.Length, CaptureBytes - (int)audio.Length)), token);
            if (audio.Length == CaptureBytes) break;
        }

        if (audio.Length != CaptureBytes || !HasSpeech(audio.GetBuffer())) return false;
        await client.SendAsync(new { type = "voice_fallback_verifying", captureId = capture }, token);
        var text = await VerifyAsync(audio.ToArray(), token);
        if (text is null || !VoiceModePolicy.ConfirmsEveryday(text) || !await CurrentAsync(token)) return false;
        await transcripts.RecordAsync(lease.SessionId, capture, ChatRoles.User, text, null, token);
        await ownership.SetVoiceModeAsync(lease, new(VoiceMode.Everyday), token, fallbackUtterance: capture);
        return true;
    }

    private async Task<string?> VerifyAsync(byte[] pcm, CancellationToken ct)
    {
        using var verification = CancellationTokenSource.CreateLinkedTokenSource(ct);
        verification.CancelAfter(TimeSpan.FromSeconds(7));
        var result = provider.TranscribeVoiceConsentAsync(pcm, lease.SessionId, CurrentAsync, verification.Token);
        try
        {
            while (!result.IsCompleted)
            {
                using var readStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pending = ReadAsync(readStop.Token);
                await Task.WhenAny(result, pending);
                if (!pending.IsCompleted)
                    await readStop.CancelAsync();
                try
                {
                    if (await pending is null) return null;
                }
                catch (OperationCanceledException) when (result.IsCompleted && !ct.IsCancellationRequested) { }
            }
            return await result;
        }
        finally
        {
            await verification.CancelAsync();
            try { await result; } catch (Exception) { /* The awaited result above reports failures. */ }
        }
    }

    private async Task<JsonElement?> ReadAsync(CancellationToken ct)
    {
        while (client.State == System.Net.WebSockets.WebSocketState.Open)
        {
            var message = await client.ReadAsync(ct);
            switch (message.GetProperty("type").GetString())
            {
                case "stop": client.RecordStop(); return null;
                case "heartbeat":
                    var renewed = await ownership.RenewAsync(lease, ct);
                    await client.SendAsync(new { type = "lease", fence = renewed.Fence, leaseSeconds = 15,
                        sequence = message.GetProperty("sequence").GetInt64() }, ct);
                    break;
                default: return message;
            }
        }
        return null;
    }

    private async Task<bool> CurrentAsync(CancellationToken ct) =>
        !client.StopRequested && await ownership.CurrentAsync(lease, ct) && !await runtime.ReadPaidAiPausedAsync(ct);

    private static bool Matches(JsonElement message, string name, Guid expected) =>
        message.TryGetProperty(name, out var id) && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var parsed) && parsed == expected;

    private static bool HasSpeech(byte[] pcm)
    {
        // Avoid sending silence to recognition, which can return a word without enough signal.
        var loud = 0;
        for (var offset = 0; offset < pcm.Length; offset += 2)
            if (Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset))) >= 450 && ++loud >= 240)
                return true;
        return false;
    }
}
