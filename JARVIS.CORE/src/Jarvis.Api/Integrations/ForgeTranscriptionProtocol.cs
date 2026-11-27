using System.Text.Json;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Integrations;

// What a Forge speaking turn needs from transcription: text plus whatever explicit quality and language evidence the
// configured model actually returns. Missing evidence stays null so the turn gate refuses rather than assumes.
// The result is bound to the model it was requested from, so evidence admission cannot drift from model capability.
public sealed record ForgeTurnTranscript(string Text, double? Confidence, string? DetectedLanguage, string ConfidenceMethod, string Model)
{
    public bool QualityEvidenceAvailable => ForgeTranscriptionProtocol.ReturnsConfidence(Model);
}

// Pure request/response shaping for OpenAI /v1/audio/transcriptions (documented 27 Nov 2026). No dispatch, reservation
// or caller here. Evidence by model family:
// - gpt-transcribe: takes `languages`, returns detected `languages`, returns NO logprobs, so confidence is always null.
// - gpt-4o-transcribe family: takes `language`; `include[]=logprobs` with response_format=json returns token logprobs.
//   This family is announced to shut down on 26 Feb 2027, inside the Forge programme.
// - other models (e.g. whisper-1): language hint only; no confidence.
public static class ForgeTranscriptionProtocol
{
    public const string LogprobConfidenceMethod = "openai-mean-token-logprob-v1";
    public const string NoConfidenceMethod = "none";
    private static readonly HashSet<string> LogprobModels = new(StringComparer.Ordinal)
        { "gpt-4o-transcribe", "gpt-4o-mini-transcribe", "gpt-4o-mini-transcribe-2025-12-15" };

    public static bool ReturnsConfidence(string model) => LogprobModels.Contains(model);

    // Every Forge turn states its locale; the provider receives that profile's ISO-639-1 language, never a guess.
    public static IReadOnlyList<KeyValuePair<string, string>> Fields(string model, string locale)
    {
        Guard.Require(!string.IsNullOrWhiteSpace(model), 503, "A reviewed transcription model is required.");
        var language = ForgeLanguageProfiles.Get(locale).Language;
        if (model == "gpt-transcribe") return [new("languages[]", language)];
        if (LogprobModels.Contains(model))
            return [new("language", language), new("response_format", "json"), new("include[]", "logprobs")];
        return [new("language", language)];
    }

    // `model` is the reviewed model the request was built for. Confidence is derived only for models whose reviewed
    // contract supplies logprobs; unexpected logprobs from any other or unknown model are ignored, never admitted.
    public static ForgeTurnTranscript Parse(JsonElement result, string model)
    {
        Guard.Require(!string.IsNullOrWhiteSpace(model), 503, "A reviewed transcription model is required.");
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            throw new DomainException(502, "Transcription provider returned no text.");
        var confidence = ReturnsConfidence(model) ? Confidence(result) : null;
        return new(text.GetString()!, confidence, Detected(result), confidence is null ? NoConfidenceMethod : LogprobConfidenceMethod, model);
    }

    // Geometric-mean token probability, exp(mean logprob). Any malformed or out-of-range entry voids the whole signal:
    // partial or repaired evidence must not certify a turn. An uncalibrated proposal, like the gate's thresholds.
    private static double? Confidence(JsonElement result)
    {
        if (!result.TryGetProperty("logprobs", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
            return null;
        double sum = 0; var count = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("logprob", out var value) ||
                value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var logprob) || !double.IsFinite(logprob) || logprob > 0)
                return null;
            sum += logprob; count++;
        }
        return Math.Exp(sum / count);
    }

    // gpt-transcribe reports `languages: [{ "code": "fr" }, ...]`. Only an unambiguous single detected language is used;
    // several detected languages are not a mismatch signal on their own, so they are reported as unknown.
    private static string? Detected(JsonElement result)
    {
        if (!result.TryGetProperty("languages", out var languages) || languages.ValueKind != JsonValueKind.Array || languages.GetArrayLength() != 1)
            return null;
        var only = languages[0];
        return only.ValueKind == JsonValueKind.Object && only.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String &&
            code.GetString() is { } value && WellFormedCode(value) ? Normalize(value) : null;
    }

    // ISO 639-2/3 forms of the starter languages become their ISO 639-1 primary so "fra" is not a false mismatch against fr-FR.
    // Any other three-letter code names a different language and is kept, so genuine mismatches remain mismatches.
    private static readonly IReadOnlyDictionary<string, string> StarterThreeLetter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["fra"] = "fr", ["fre"] = "fr", ["eng"] = "en", ["spa"] = "es" };
    private static string Normalize(string value)
    {
        var dash = value.IndexOf('-');
        var primary = dash < 0 ? value : value[..dash];
        return StarterThreeLetter.TryGetValue(primary, out var two) ? two + (dash < 0 ? "" : value[dash..]) : value;
    }

    // Documented code forms: ISO 639-1 ("fr"), ISO 639-3 ("eng") and a regional subtag ("zh-cn"). Anything else
    // (whitespace, punctuation, control characters, full names like "english") is not an observation; it stays unknown
    // instead of becoming a false language mismatch.
    internal static bool WellFormedCode(string value)
    {
        var parts = value.Split('-');
        return parts.Length is 1 or 2 && parts[0].Length is 2 or 3 && parts[0].All(char.IsAsciiLetter) &&
            (parts.Length == 1 || parts[1].Length is >= 2 and <= 4 && parts[1].All(char.IsAsciiLetterOrDigit));
    }
}
