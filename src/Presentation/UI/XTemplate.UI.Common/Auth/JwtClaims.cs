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
        string[] parts = accessToken.Split('.');
        if (parts.Length != 3)
        {
            return [];
        }

        byte[] payload = Base64UrlDecode(parts[1]);
        Dictionary<string, JsonElement>? map =
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload);

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
