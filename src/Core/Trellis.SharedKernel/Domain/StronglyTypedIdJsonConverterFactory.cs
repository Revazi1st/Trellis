using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trellis.SharedKernel.Domain;

/// <summary>
/// Serializes any IStronglyTypedId&lt;T&gt; as its inner GUID string — as a value and as a dictionary key,
/// so <c>Dictionary&lt;InvoiceId, T&gt;</c> round-trips too. Register once on JsonSerializerOptions.
/// </summary>
public sealed class StronglyTypedIdJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsValueType
           && typeToConvert.GetInterfaces().Any(static i =>
               i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStronglyTypedId<>));

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(
            typeof(StronglyTypedIdJsonConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class StronglyTypedIdJsonConverter<TId> : JsonConverter<TId>
        where TId : struct, IStronglyTypedId<TId>
    {
        public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => TId.From(reader.GetGuid());

        public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value);

        // Property names arrive as text; "D" is the one format Read (Utf8JsonReader.GetGuid) accepts and Write emits.
        public override TId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => Guid.TryParseExact(reader.GetString(), "D", out Guid value)
                ? TId.From(value)
                : throw new JsonException($"A {typeof(TId).Name} dictionary key must be a GUID.");

        public override void WriteAsPropertyName(Utf8JsonWriter writer, TId value, JsonSerializerOptions options)
            => writer.WritePropertyName(value.Value.ToString("D"));
    }
}
