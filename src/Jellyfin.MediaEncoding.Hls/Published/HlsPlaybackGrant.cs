using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.MediaEncoding.Hls.Published;

/// <summary>
/// The anonymous grant to play the pre-encoded HLS of one item, carried in the path so that clients need no authentication header:
/// <c>Base64Url(JSON {"v":1,"i":"{item N}","e":{unix seconds}}) "." Base64Url(HMACSHA256)</c>, the format of the DistributedHls plugin.
/// </summary>
public static class HlsPlaybackGrant
{
    /// <summary>
    /// The longest grant that is read.
    /// </summary>
    public const int MaxLength = 512;

    /// <summary>
    /// Creates a grant.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="expires">When the grant expires.</param>
    /// <param name="key">The signing key.</param>
    /// <returns>The grant.</returns>
    /// <exception cref="ArgumentException">The key is empty.</exception>
    public static string Create(Guid itemId, DateTimeOffset expires, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0)
        {
            throw new ArgumentException("The signing key is empty.", nameof(key));
        }

        var payload = ToBase64Url(JsonSerializer.SerializeToUtf8Bytes(new { v = 1, i = itemId.ToString("N"), e = expires.ToUnixTimeSeconds() }));
        return payload + "." + ToBase64Url(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(payload)));
    }

    /// <summary>
    /// Reads a grant. Never throws: anything but a well-formed, correctly signed, unexpired version 1 grant is refused.
    /// </summary>
    /// <param name="grant">The grant.</param>
    /// <param name="key">The signing key.</param>
    /// <param name="now">The current time.</param>
    /// <param name="itemId">The item, or <see cref="Guid.Empty"/> when the grant is refused.</param>
    /// <returns>Whether the grant is valid.</returns>
    public static bool TryVerify(string? grant, byte[] key, DateTimeOffset now, out Guid itemId)
    {
        itemId = Guid.Empty;
        if (string.IsNullOrEmpty(grant) || grant.Length > MaxLength || key is null || key.Length == 0)
        {
            return false;
        }

        var parts = grant.Split('.');
        if (parts.Length != 2
            || !TryFromBase64Url(parts[0], out var json)
            || !TryFromBase64Url(parts[1], out var signature)
            || !CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(parts[0]))))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1
                || !root.TryGetProperty("e", out var expires) || expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt64(out var e) || e <= now.ToUnixTimeSeconds()
                || !root.TryGetProperty("i", out var item) || item.ValueKind != JsonValueKind.String || !Guid.TryParseExact(item.GetString(), "N", out var id))
            {
                return false;
            }

            itemId = id;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ToBase64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // The strict inverse of ToBase64Url: only the URL-safe alphabet, the padding restored from the length, and only the canonical
    // spelling, so no second string is valid for the same bytes.
    private static bool TryFromBase64Url(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length == 0 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/').PadRight((text.Length + 3) / 4 * 4, '='));
        }
        catch (FormatException)
        {
            return false;
        }

        return string.Equals(ToBase64Url(bytes), text, StringComparison.Ordinal);
    }
}
