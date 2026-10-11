using System.Text.Json;

namespace Trellis.Api.Common.Http;

/// <summary>
/// Turns the member path FluentValidation names (<c>Lines[0].Amount</c>) into the JSON path the client
/// sent (<c>lines[0].amount</c>): each segment's identifier is camel-cased the way the API serialises
/// (<c>JsonSerializerDefaults.Web</c>), indexers are kept. The <c>field</c> of a problem's <c>errors[]</c>.
/// </summary>
internal static class JsonFieldPath
{
    public static string From(string propertyPath)
    {
        if (string.IsNullOrEmpty(propertyPath))
        {
            return propertyPath;
        }

        string[] segments = propertyPath.Split('.');
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            int indexer = segment.IndexOf('[', StringComparison.Ordinal);
            segments[i] = indexer < 0
                ? JsonNamingPolicy.CamelCase.ConvertName(segment)
                : JsonNamingPolicy.CamelCase.ConvertName(segment[..indexer]) + segment[indexer..];
        }

        return string.Join('.', segments);
    }

    /// <summary>
    /// The member path of a <see cref="JsonException.Path"/> (<c>$.lines[1].amount</c> → <c>lines[1].amount</c>),
    /// camel-cased like every other <c>field</c> (the binder matches names case-insensitively, so the client
    /// may have sent <c>Amount</c>); null for the root (<c>$</c>) or a bracketed name, which is not a member
    /// of any contract this API declares.
    /// </summary>
    public static string? FromSerializerPath(string? jsonPath)
        => jsonPath is { Length: > 2 } && jsonPath.StartsWith("$.", StringComparison.Ordinal) ? From(jsonPath[2..]) : null;
}
