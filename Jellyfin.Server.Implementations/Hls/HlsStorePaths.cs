using System;
using System.Linq;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// The names of the marker in the root of an HLS store, and the rule for paths in it.
/// </summary>
internal static class HlsStorePaths
{
    /// <summary>
    /// The marker in the root that says the store is the HLS store.
    /// </summary>
    public const string MarkerName = ".jellyfin-hls";

    /// <summary>
    /// The marker the DistributedHls plugin wrote, which counts as the marker too.
    /// </summary>
    public const string LegacyMarkerName = ".distributed-hls";

    /// <summary>
    /// Checks that a path is relative, with forward slashes, and has no empty, <c>.</c> or <c>..</c> parts, no parts that end
    /// with a dot or a space, which Windows trims so that <c>...</c> would name the parent, and no control characters, which
    /// XML cannot carry in a listing of network storage.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="paramName">The name of the parameter it was given as.</param>
    /// <exception cref="ArgumentException">The path is not such a path.</exception>
    public static void Validate(string path, string paramName)
    {
        ArgumentException.ThrowIfNullOrEmpty(path, paramName);
        if (path.Contains('\\', StringComparison.Ordinal)
            || path.Contains(':', StringComparison.Ordinal)
            || path.Any(char.IsControl)
            || path.Split('/').Any(part => part.Length == 0 || part[^1] is '.' or ' '))
        {
            throw new ArgumentException("A path in the HLS store must be relative, with forward slashes, without parts ending in a dot or a space and without control characters: " + path, paramName);
        }
    }
}
