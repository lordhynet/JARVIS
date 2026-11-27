namespace Jarvis.Orchestrator.Forge;

// One authored speaking task. The instruction is spoken by device TTS in InstructionLocale; the learner answers in Locale.
// Corrections come only from ModelAnswer and Hints (owner decision D2): no AI-generated feedback, no invented errors.
public sealed record ForgeDrillItem(Guid Id, int Version, string Key, string Locale, string Level, string InstructionLocale,
    string Instruction, string ModelAnswer, IReadOnlyList<string> Hints);

// PF-05/07 starter content: original text written for JARVIS, versioned in code. Not a Domain Registry entry; installs,
// activates or grants nothing, and choosing an item authorises no paid work.
public static class ForgeDrillCatalog
{
    public const string Version = "forge-drill-catalog-v1";

    private static ForgeDrillItem Fr(string key, string level, string instruction, string answer, params string[] hints) =>
        new(ForgeLanguageProfiles.ReviewItemId("fr-FR", "drill:" + key), 1, key, "fr-FR", level, "en-GB", instruction, answer,
            Array.AsReadOnly(hints));

    public static IReadOnlyList<ForgeDrillItem> All { get; } = Array.AsReadOnly(new[]
    {
        Fr("fr-intro-name", "A1", "Introduce yourself: say your name and that you live in London.",
            "Bonjour, je m'appelle Paul et j'habite à Londres.",
            "« Je m'appelle » introduces your name.", "Use « à » before a city: à Londres."),
        Fr("fr-intro-work", "A1", "Say that you work as a software engineer.",
            "Je travaille comme ingénieur logiciel.",
            "« Travailler comme » + profession, with no article.", "« Ingénieure » is the feminine form."),
        Fr("fr-restaurant-table", "A1", "At a restaurant, ask for a table for two this evening.",
            "Bonsoir, une table pour deux, s'il vous plaît, pour ce soir.",
            "« S'il vous plaît » is the polite form.", "« Ce soir » means this evening."),
        Fr("fr-restaurant-bill", "A1", "Ask the waiter for the bill politely.",
            "L'addition, s'il vous plaît.",
            "« L'addition » is the bill in a restaurant.", "Keep it short; this is the usual phrase."),
        Fr("fr-directions-station", "A1", "Ask a passer-by where the train station is.",
            "Excusez-moi, où est la gare, s'il vous plaît ?",
            "« Excusez-moi » opens a polite question to a stranger.", "« La gare » is the train station."),
        Fr("fr-time-meeting", "A2", "Say that your meeting starts at half past nine tomorrow morning.",
            "Ma réunion commence demain matin à neuf heures et demie.",
            "« Et demie » adds half an hour.", "« Demain matin » means tomorrow morning."),
        Fr("fr-past-weekend", "A2", "Say that last weekend you visited a museum with friends.",
            "Le week-end dernier, j'ai visité un musée avec des amis.",
            "Passé composé: « avoir » + past participle (j'ai visité).", "« Dernier » follows « le week-end »."),
        Fr("fr-future-plan", "A2", "Say that next month you are going to learn to cook.",
            "Le mois prochain, je vais apprendre à cuisiner.",
            "Near future: « aller » + infinitive (je vais apprendre).", "« Apprendre à » + infinitive."),
        Fr("fr-opinion-city", "A2", "Give your opinion: say that you think the city is beautiful but expensive.",
            "Je trouve que la ville est belle mais chère.",
            "« Je trouve que » introduces an opinion.", "« La ville » is feminine, so the adjectives agree: belle, chère."),
        Fr("fr-request-repeat", "A1", "Ask the speaker to repeat more slowly, please.",
            "Pouvez-vous répéter plus lentement, s'il vous plaît ?",
            "« Pouvez-vous » + infinitive is a polite request.", "« Plus lentement » means more slowly.")
    });

    public static IReadOnlyList<ForgeDrillItem> For(string locale) =>
        ForgeLanguageProfiles.IsStarter(locale) ? All.Where(item => item.Locale == locale).ToArray() : throw new ArgumentOutOfRangeException(nameof(locale));
}
