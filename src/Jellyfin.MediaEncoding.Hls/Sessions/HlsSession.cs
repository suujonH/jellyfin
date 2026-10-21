using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Jellyfin.MediaEncoding.Hls.Plans;
using MediaBrowser.Common.Extensions;

namespace Jellyfin.MediaEncoding.Hls.Sessions;

/// <summary>
/// One variant of one playback: its plan and the run that encodes it. The key names the variant; the id, which the segment
/// URLs carry, is derived from it, so that a session lost with a restart of the server is rebuilt the same.
/// </summary>
public sealed class HlsSession : IDisposable
{
    private long _lastAccessTicks = DateTime.UtcNow.Ticks;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsSession"/> class.
    /// </summary>
    /// <param name="key">The key of the variant: the user, the item and the parameters of the stream.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="mode">What decides the cuts.</param>
    /// <param name="directory">The directory of the files, the transcode directory.</param>
    /// <param name="segmentExtension">The extension of the segments: <c>.ts</c>, or <c>.mp4</c> for fMP4.</param>
    /// <param name="playSessionId">The play session, whose other variants stop when this one starts a run.</param>
    /// <param name="deviceId">The device, which stands for the play session when there is none.</param>
    public HlsSession(string key, SegmentPlan plan, HlsCutMode mode, string directory, string segmentExtension, string? playSessionId, string? deviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(segmentExtension);

        Key = key;
        Id = key.GetMD5().ToString("N", CultureInfo.InvariantCulture);
        Plan = plan;
        Mode = mode;
        Directory = directory;
        SegmentExtension = segmentExtension;
        PlaySessionId = playSessionId;
        DeviceId = deviceId;
        InitPath = Path.Combine(directory, Id + "-init.mp4");
    }

    /// <summary>
    /// Gets the id of the session, which the segment URLs carry.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the key of the variant.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Gets the plan.
    /// </summary>
    public SegmentPlan Plan { get; }

    /// <summary>
    /// Gets what decides the cuts.
    /// </summary>
    public HlsCutMode Mode { get; }

    /// <summary>
    /// Gets the directory of the files.
    /// </summary>
    public string Directory { get; }

    /// <summary>
    /// Gets the extension of the segments.
    /// </summary>
    public string SegmentExtension { get; }

    /// <summary>
    /// Gets a value indicating whether the segments are fMP4, behind an init segment.
    /// </summary>
    public bool IsFragmented => string.Equals(SegmentExtension, ".mp4", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the path of the init segment of the session: a copy of the first one a run wrote, the only one sent.
    /// </summary>
    public string InitPath { get; }

    internal string? PlaySessionId { get; }

    internal string? DeviceId { get; }

    internal HlsEncoderRun? Run { get; set; }

    internal SemaphoreSlim Lock { get; } = new(1, 1);

    // Set, under the lock, once the session has expired; a request that still holds it adds it back.
    internal bool IsClosed { get; set; }

    internal DateTime LastAccess => new(Interlocked.Read(ref _lastAccessTicks), DateTimeKind.Utc);

    internal void Touch() => Interlocked.Exchange(ref _lastAccessTicks, DateTime.UtcNow.Ticks);

    /// <inheritdoc />
    public void Dispose()
    {
        Run?.Dispose();
        Lock.Dispose();
    }
}
