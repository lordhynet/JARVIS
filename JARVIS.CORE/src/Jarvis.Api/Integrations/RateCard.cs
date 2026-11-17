using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Api.Integrations;

public sealed record ReviewedTextRates(ModelRoute Route, decimal SearchUsdPerCall, BudgetLimits Limits)
{
    [JsonIgnore] public string ApiKey { get; init; } = "";
    public override string ToString() => Route.ToString();
    public decimal Cost(int input, int output) {
        Guard.Require(input >= 0 && output >= 0, 502, "Provider returned invalid token usage.");
        return (input * Route.InputUsdPerMillion + output * Route.OutputUsdPerMillion) / 1_000_000m;
    }
}

public sealed record ReviewedVoiceRates(VoiceMode Mode, ModelRoute Route, RealtimePricing Pricing);

/// <summary>Single reviewed text-rate reader, including the existing Anthropic configuration layout.</summary>
public sealed class RateCard(IConfiguration config, TimeProvider clock)
{
    public DateOnly ReviewedDate(string prefix, string? model)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        Guard.Require(!string.IsNullOrWhiteSpace(model) && model == config[prefix + ConfigKeys.RateCardModel], 503, "The selected model must match its reviewed rate card.");
        Guard.Require(DateOnly.TryParse(config[prefix + ConfigKeys.RateCardDate], out var date) && date <= today && date >= today.AddDays(-90), 503, "AI rate card requires review.");
        return date;
    }
    public (string Model, RealtimePricing Pricing) ReadRealtime()
    {
        var reviewed = ReadRealtime(VoiceMode.Everyday);
        return (reviewed.Route.Model, reviewed.Pricing);
    }

    public ReviewedVoiceRates ReadRealtime(VoiceMode mode)
    {
        Guard.Require(Enum.IsDefined(mode), 400, "Unknown realtime voice mode.");
        var reload = config.GetReloadToken();
        var named = ConfigKeys.VoiceModes + mode + ":";
        var enabledValue = config[named + ConfigKeys.Enabled];
        var enabled = false;
        Guard.Require(string.IsNullOrWhiteSpace(enabledValue) || bool.TryParse(enabledValue, out enabled), 503, "Invalid voice mode enable setting.");
        Guard.Require(mode != VoiceMode.Serious || enabled, 503, "Serious voice is disabled. Enable and configure its reviewed rate card first.");
        var prefix = enabled ? named : ConfigKeys.VoiceProfile;
        var model = config[prefix + ConfigKeys.Model];
        Guard.Require(!enabled || model == (mode == VoiceMode.Serious ? ConfigKeys.SeriousRealtimeModel : ConfigKeys.EverydayRealtimeModel),
            503, "The realtime model does not match the selected voice mode.");
        var date = ReviewedDate(prefix, model);
        Guard.Require((config[prefix + ConfigKeys.Provider] ?? ProviderIds.OpenAi) == ProviderIds.OpenAi && !string.IsNullOrWhiteSpace(config[ConfigKeys.OpenAiApiKey]), 503, "Configure the installed OpenAI realtime route.");
        RealtimePricing pricing;
        try { pricing = RealtimePricing.Read(config, prefix); }
        catch (InvalidOperationException) { throw new DomainException(503, "The realtime voice rate card is malformed."); }
        var revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { mode, model, date, pricing })));
        Guard.Require(!reload.HasChanged, 409, "Realtime settings changed during route selection.");
        return new(mode, new(ProviderIds.OpenAi, model!, "Voice:" + mode, VoiceModePolicy.Version, revision, date,
            pricing.MaxInput, pricing.MaxOutput, RealtimeWindowCost.OutputTokenLimit, TextCapability.Conversation,
            ReasoningDataClass.Personal, CloudConsent: true), pricing);
    }
    public (string Model, decimal PerMinute, string ApiKey) ReadTranscription()
    {
        Guard.Require(!string.IsNullOrWhiteSpace(config[ConfigKeys.OpenAiApiKey]), 503, "Configure OpenAI:ApiKey to enable transcription.");
        var reload = config.GetReloadToken(); var model = config[ConfigKeys.TranscriptionProfile + ConfigKeys.Model];
        ReviewedDate(ConfigKeys.TranscriptionProfile, model);
        var rate = config.GetValue<decimal>(ConfigKeys.TranscriptionProfile + "UsdPerMinute"); var key = config[ConfigKeys.OpenAiApiKey];
        Guard.Require(rate > 0 && model == config[ConfigKeys.OpenAiTranscriptionModel] && !string.IsNullOrWhiteSpace(key), 503, "Configure matching transcription model, reviewed rate and credential.");
        Guard.Require(!reload.HasChanged, 409, "Transcription settings changed during route selection.");
        return (model!, rate, key!);
    }
    public ReviewedTextRates ReadText(string profile, TextCapability capability, ReasoningDataClass dataClass, int? requestedLimit = null)
    {
        var reload = config.GetReloadToken(); var legacy = profile == "LegacyInterview";
        var prefix = legacy ? ConfigKeys.PaidAiPrefix : ConfigKeys.AiProfilePrefix(profile);
        var provider = legacy ? ProviderIds.Anthropic : config[prefix + ConfigKeys.Provider] ?? ProviderIds.OpenAi;
        Guard.Require(System.Text.RegularExpressions.Regex.IsMatch(provider, "^[a-z][a-z0-9-]{0,39}$"), 503, "Invalid provider route identifier.");
        var credentialSetting = provider switch { ProviderIds.OpenAi => ConfigKeys.OpenAiApiKey, ProviderIds.Anthropic => ConfigKeys.AnthropicApiKey, _ => $"Providers:{provider}:ApiKey" };
        var secret = config[credentialSetting];
        Guard.Require(!string.IsNullOrWhiteSpace(secret), 503, $"Configure {credentialSetting} to enable AI.");
        var model = legacy ? config[ConfigKeys.AnthropicModel] : config[prefix + ConfigKeys.Model];
        Guard.Require(!legacy || config.GetValue<bool>(ConfigKeys.PaidAiEnabled), 503, "Paid interview briefs are disabled.");
        Guard.Require(!string.IsNullOrWhiteSpace(model) && model == config[prefix + ConfigKeys.RateCardModel], 503, "The selected model must match its reviewed rate card.");
        if (profile == "Chat" && provider == ProviderIds.OpenAi) Guard.Require(model == config[ConfigKeys.OpenAiModel], 503, "Chat model and its priced profile must match.");
        var date = ReviewedDate(prefix, model);
        var input = config.GetValue<decimal>(prefix + "InputUsdPerMillion"); var output = config.GetValue<decimal>(prefix + "OutputUsdPerMillion");
        var cap = config.GetValue(prefix + ConfigKeys.MaxOutputTokens, profile == "Chat" ? 1500 : 2048);
        Guard.Require(input > 0 && output > 0 && cap is > 0 and <= 32768 && requestedLimit is null or > 0, 503, "Configure positive text rates and a valid output limit.");
        Guard.Require(requestedLimit is null || requestedLimit <= cap, 400, "Requested output exceeds the reviewed profile cap.");
        cap = requestedLimit ?? cap;
        var allowed = config.GetSection(prefix + "AllowedDataClasses").Get<string[]>();
        if (provider != ProviderIds.OpenAi && !legacy) Guard.Require(allowed is { Length: > 0 }, 503, "Explicitly approve data classes before enabling a new provider destination.");
        Guard.Require((allowed ?? (profile == "Research" ? ["Public"] : new[] { "Public", "Personal" })).Contains(dataClass.ToString(), StringComparer.Ordinal), 403, "That data class is not permitted on this route.");
        Guard.Require(capability != TextCapability.GroundedPublicSearch || profile == "Research" && dataClass == ReasoningDataClass.Public,
            403, "This route cannot provide the required public grounding. No fallback was attempted.");
        var search = capability == TextCapability.GroundedPublicSearch ? config.GetValue<decimal>(prefix + "SearchUsdPerCall") : 0;
        Guard.Require(capability != TextCapability.GroundedPublicSearch || search > 0, 503, "Grounded search requires its reviewed per-call price.");
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { provider, model, date, input, output, cap, search, allowed }))));
        var route = new ModelRoute(provider, model!, profile, "text-routing-v1", revision, date, input, output, cap, capability, dataClass);
        var limits = new BudgetLimits(config.GetValue(ConfigKeys.DailyLimitUsd, 2m), config.GetValue(ConfigKeys.MonthlyLimitUsd, 50m),
            config.GetValue(ConfigKeys.BackgroundLimitUsd, 15m), config.GetValue(ConfigKeys.VoiceLimitUsd, 30m), config.GetValue(ConfigKeys.InteractiveLimitUsd, 5m));
        Guard.Require(!reload.HasChanged, 409, "Provider settings changed while selecting a route. Retry before dispatch.");
        return new(route, search, limits) { ApiKey = secret! };
    }
}
