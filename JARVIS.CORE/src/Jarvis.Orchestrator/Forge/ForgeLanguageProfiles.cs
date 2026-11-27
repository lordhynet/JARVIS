using System.Security.Cryptography;
using System.Text;

namespace Jarvis.Orchestrator.Forge;

// When corrections are given during a language drill (ADR 0025). The stored per-profile owner choice is a later,
// separately reviewed setting; until then every profile uses its default.
public enum ForgeCorrectionTiming { AfterEachTurn = 1, Immediate, EndOfSession }

public sealed record ForgeLanguageProfile(string Locale, string Language, string DisplayName,
    IReadOnlyList<string> AcceptedRegions, ForgeCorrectionTiming DefaultCorrection);

// PF-07: the single authority for Forge practice locales. Code-registered starter profiles only. They are not Domain
// Registry entries, are not installed or activated by this catalogue, and grant no capability or paid work.
public static class ForgeLanguageProfiles
{
    public const string Version = "forge-language-profiles-v1";

    // Accepted regions are listening/accent variants that must not be treated as a different language or an error.
    // They are starting proposals for the owner to configure, not a judgement about which accent is correct.
    public static IReadOnlyList<ForgeLanguageProfile> All { get; } = new List<ForgeLanguageProfile>
    {
        new("en-GB", "en", "English (UK)", ["GB", "IE", "US", "AU", "NG"], ForgeCorrectionTiming.AfterEachTurn),
        new("fr-FR", "fr", "French (France)", ["FR", "BE", "CH", "CA"], ForgeCorrectionTiming.AfterEachTurn),
        new("es-ES", "es", "Spanish (Spain)", ["ES", "MX", "AR", "CO", "US"], ForgeCorrectionTiming.AfterEachTurn)
    }.AsReadOnly();

    private static readonly IReadOnlyDictionary<string, ForgeLanguageProfile> ByLocale =
        All.ToDictionary(profile => profile.Locale, StringComparer.Ordinal);

    // Exact, case-sensitive canonical tags: every turn and checkpoint states its locale explicitly, so no guessing.
    public static bool IsStarter(string? locale) => locale is not null && ByLocale.ContainsKey(locale);

    public static ForgeLanguageProfile Get(string locale) => ByLocale.TryGetValue(locale, out var profile) ? profile :
        throw new ArgumentOutOfRangeException(nameof(locale), "An explicit supported practice locale is required.");

    // Vocabulary and review identities are separate per locale: the same item key in French and Spanish yields different IDs,
    // so switching language never merges or overwrites another language's history. Deterministic for replay.
    public static Guid ReviewItemId(string locale, string itemKey)
    {
        var profile = Get(locale);
        if (string.IsNullOrWhiteSpace(itemKey) || itemKey.Length > 200 || itemKey != itemKey.Trim())
            throw new ArgumentException("A trimmed item key of 1–200 characters is required.", nameof(itemKey));
        return new(SHA256.HashData(Encoding.UTF8.GetBytes($"forge-language-review-v1\n{profile.Locale}\n{itemKey}"))[..16]);
    }
}
