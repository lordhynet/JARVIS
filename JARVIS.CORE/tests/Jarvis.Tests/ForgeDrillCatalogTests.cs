using Jarvis.Orchestrator.Forge;

namespace Jarvis.Tests;

public sealed class ForgeDrillCatalogTests
{
    [Fact]
    public void Every_item_is_well_formed_versioned_and_in_a_starter_locale()
    {
        Assert.NotEmpty(ForgeDrillCatalog.All);
        Assert.All(ForgeDrillCatalog.All, item =>
        {
            Assert.True(ForgeLanguageProfiles.IsStarter(item.Locale));
            Assert.True(ForgeLanguageProfiles.IsStarter(item.InstructionLocale));
            Assert.NotEqual(item.Locale, item.InstructionLocale);
            Assert.Equal(1, item.Version);
            Assert.Contains(item.Level, new[] { "A1", "A2" });
            Assert.False(string.IsNullOrWhiteSpace(item.Instruction));
            Assert.False(string.IsNullOrWhiteSpace(item.ModelAnswer));
            Assert.InRange(item.ModelAnswer.Length, 1, 200);
            Assert.InRange(item.Hints.Count, 1, 4);
            Assert.All(item.Hints, hint => Assert.False(string.IsNullOrWhiteSpace(hint)));
            Assert.Equal(item.Key.Trim(), item.Key);
        });
    }

    [Fact]
    public void Identities_and_keys_are_unique_and_derived_from_the_shared_per_locale_review_identity()
    {
        Assert.Equal(ForgeDrillCatalog.All.Count, ForgeDrillCatalog.All.Select(i => i.Id).Distinct().Count());
        Assert.Equal(ForgeDrillCatalog.All.Count, ForgeDrillCatalog.All.Select(i => i.Key).Distinct().Count());
        Assert.All(ForgeDrillCatalog.All, item => Assert.Equal(ForgeLanguageProfiles.ReviewItemId(item.Locale, "drill:" + item.Key), item.Id));
    }

    [Fact]
    public void Model_answers_are_scorable_text_under_the_shared_gate_word_count()
    {
        // A model answer read aloud at a normal pace must not look like an implausible, noise-invented transcript.
        Assert.All(ForgeDrillCatalog.All, item => Assert.InRange(ForgeTurnQualityGate.Words(item.ModelAnswer), 2, 20));
    }

    [Fact]
    public void Locale_filter_uses_the_profile_authority()
    {
        Assert.Equal(ForgeDrillCatalog.All.Count(i => i.Locale == "fr-FR"), ForgeDrillCatalog.For("fr-FR").Count);
        Assert.Empty(ForgeDrillCatalog.For("es-ES"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ForgeDrillCatalog.For("fr"));
    }

    [Fact]
    public void The_catalogue_is_immutable_through_its_interfaces()
    {
        Assert.Throws<NotSupportedException>(() => ((IList<ForgeDrillItem>)ForgeDrillCatalog.All).Add(ForgeDrillCatalog.All[0]));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)ForgeDrillCatalog.All[0].Hints).Add("x"));
    }
}
