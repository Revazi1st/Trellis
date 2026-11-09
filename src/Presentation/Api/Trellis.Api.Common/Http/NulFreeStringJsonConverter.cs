using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trellis.Api.Common.Http;

/// <summary>
/// Refuses a JSON string that holds U+0000 while the request binds. PostgreSQL's <c>text</c> stores no NUL,
/// so such a value would reach the first query or save that carries it and end as a 500; refused here, it is
/// a 400 <c>Request.Malformed</c> whose <c>errors[]</c> names the member (<see cref="ProblemCatalog.Malformed"/>),
/// on every endpoint at once, and no aggregate checks for it. Writing stays the built-in converter's, so a
/// response, and the body the idempotency fingerprint re-serialises, come out as before. Registered by
/// <c>AddApiCommon</c> beside the strongly-typed-id converter.
/// </summary>
internal sealed class NulFreeStringJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Refuse(reader.GetString());

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);

    /// <summary>A dictionary key is a string the client sent too; writing one falls back to the built-in converter.</summary>
    public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Refuse(reader.GetString())!;

    private static string? Refuse(string? value)
        => value is not null && value.Contains('\0', StringComparison.Ordinal) ? throw new NulCharacterException() : value;
}

/// <summary>
/// The serializer adds the member's path to it and the binder wraps it in a <c>BadHttpRequestException</c>;
/// its type is what tells <see cref="ProblemCatalog.Malformed"/> to describe a NUL rather than a wrong type.
/// </summary>
internal sealed class NulCharacterException() : JsonException(ProblemCatalog.NulCharacterDescription);
