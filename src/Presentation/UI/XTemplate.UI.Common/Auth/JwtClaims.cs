using System.Security.Claims;
using System.Text.Json;

namespace XTemplate.UI.Common.Auth;

/// <summary>
/// Reads claims from a JWT payload for UI display/convenience only. Deliberately does NOT validate the
/// signature — the API is the authority; this just lets the UI show the user and gate menu items.
/// </summary>
internal static class JwtClaims
{
    public static IReadOnlyList<Claim> Read(string accessToken)
    {
        Dictionary<string, JsonElement>? map = ParsePayload(accessToken);
        if (map is null)
        {
            return [];
        }

        var claims = new List<Claim>();
        foreach ((string type, JsonElement value) in map)
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                claims.AddRange(value.EnumerateArray().Select(item => new Claim(type, item.ToString())));
            }
            else
            {
                claims.Add(new Claim(type, value.ToString()));
            }
        }

        return claims;
    }

    /// <summary>The token's <c>exp</c> claim, or null when absent/unreadable (treat as expired).</summary>
    public static DateTimeOffset? GetExpiration(string accessToken)
        => ParsePayload(accessToken) is { } map
           && map.TryGetValue("exp", out JsonElement exp)
           && exp.TryGetInt64(out long unixSeconds)
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
            : null;

    private static Dictionary<string, JsonElement>? ParsePayload(string accessToken)
    {
        string[] parts = accessToken.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            byte[] payload = Base64UrlDecode(parts[1]);
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        string padded = input.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded,
        };

        return Convert.FromBase64String(padded);
    }
}
