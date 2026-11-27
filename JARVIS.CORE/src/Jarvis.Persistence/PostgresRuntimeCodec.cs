using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Persistence;

// Canonical dictionary order makes the stored fingerprint stable after JSONB normalization.
internal static class PostgresRuntimeCodec
{
    private static readonly HashSet<string> Hot = [nameof(RuntimeState.AudioLease), nameof(RuntimeState.AudioDrainLease),
        nameof(RuntimeState.AudioDrainUntil), nameof(RuntimeState.NextAudioFence), nameof(RuntimeState.VoiceTickets), nameof(RuntimeState.VoiceTurns),
        nameof(RuntimeState.AgentControls), nameof(RuntimeState.Devices), nameof(RuntimeState.DeviceCredentials), nameof(RuntimeState.WakeCandidateAttempts)];
    public static bool IsHot(string name) => Hot.Contains(name) || PostgresRuntimeLearningState.IsProperty(name) || PostgresRuntimeTrainingState.IsProperty(name);
    private static readonly JsonSerializerOptions WriteOptions = Create();
    private static JsonSerializerOptions Create()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info => {
            if (info.Type != typeof(RuntimeState)) return;
            foreach (var property in info.Properties.Where(p => IsHot(p.Name)).ToArray()) info.Properties.Remove(property);
        });
        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        options.Converters.Add(new OrderedMaps()); return options;
    }
    public static string Serialize(RuntimeState state) => JsonSerializer.Serialize(state, WriteOptions);
    public static string Fingerprint(string json) => PrivacyData.Hash(json);
    private sealed class OrderedMaps : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() is var definition &&
            (definition == typeof(Dictionary<,>) || definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(IDictionary<,>));
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(OrderedMap<,,>).MakeGenericType(type, type.GenericTypeArguments[0], type.GenericTypeArguments[1]))!;
    }
    private sealed class OrderedMap<TMap, TKey, TValue> : JsonConverter<TMap> where TMap : IEnumerable<KeyValuePair<TKey, TValue>> where TKey : notnull
    {
        public override TMap Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException("Write-only canonical map converter.");
        public override void Write(Utf8JsonWriter writer, TMap value, JsonSerializerOptions options)
        {
            writer.WriteStartObject(); var keyConverter = (JsonConverter<TKey>)options.GetConverter(typeof(TKey));
            foreach (var item in value.OrderBy(p => Convert.ToString(p.Key, CultureInfo.InvariantCulture), StringComparer.Ordinal)) {
                keyConverter.WriteAsPropertyName(writer, item.Key, options); JsonSerializer.Serialize(writer, item.Value, options);
            }
            writer.WriteEndObject();
        }
    }
}
