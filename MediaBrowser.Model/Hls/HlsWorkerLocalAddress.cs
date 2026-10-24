using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// The target of a .strm file whose media is kept on the machine of an HLS worker: <c>worker-local://{worker id}/{local path}</c>,
/// the id in its 32 digit form and the path as the worker names it. The server cannot read the file; only its worker encodes it.
/// </summary>
public static class HlsWorkerLocalAddress
{
    /// <summary>
    /// The scheme the target starts with.
    /// </summary>
    public const string Prefix = "worker-local://";

    private const int IdLength = 32;

    /// <summary>
    /// Reads a target.
    /// </summary>
    /// <param name="value">The target.</param>
    /// <param name="workerId">The worker that keeps the file.</param>
    /// <param name="localPath">The path of the file on the worker.</param>
    /// <returns>Whether the value is such a target.</returns>
    public static bool TryParse([NotNullWhen(true)] string? value, out Guid workerId, out string localPath)
    {
        workerId = Guid.Empty;
        localPath = string.Empty;
        if (value is null || !value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = value[Prefix.Length..];
        if (rest.Length < IdLength + 2
            || !Guid.TryParseExact(rest[..IdLength], "N", out var id)
            || id.Equals(Guid.Empty)
            || rest[IdLength] != '/')
        {
            return false;
        }

        var path = rest[(IdLength + 1)..];
        if (path.Any(char.IsControl))
        {
            return false;
        }

        workerId = id;
        localPath = path;
        return true;
    }

    /// <summary>
    /// Makes a target.
    /// </summary>
    /// <param name="workerId">The worker that keeps the file.</param>
    /// <param name="localPath">The path of the file on the worker.</param>
    /// <returns>The target.</returns>
    /// <exception cref="ArgumentException">The id is empty, or the path is empty or has a control character.</exception>
    public static string Format(Guid workerId, string localPath)
    {
        if (workerId.Equals(Guid.Empty))
        {
            throw new ArgumentException("The worker id is empty.", nameof(workerId));
        }

        if (string.IsNullOrEmpty(localPath) || localPath.Any(char.IsControl))
        {
            throw new ArgumentException("The local path is empty or has a control character.", nameof(localPath));
        }

        return Prefix + workerId.ToString("N", CultureInfo.InvariantCulture) + "/" + localPath;
    }
}
