using Jarvis.Orchestrator.Forge;

namespace Jarvis.Tests;

public sealed class ForgeLanguageProfileTests
{
    [Fact]
    public void Starter_catalogue_is_exactly_the_three_canonical_locales_with_correction_after_each_turn()
    {
        Assert.Equal(["en-GB", "fr-FR", "es-ES"], ForgeLanguageProfiles.All.Select(p => p.Locale));
        Assert.All(ForgeLanguageProfiles.All, profile =>
        {
            Assert.Equal(ForgeCorrectionTiming.AfterEachTurn, profile.DefaultCorrection);
            Assert.Equal(profile.Locale[..2], profile.Language);
            Assert.Contains(profile.Locale[3..], profile.AcceptedRegions);
            Assert.Equal(profile.AcceptedRegions.Count, profile.AcceptedRegions.Distinct().Count());
        });
    }

    [Theory]
    [InlineData("fr-FR", true)]
    [InlineData("fr", false)]
    [InlineData("fr-fr", false)]
    [InlineData("fr-CA", false)]
    [InlineData("de-DE", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_exact_canonical_starter_locales_are_accepted(string? locale, bool expected)
    {
        Assert.Equal(expected, ForgeLanguageProfiles.IsStarter(locale));
        if (!expected && locale is not null) Assert.Throws<ArgumentOutOfRangeException>(() => ForgeLanguageProfiles.Get(locale));
    }

    [Fact]
    public void Review_identities_are_separate_per_locale_and_deterministic()
    {
        var ids = ForgeLanguageProfiles.All.Select(p => ForgeLanguageProfiles.ReviewItemId(p.Locale, "vocab:pain")).ToArray();
        Assert.Equal(3, ids.Distinct().Count());
        Assert.Equal(ids[1], ForgeLanguageProfiles.ReviewItemId("fr-FR", "vocab:pain"));
        Assert.NotEqual(ids[1], ForgeLanguageProfiles.ReviewItemId("fr-FR", "vocab:Pain"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" padded")]
    public void Review_identity_requires_a_trimmed_item_key(string key) =>
        Assert.Throws<ArgumentException>(() => ForgeLanguageProfiles.ReviewItemId("fr-FR", key));

    [Fact]
    public void Review_identity_refuses_an_unsupported_locale() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ForgeLanguageProfiles.ReviewItemId("it-IT", "vocab:pane"));

    [Fact]
    public void Turn_gate_uses_the_catalogue_as_its_only_locale_authority()
    {
        var activity = new ForgeSpeechActivity(3, 2, ForgeSpeechActivity.PcmActiveMethod);
        foreach (var profile in ForgeLanguageProfiles.All)
            Assert.True(ForgeTurnQualityGate.Assess(profile.Locale, activity, "word word word", .9, profile.Language).Scorable);
        Assert.Throws<ArgumentOutOfRangeException>(() => ForgeTurnQualityGate.Assess("it-IT", activity, "ciao a tutti", .9));
        Assert.Null(typeof(ForgeTurnQualityGate).GetField("StarterLocales"));
    }
}
