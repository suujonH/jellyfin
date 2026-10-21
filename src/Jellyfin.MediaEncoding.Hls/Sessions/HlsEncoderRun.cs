using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Plans;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.MediaEncoding.Hls.Sessions;

/// <summary>
/// One ffmpeg process of an <see cref="HlsSession"/>: the segment muxer writes a GOP file at every keyframe, and a CSV list
/// of them, from which the run tells when a plan segment is complete. The files of a run lie in the transcode directory and
/// start with its id, like the files of other transcodes, so that the transcode manager deletes them with its own.
/// </summary>
public sealed class HlsEncoderRun : IDisposable
{
    // File changes wake the waiters; this catches what the watcher misses, such as a file created while it was busy.
    private static readonly TimeSpan _recheckInterval = TimeSpan.FromSeconds(1);

    private readonly Lock _lock = new();
    private readonly SegmentPlan _plan;
    private readonly long _toleranceTicks;
    private readonly string _segmentExtension;
    private readonly HlsListReader _list;
    private readonly ILogger _logger;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private HlsRunState _state;
    private int _completedThrough;
    private bool _disposed;

    internal HlsEncoderRun(SegmentPlan plan, int startIndex, int endIndex, HlsCutMode mode, string directory, string segmentExtension, ILogger logger)
    {
        _plan = plan;
        _segmentExtension = segmentExtension;
        _logger = logger;

        // Audio is cut by time, within a packet or two of the plan.
        _toleranceTicks = mode is HlsCutMode.CopyAudio or HlsCutMode.TranscodeAudio ? plan.Segments[0].DurationTicks / 2 : plan.ToleranceTicks;
        _completedThrough = startIndex - 1;

        Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        StartIndex = startIndex;
        EndIndex = endIndex;
        Mode = mode;
        Start = HlsCuts.GetRunStart(plan, startIndex, mode);
        Directory = directory;
        ListPath = Path.Combine(directory, Id + ".csv");
        InitPath = Path.Combine(directory, Id + "-init.mp4");
        SegmentPattern = Path.Combine(directory, Id + "-%d" + segmentExtension);
        _list = new HlsListReader(ListPath);
    }

    /// <summary>
    /// Gets the id of the run, which starts the names of its files.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the plan of the session of the run.
    /// </summary>
    public SegmentPlan Plan => _plan;

    /// <summary>
    /// Gets the first plan segment of the run.
    /// </summary>
    public int StartIndex { get; }

    /// <summary>
    /// Gets the plan segment after the last one of the run.
    /// </summary>
    public int EndIndex { get; }

    /// <summary>
    /// Gets what decides the cuts of the run.
    /// </summary>
    public HlsCutMode Mode { get; }

    /// <summary>
    /// Gets how the run starts.
    /// </summary>
    public HlsRunStart Start { get; }

    /// <summary>
    /// Gets the directory of the files of the run.
    /// </summary>
    public string Directory { get; }

    /// <summary>
    /// Gets the path of the CSV list, which is the output path of the transcoding job.
    /// </summary>
    public string ListPath { get; }

    /// <summary>
    /// Gets the path of the fMP4 init segment of the run.
    /// </summary>
    public string InitPath { get; }

    /// <summary>
    /// Gets the output pattern of the GOP files, numbered from 0.
    /// </summary>
    public string SegmentPattern { get; }

    /// <summary>
    /// Gets the transcoding job of the run, once it has started.
    /// </summary>
    public TranscodingJob? Job { get; private set; }

    internal HlsRunState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Gets the end of the complete segments from the start of the run on; the start of the run while there are none.
    /// </summary>
    internal long CompletedPositionTicks
    {
        get
        {
            lock (_lock)
            {
                return _completedThrough >= StartIndex ? _plan.Segments[_completedThrough].EndTicks : _plan.Segments[StartIndex].StartTicks;
            }
        }
    }

    /// <summary>
    /// Watches the files of the run once its ffmpeg process has started.
    /// </summary>
    /// <param name="job">The transcoding job of the run.</param>
    /// <param name="cancellationTokenSource">The cancellation token source the job was started with; it is cancelled when the job is killed.</param>
    internal void Attach(TranscodingJob job, CancellationTokenSource cancellationTokenSource)
    {
        Job = job;
        job.SegmentFiles = GetFilesThrough;
        try
        {
            var watcher = new FileSystemWatcher(Directory, Id + "*") { NotifyFilter = NotifyFilters.FileName };
            watcher.Created += OnFileChanged;
            watcher.Renamed += OnFileChanged;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            _logger.LogDebug(ex, "Could not watch the files of HLS run {RunId}; checking them every {Interval}", Id, _recheckInterval);
        }

        _timer = new Timer(_ => Refresh(), null, _recheckInterval, _recheckInterval);
        _ = WatchExitAsync(job, cancellationTokenSource);
        Refresh();
    }

    /// <summary>
    /// Locates a plan segment in the list written so far.
    /// </summary>
    /// <param name="index">The segment.</param>
    /// <returns>Where the segment lies.</returns>
    internal HlsSegmentLocation Locate(int index)
    {
        lock (_lock)
        {
            Read();
            return LocateCore(index);
        }
    }

