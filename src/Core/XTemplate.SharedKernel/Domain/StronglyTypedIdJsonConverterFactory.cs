using System.Text.Json;
using System.Text.Json.Serialization;

namespace XTemplate.SharedKernel.Domain;

/// <summary>Serializes any IStronglyTypedId&lt;T&gt; as its inner GUID string. Register once on JsonSerializerOptions.</summary>
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
    }
}
