#pragma warning disable CA1819 // XML serialization handles collections improperly, so we need to use arrays

using System;

namespace MediaBrowser.Model.Configuration;

/// <summary>
/// The settings of pre-encoded HLS.
/// </summary>
public class HlsOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether library files are pre-encoded and their HLS is played.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the libraries whose files are pre-encoded; none means every library.
    /// </summary>
    public Guid[] Libraries { get; set; } = [];

    /// <summary>
    /// Gets or sets the video qualities, by the height of the picture.
    /// </summary>
    public int[] Qualities { get; set; } = [1080, 720, 480];

    /// <summary>
    /// Gets or sets where the renditions are kept: an absolute local path, or <c>s3://bucket/prefix</c>.
    /// </summary>
    public string Store { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether a request from the local network may play the original file.
    /// </summary>
    public bool AllowLocalOriginal { get; set; }

    /// <summary>
    /// Gets or sets the users who may always play the original file.
    /// </summary>
    public Guid[] OriginalUsers { get; set; } = [];

    /// <summary>
    /// Gets or sets how long a worker holds a job without a heartbeat, in minutes.
    /// </summary>
    public int LeaseMinutes { get; set; } = 5;

    /// <summary>
    /// Gets or sets how often a job is tried before it fails for good.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Gets or sets how long the renditions of a content without a library file are kept, in hours.
    /// </summary>
    public int OrphanRetentionHours { get; set; } = 72;

    /// <summary>
    /// Gets or sets how long a playback grant is valid, in hours.
    /// </summary>
    public int PlaybackTokenHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets the key that signs playback grants, in Base64.
    /// </summary>
    public string PlaybackSigningKey { get; set; } = string.Empty;
}
