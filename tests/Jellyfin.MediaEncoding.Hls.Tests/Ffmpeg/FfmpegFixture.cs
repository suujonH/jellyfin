using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Ffmpeg;

/// <summary>
/// Finds ffmpeg through <c>JELLYFIN_FFMPEG</c>, the variable the server reads, and generates the sample media for the cut tests.
/// Without it the tests are skipped.
/// </summary>
public sealed class FfmpegFixture : IAsyncLifetime
{
    public const string FfmpegVariable = "JELLYFIN_FFMPEG";

    /// <summary>The frame duration of the samples, 23.976 fps.</summary>
    public const double FrameDuration = 1001d / 24000;

    public const double SourceSeconds = 40;

    // Irregular GOPs, from 0.4 s to 6.3 s, so a plan boundary may follow its preceding keyframe closely or from far away.
    private const string SourceKeyframes = "0,1.7,4.2,6.1,9.9,12.0,13.5,19.8,20.2,25.0,31.3,32.0,38.4";

    private static readonly TimeSpan _processTimeout = TimeSpan.FromMinutes(2);

    public FfmpegFixture()
    {
        FfmpegPath = Environment.GetEnvironmentVariable(FfmpegVariable) ?? string.Empty;
        FfprobePath = Path.Combine(Path.GetDirectoryName(FfmpegPath) ?? string.Empty, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        WorkDirectory = Path.Combine(Path.GetTempPath(), "jellyfin-hls-cuts-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
    }

    public string FfmpegPath { get; }

    public string FfprobePath { get; }

    public string WorkDirectory { get; }

    public string? UnavailableReason { get; private set; }

    /// <summary>Gets the H.264 (with B-frames) and AAC sample in Matroska.</summary>
    public string Mkv => Path.Combine(WorkDirectory, "source.mkv");

    public string Mp4 => Path.Combine(WorkDirectory, "source.mp4");

    /// <summary>Gets the sample in MPEG-TS, which has no index, so a seek lands on any packet rather than on a keyframe.</summary>
    public string Ts => Path.Combine(WorkDirectory, "source.ts");

    /// <summary>Gets the Matroska sample with the video starting 0.3 s after the audio, off the grid of a uniform plan.</summary>
    public string LateVideoMkv => Path.Combine(WorkDirectory, "late-video.mkv");

    public static double ParseSeconds(string? value) => double.Parse(value ?? "NaN", CultureInfo.InvariantCulture);

    public static string FormatSeconds(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        if (string.IsNullOrEmpty(FfmpegPath))
        {
            UnavailableReason = $"Set {FfmpegVariable} to an ffmpeg binary to run the HLS cut tests.";
            return;
        }

        if (!File.Exists(FfmpegPath) || !File.Exists(FfprobePath))
        {
            UnavailableReason = $"{FfmpegVariable} must name an ffmpeg binary with ffprobe next to it.";
            return;
        }

        Directory.CreateDirectory(WorkDirectory);
        var cancellationToken = TestContext.Current.CancellationToken;
        var encoders = await RunAsync(FfmpegPath, ["-hide_banner", "-encoders"], WorkDirectory, cancellationToken).ConfigureAwait(false);
        if (!encoders.Contains(" libx264 ", StringComparison.Ordinal))
        {
            UnavailableReason = $"The ffmpeg in {FfmpegVariable} has no libx264 encoder.";
            return;
        }

        var seconds = FormatSeconds(SourceSeconds);
        await FfmpegAsync(
            WorkDirectory,
            [
                "-f", "lavfi", "-i", $"testsrc2=size=320x180:rate=24000/1001:duration={seconds}",
                "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={seconds}",
                "-c:v", "libx264", "-preset", "veryfast", "-bf", "2", "-g", "1000", "-keyint_min", "1000", "-sc_threshold", "0",
                "-force_key_frames", SourceKeyframes, "-c:a", "aac", "-b:a", "96k", "-shortest", Mkv
            ],
            cancellationToken).ConfigureAwait(false);
        await FfmpegAsync(WorkDirectory, ["-i", Mkv, "-c", "copy", Mp4], cancellationToken).ConfigureAwait(false);
        await FfmpegAsync(WorkDirectory, ["-i", Mkv, "-c", "copy", Ts], cancellationToken).ConfigureAwait(false);
        await FfmpegAsync(
            WorkDirectory,
            ["-i", Mkv, "-itsoffset", "0.3", "-i", Mkv, "-map", "1:v:0", "-map", "0:a:0", "-c", "copy", LateVideoMkv],
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(WorkDirectory))
            {
                Directory.Delete(WorkDirectory, true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary directory is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // A leftover temporary directory is harmless.
        }

        return ValueTask.CompletedTask;
    }

    public void SkipIfUnavailable() => Assert.SkipWhen(UnavailableReason is not null, UnavailableReason ?? string.Empty);

    public string GetSource(string container) => container switch
    {
        "mkv" => Mkv,
        "mp4" => Mp4,
        "ts" => Ts,
        _ => throw new ArgumentOutOfRangeException(nameof(container), container, null)
    };

    /// <summary>Creates an empty directory for one ffmpeg run.</summary>
    public string CreateRunDirectory(string name)
    {
        var directory = Path.Combine(WorkDirectory, name);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }

        Directory.CreateDirectory(directory);
        return directory;
    }

    public Task<string> FfmpegAsync(string workingDirectory, IEnumerable<string> arguments, CancellationToken cancellationToken)
        => RunAsync(FfmpegPath, ["-hide_banner", "-y", "-v", "error", .. arguments], workingDirectory, cancellationToken);

    /// <summary>Runs ffmpeg like the transcode manager does, which reads the exit code rather than failing on it.</summary>
    public async Task<(int ExitCode, string Error)> TryFfmpegAsync(string workingDirectory, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(FfmpegPath, ["-hide_banner", "-y", "-v", "error", .. arguments], workingDirectory, cancellationToken).ConfigureAwait(false);
        return (result.ExitCode, result.Error);
    }

    /// <summary>Gets the video keyframe times relative to the format start, the timeline of <c>-copyts -start_at_zero</c>.</summary>
    public async Task<IReadOnlyList<double>> GetKeyframesAsync(string path, CancellationToken cancellationToken)
    {
        var output = await RunAsync(FfprobePath, ["-v", "error", "-show_entries", "format=start_time", "-of", "json", path], WorkDirectory, cancellationToken).ConfigureAwait(false);
        double start;
        using (var json = JsonDocument.Parse(output))
        {
            start = ParseSeconds(json.RootElement.GetProperty("format").GetProperty("start_time").GetString());
        }

        var packets = await GetPacketsAsync(path, "v:0", cancellationToken).ConfigureAwait(false);
        return packets.Where(p => p.IsKeyframe).Select(p => ParseSeconds(p.Pts) - start).Order().ToList();
    }

    /// <summary>Gets the duration ffprobe reports for the whole file.</summary>
    public async Task<double> GetFormatDurationAsync(string path, CancellationToken cancellationToken)
    {
        var output = await RunAsync(FfprobePath, ["-v", "error", "-show_entries", "format=duration", "-of", "json", path], WorkDirectory, cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(output);
        return ParseSeconds(json.RootElement.GetProperty("format").GetProperty("duration").GetString());
    }

    public async Task<IReadOnlyList<ProbePacket>> GetPacketsAsync(string path, string? stream, CancellationToken cancellationToken)
    {
        List<string> arguments = ["-v", "error"];
        if (stream is not null)
        {
            arguments.AddRange(["-select_streams", stream]);
        }

        arguments.AddRange(["-show_entries", "packet=stream_index,pts_time,size,flags", "-of", "json", path]);
        var output = await RunAsync(FfprobePath, arguments, WorkDirectory, cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(output);
        var packets = new List<ProbePacket>();
        if (json.RootElement.TryGetProperty("packets", out var array))
        {
            foreach (var packet in array.EnumerateArray())
            {
                packets.Add(new ProbePacket(
                    packet.GetProperty("stream_index").GetInt32(),
                    packet.TryGetProperty("pts_time", out var pts) ? pts.GetString() ?? "N/A" : "N/A",
                    int.Parse(packet.GetProperty("size").GetString() ?? "0", CultureInfo.InvariantCulture),
                    (packet.GetProperty("flags").GetString() ?? string.Empty).Contains('K', StringComparison.Ordinal)));
            }
        }

        return packets;
    }

    /// <summary>Gets the packets of segment files read as one stream; fMP4 fragments are read behind the given init segment.</summary>
    public Task<IReadOnlyList<ProbePacket>> GetSegmentPacketsAsync(string? initPath, IEnumerable<string> segmentPaths, CancellationToken cancellationToken)
        => GetPacketsAsync(JoinSegments(initPath, segmentPaths), null, cancellationToken);

    /// <summary>Decodes segment files as one stream and returns what ffmpeg reports as errors.</summary>
    public async Task<string> GetDecodeErrorsAsync(string? initPath, IEnumerable<string> segmentPaths, CancellationToken cancellationToken)
    {
        var joined = JoinSegments(initPath, segmentPaths);
        var result = await ExecuteAsync(FfmpegPath, ["-hide_banner", "-v", "error", "-i", joined, "-f", "null", "-"], WorkDirectory, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Error.Trim() : $"exit code {result.ExitCode}: {result.Error.Trim()}";
    }

    private static async Task<string> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(fileName, arguments, workingDirectory, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} {string.Join(' ', arguments)} exited with {result.ExitCode}: {result.Error}");
        }

        return result.Output;
    }

    private static async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start " + fileName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_processTimeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw;
        }

        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    // Writes the files one after another into a new file, the way a player appends them.
    private string JoinSegments(string? initPath, IEnumerable<string> segmentPaths)
    {
        var paths = segmentPaths.ToList();
        var extension = initPath is null ? Path.GetExtension(paths[0]) : ".mp4";
        var joined = Path.Combine(WorkDirectory, "joined-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + extension);
        using var output = File.Create(joined);
        foreach (var path in initPath is null ? paths : paths.Prepend(initPath))
        {
            using var input = File.OpenRead(path);
            input.CopyTo(output);
        }

        return joined;
    }
}
