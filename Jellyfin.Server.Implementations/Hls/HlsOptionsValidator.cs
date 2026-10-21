using System;
using System.Linq;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// Checks the settings of pre-encoded HLS.
/// </summary>
public static class HlsOptionsValidator
{
    /// <summary>
    /// The least length of the key that signs playback grants, in bytes, so a grant cannot be forged by trying keys.
    /// </summary>
    public const int MinSigningKeyBytes = 32;

    /// <summary>
    /// Checks the settings.
    /// </summary>
    /// <param name="options">The settings.</param>
    /// <returns><c>null</c> when they are valid, otherwise what is wrong.</returns>
    public static string? Validate(HlsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Libraries is null || options.Qualities is null)
        {
            return "The libraries and the qualities must not be null.";
        }

        if (options.Qualities.Any(q => !HlsEncodingLadder.Heights.Contains(q)) || options.Qualities.Distinct().Count() != options.Qualities.Length)
        {
            return "Every quality must be one of " + string.Join(", ", HlsEncodingLadder.Heights) + ", each at most once.";
        }

        // An empty list would withdraw every published rendition at once, beyond undoing.
        if (options.Qualities.Length == 0)
        {
            return "At least one quality is required; turn pre-encoded HLS off instead.";
        }

        if (options.LeaseMinutes is < 1 or > 60)
        {
            return "The lease must be 1 to 60 minutes long.";
        }

        if (options.MaxAttempts is < 1 or > 20)
        {
            return "The attempts must be 1 to 20.";
        }

        if (options.OrphanRetentionHours is < 0 or > 8760)
        {
            return "The renditions of a content without a library file must be kept 0 to 8760 hours.";
        }

        if (options.PlaybackTokenHours is < 1 or > 168)
        {
            return "A playback grant must be valid 1 to 168 hours.";
        }

        if (!string.IsNullOrEmpty(options.PlaybackSigningKey) && !IsValidSigningKey(options.PlaybackSigningKey))
        {
            return "The playback signing key must be Base64 of at least " + MinSigningKeyBytes + " bytes.";
        }

        return null;
    }

    private static bool IsValidSigningKey(string key)
    {
        var bytes = new byte[(key.Length * 3 / 4) + 3];
        return Convert.TryFromBase64String(key, bytes, out var length) && length >= MinSigningKeyBytes;
    }
}
