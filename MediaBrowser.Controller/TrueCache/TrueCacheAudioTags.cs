#pragma warning disable CS1591

using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// The tags of an audio file, as the tag reader reports them.
/// Multi-valued fields keep their values separately.
/// </summary>
public sealed class TrueCacheAudioTags
{
    /// <summary>The separator the server uses between the values of a multi-valued tag.</summary>
    public const string ValueSeparator = "\u001F";

    public string? Title { get; init; }

    public string? Album { get; init; }

    public IReadOnlyList<string> Artists { get; init; } = [];

    public IReadOnlyList<string> AlbumArtists { get; init; } = [];

    public IReadOnlyList<string> Composers { get; init; } = [];

    public IReadOnlyList<string> Genres { get; init; } = [];

    public int? Year { get; init; }

    public int? TrackNumber { get; init; }

    public int? DiscNumber { get; init; }

    public DateTime? Date { get; init; }

    public string? Description { get; init; }

    public string? Comment { get; init; }

    public string? Publisher { get; init; }

    /// <summary>Gets a value indicating whether ID3v1 is the only tag format of the file.</summary>
    public bool Id3v1Only { get; init; }

    /// <summary>Gets the remaining fields by their native key, for example <c>REPLAYGAIN_TRACK_GAIN</c>.</summary>
    public IReadOnlyDictionary<string, string> AdditionalFields { get; init; } = new Dictionary<string, string>();
}