    /// <summary>
    /// Waits until a plan segment is complete, or will never be in this run.
    /// </summary>
    /// <param name="index">The segment.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Where the segment lies; pending only when the run has stopped.</returns>
    internal async Task<HlsSegmentLocation> WaitForAsync(int index, CancellationToken cancellationToken)
    {
        while (true)
        {
            var changed = Volatile.Read(ref _changed).Task;
            HlsSegmentLocation location;
            HlsRunState state;
            lock (_lock)
            {
                Read();
                location = LocateCore(index);
                state = _state;
            }

            if (location.State != HlsSegmentState.Pending || state != HlsRunState.Running)
            {
                return location;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until the fMP4 init segment of the run is written: the muxer closes it before it opens the first GOP file.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the init segment is written; false when the run stopped before.</returns>
    internal async Task<bool> WaitForInitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var changed = Volatile.Read(ref _changed).Task;
            var state = State;
            if (File.Exists(InitPath) && (state == HlsRunState.Finished || File.Exists(GetFilePath(0))))
            {
                return true;
            }

            if (state != HlsRunState.Running)
            {
                return false;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the files of a located segment, in order.
    /// </summary>
    /// <param name="location">The location of the segment.</param>
    /// <returns>The paths of the files.</returns>
    internal IReadOnlyList<string> GetFiles(HlsSegmentLocation location)
    {
        lock (_lock)
        {
            var files = new string[location.EntryCount];
            for (var i = 0; i < files.Length; i++)
            {
                files[i] = Path.Combine(Directory, _list.Entries[location.FirstEntry + i].FileName);
            }

            return files;
        }
    }

    /// <summary>
    /// Gets the files the run has written that no segment after a plan segment holds, for the segment cleaner.
    /// </summary>
    /// <param name="index">The last segment.</param>
    /// <returns>The paths of the files.</returns>
    internal IEnumerable<string> GetFilesThrough(int index)
    {
        lock (_lock)
        {
            var high = GetHigh(index);
            var nextHigh = GetHigh(index + 1);
            var files = new List<string>();
            var entries = _list.Entries;
            for (var i = 0; i < entries.Count; i++)
            {
                // A first file without a start is no segment's, and comes before them all.
                var start = i > 0 ? entries[i].StartTicks : Start.FirstFileStartTicks;
                if (start >= high)
                {
                    break;
                }

                // A file is sent for the segments after its own that no file starts in: it is kept until the next file
                // starts before the end of the segment after the last one. The last file listed may still be such a file.
                if (i + 1 == entries.Count || entries[i + 1].StartTicks >= nextHigh)
                {
                    break;
                }

                files.Add(Path.Combine(Directory, entries[i].FileName));
            }

            return files;
        }
    }

    /// <summary>
    /// Deletes the files of the run.
    /// </summary>
    /// <exception cref="IOException">A file could not be deleted.</exception>
    internal void DeleteFiles()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, Id + "*"))
        {
            File.Delete(file);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            StopWatching();
        }
    }

    private string GetFilePath(int number) => Path.Combine(Directory, Id + "-" + number.ToString(CultureInfo.InvariantCulture) + _segmentExtension);

    // The end of the range of a plan segment: where the files of the next one start.
    private long GetHigh(int index) => index + 1 < _plan.Segments.Count ? _plan.Segments[index + 1].StartTicks - _toleranceTicks : long.MaxValue;

    private void OnFileChanged(object sender, FileSystemEventArgs e) => Refresh();

    // Reads the list, advances the completed segments for the throttler and wakes the waiters.
    private void Refresh()
    {
        try
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                Read();
            }
        }
        catch (InvalidDataException ex)
        {
            _logger.LogError(ex, "Could not read the segment list of HLS run {RunId}", Id);
        }

        Signal();
    }

    private async Task WatchExitAsync(TranscodingJob job, CancellationTokenSource cancellationTokenSource)
    {
        var exitCode = await job.Exited.ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                // The last lines are written before ffmpeg exits; they are read before the run reads as ended.
                Read();
                _state = cancellationTokenSource.IsCancellationRequested ? HlsRunState.Stopped
                    : exitCode == 0 ? HlsRunState.Finished
                    : HlsRunState.Failed;
                AdvanceCompleted();
                StopWatching();
            }
        }
        catch (InvalidDataException ex)
        {
            _logger.LogError(ex, "Could not read the segment list of HLS run {RunId}", Id);
            lock (_lock)
            {
                _state = HlsRunState.Failed;
                StopWatching();
            }
        }

        Signal();
    }

    // Callers hold the lock. A segment can complete without a new line, when the file after it is created.
    private void Read()
    {
        _list.Read();
        AdvanceCompleted();
    }

    private void AdvanceCompleted()
    {
        var advanced = false;
        while (_completedThrough + 1 < EndIndex && LocateCore(_completedThrough + 1).State == HlsSegmentState.Complete)
        {
            _completedThrough++;
            advanced = true;
        }

        if (advanced && Job is not null)
        {
            Job.CompletedPositionTicks = _plan.Segments[_completedThrough].EndTicks;
        }
    }

    private HlsSegmentLocation LocateCore(int index)
        => HlsSegmentLocator.Locate(
            _plan,
            index,
            _toleranceTicks,
            Start.FirstFileStartTicks,
            _list.Entries,
            _state == HlsRunState.Finished,
            number => File.Exists(GetFilePath(number)));

    private void Signal() => Interlocked.Exchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        _timer?.Dispose();
        _timer = null;
    }
}
