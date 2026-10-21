using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Model.Hls;

namespace Jellyfin.MediaEncoding.Hls.Published;

/// <summary>
/// Compares the files a worker reported for a rendition with those in the HLS store.
/// </summary>
public static class HlsOutputVerifier
{
    /// <summary>
    /// Checks that every reported file is in the store with the reported length. Files that were not reported are ignored.
    /// </summary>
    /// <param name="reported">The files the worker reported.</param>
    /// <param name="stored">The files listed in the directory of the rendition.</param>
    /// <returns><c>null</c> when every file is in place, otherwise the first problem.</returns>
    public static string? Compare(IReadOnlyList<HlsOutputFile> reported, IReadOnlyList<HlsStoreFile> stored)
    {
        ArgumentNullException.ThrowIfNull(reported);
        ArgumentNullException.ThrowIfNull(stored);
        if (stored.Count == 0)
        {
            return "The directory is missing or empty.";
        }

        var lengths = stored.ToDictionary(f => f.Path, f => f.Length, StringComparer.Ordinal);
        foreach (var file in reported)
        {
            if (!lengths.TryGetValue(file.Path, out var length))
            {
                return "The file " + file.Path + " is missing.";
            }

            if (length != file.Length)
            {
                return string.Create(CultureInfo.InvariantCulture, $"The file {file.Path} is {length} bytes long, not {file.Length}.");
            }
        }

        return null;
    }
}
