using System.Security.Claims;
using System.Text.Json;

namespace CalCrony.Web.Auth;

/// <summary>Minimal JWT payload reader for display claims — no signature validation (the API validates).</summary>
public static class JwtParser
{
    /// <summary>Decodes the payload segment into claims.</summary>
    /// <param name="jwt">The encoded JWT.</param>
    /// <returns>The payload claims.</returns>
    /// <exception cref="FormatException">When a segment is not valid base64url.</exception>
    public static IEnumerable<Claim> ParseClaimsFromJwt(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            yield break;
        }

        Dictionary<string, object>? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Dictionary<string, object>>(ParseBase64WithoutPadding(parts[1]));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or JsonException)
        {
            yield break;
        }

        if (payload is null)
        {
            yield break;
        }

        foreach (var (key, value) in payload)
        {
            if (value is JsonElement { ValueKind: JsonValueKind.Array } element)
            {
                foreach (var item in element.EnumerateArray())
                {
                    yield return new Claim(key, item.ToString());
                }
            }
            else
            {
                yield return new Claim(key, value?.ToString() ?? string.Empty);
            }
        }
    }

    /// <summary>Base64url-decodes a JWT segment, restoring stripped padding.</summary>
    /// <param name="base64">The base64url-encoded segment.</param>
    /// <returns>The decoded bytes.</returns>
    /// <exception cref="FormatException">When the input is not valid base64url.</exception>
    private static byte[] ParseBase64WithoutPadding(string base64)
    {
        var padded = base64.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }
}
