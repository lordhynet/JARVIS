using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Voice;

internal sealed class SeriousVoiceUnavailableException(DomainException error)
    : Exception($"Serious voice is unavailable. {error.Message}", error);

internal sealed class SeriousVoiceConfirmationRequired(VoiceModeEstimate estimate) : Exception
{
    public VoiceModeEstimate Estimate { get; } = estimate;
}

internal sealed partial class RealtimeVoiceSession
{
    private ModeOffer? modeOffer;

    private sealed record ModeOffer(Guid Id, decimal MaximumGbpForTenMinutes);

    private Task HandleSpokenModeAsync(string captured)
    {
        // This path runs only after Core saves a distinct user audio transcript from this window.
        var command = captured.Trim().TrimEnd('.', '!', '?').TrimEnd().Replace('’', '\'').ToLowerInvariant();
        return command switch
        {
            "let's do a proper interview" or "serious mode" => OfferSeriousModeAsync(),
            "back to normal" or "everyday mode" => SelectModeAsync(new(VoiceMode.Everyday)),
            _ => Task.CompletedTask
        };
    }

    private Task HandleModeRequestAsync(JsonElement message) =>
        message.TryGetProperty("mode", out var requested) && requested.ValueKind == JsonValueKind.String
            ? requested.GetString() switch
            {
                nameof(VoiceMode.Serious) => OfferSeriousModeAsync(),
                nameof(VoiceMode.Everyday) => SelectModeAsync(new(VoiceMode.Everyday)),
                _ => RejectModeAsync("Choose Everyday or Serious voice.")
            }
            : RejectModeAsync("Choose Everyday or Serious voice.");

    private async Task OfferSeriousModeAsync()
    {
        if (!await CurrentAudioAsync(timeout.Token)) return;
        var estimate = ai.VoiceOption(VoiceMode.Serious, window);
        if (!estimate.PricingReady || estimate.MaximumGbpForTenMinutes is not decimal maximum)
        {
            Interlocked.Exchange(ref modeOffer, null);
            await RejectModeAsync(estimate.Detail);
            return;
        }

        var offer = new ModeOffer(Guid.NewGuid(), maximum);
        Interlocked.Exchange(ref modeOffer, offer);
        await SendClientAsync(new { type = "voice_mode_offer", window = number, offerId = offer.Id,
            mode = VoiceMode.Serious.ToString(), maximumGbpForTenMinutes = maximum, dailyCapGbp = estimate.DailyCapGbp });
    }

    private async Task ConfirmModeOfferAsync(JsonElement message)
    {
        var offer = Volatile.Read(ref modeOffer);
        if (offer is null || !message.TryGetProperty("offerId", out var value) || value.ValueKind != JsonValueKind.String ||
            !value.TryGetGuid(out var id) || id != offer.Id || Interlocked.CompareExchange(ref modeOffer, null, offer) != offer)
        {
            await RejectModeAsync("Request a current Serious voice estimate before confirming.");
            return;
        }

        var current = ai.VoiceOption(VoiceMode.Serious, window);
        if (!current.PricingReady || current.MaximumGbpForTenMinutes > offer.MaximumGbpForTenMinutes)
        {
            await RejectModeAsync(current.PricingReady
                ? "The Serious voice estimate has increased. Request a new estimate."
                : current.Detail);
            return;
        }

        await SelectModeAsync(new(VoiceMode.Serious, offer.MaximumGbpForTenMinutes));
    }

    private async Task SelectModeAsync(VoiceStreamMode selection)
    {
        if (!await CurrentAudioAsync(timeout.Token)) return;
        if (selection.Mode == VoiceMode.Everyday) Interlocked.Exchange(ref modeOffer, null);
        client.ModeSelection = selection;
        await SendClientAsync(new { type = "voice_mode_queued", window = number, mode = selection.Mode.ToString() });
    }

    private Task RejectModeAsync(string detail) => SendClientAsync(new { type = "voice_mode_rejected", window = number, detail });
}
