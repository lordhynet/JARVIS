using System.Text.Json;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Integrations;

public sealed record RealtimePricing(decimal AudioInput, decimal TextInput, decimal CachedAudioInput,
    decimal CachedTextInput, decimal AudioOutput, decimal TextOutput, decimal TranscriptionPerMinute, string TranscriptionModel)
{
    public decimal MaxInput => Math.Max(AudioInput, TextInput);
    public decimal MaxOutput => Math.Max(AudioOutput, TextOutput);
    public static RealtimePricing Read(IConfiguration config)
    {
        decimal Rate(string key) => config.GetValue<decimal>("AI:Voice:" + key);
        var p = new RealtimePricing(Rate("AudioInputUsdPerMillion"), Rate("TextInputUsdPerMillion"),
            Rate("CachedAudioInputUsdPerMillion"), Rate("CachedTextInputUsdPerMillion"), Rate("AudioOutputUsdPerMillion"),
            Rate("TextOutputUsdPerMillion"), Rate("TranscriptionUsdPerMinute"), config["AI:Voice:TranscriptionModel"] ?? "");
        Guard.Require(p.AudioInput >= p.TextInput && p.TextInput > 0 && p.AudioOutput >= p.TextOutput && p.TextOutput > 0 &&
            p.CachedAudioInput > 0 && p.CachedAudioInput <= p.AudioInput && p.CachedTextInput > 0 && p.CachedTextInput <= p.TextInput &&
            p.TranscriptionPerMinute > 0 && !string.IsNullOrWhiteSpace(p.TranscriptionModel) &&
            config["AI:Voice:TranscriptionRateCardModel"] == p.TranscriptionModel, 503,
            "Configure explicit realtime audio/text/cached rates and model-bound input transcription pricing.");
        return p;
    }
    public decimal Cost(JsonElement usage)
    {
        var input = usage.GetProperty("input_tokens").GetInt32(); var output = usage.GetProperty("output_tokens").GetInt32();
        Guard.Require(input >= 0 && output >= 0, 502, "Invalid realtime usage totals.");
        // Missing/inconsistent breakdowns retain the highest applicable uncached rate.
        decimal result = input * MaxInput + output * MaxOutput;
        if (usage.TryGetProperty("input_token_details", out var inputDetails))
            Guard.Require(!inputDetails.TryGetProperty("image_tokens", out var imageUsage) || imageUsage.GetInt32() == 0,
                502, "Unexpected image usage requires reconciliation.");
        if (usage.TryGetProperty("input_token_details", out var details) &&
            details.TryGetProperty("audio_tokens", out var audio) && details.TryGetProperty("text_tokens", out var text))
        {
            var a = audio.GetInt32(); var t = text.GetInt32();
            Guard.Require(!details.TryGetProperty("image_tokens", out var images) || images.GetInt32() == 0, 502, "Unexpected image usage requires reconciliation.");
            if (a >= 0 && t >= 0 && (long)a + t == input) {
                result = a * AudioInput + t * TextInput + output * MaxOutput;
                if (details.TryGetProperty("cached_tokens", out var totalCached) && details.TryGetProperty("cached_tokens_details", out var cached) &&
                    cached.TryGetProperty("audio_tokens", out var ca) && cached.TryGetProperty("text_tokens", out var ct)) {
                    var ac = ca.GetInt32(); var tc = ct.GetInt32();
                    if (ac >= 0 && ac <= a && tc >= 0 && tc <= t && (long)ac + tc == totalCached.GetInt32() &&
                        (!cached.TryGetProperty("image_tokens", out var ci) || ci.GetInt32() == 0))
                        result -= ac * (AudioInput - CachedAudioInput) + tc * (TextInput - CachedTextInput);
                }
            }
        }
        if (usage.TryGetProperty("output_token_details", out var outs) && outs.TryGetProperty("audio_tokens", out var oa) && outs.TryGetProperty("text_tokens", out var ot))
        {
            var a = oa.GetInt32(); var t = ot.GetInt32();
            if (a >= 0 && t >= 0 && (long)a + t == output) result += a * AudioOutput + t * TextOutput - output * MaxOutput;
        }
        return result / 1_000_000m;
    }
    public decimal Transcription(double seconds) => (decimal)Math.Ceiling(seconds) / 60m * TranscriptionPerMinute * 2;
}
