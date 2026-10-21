using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Hls;

namespace Jellyfin.MediaEncoding.Hls.Published;

/// <summary>
/// Checks the outputs a worker reports against the plan of its job.
/// </summary>
public static class HlsCompletionValidator
{
    /// <summary>
    /// The most files a rendition may have.
    /// </summary>
    public const int MaxFilesPerRendition = 100000;

    // ffmpeg writes the durations with six decimals; the slack also covers the rounding of the frame rate.
    private const double DurationSlackSeconds = 0.001;

    // An AAC frame.
    private const int AudioFrameSamples = 1024;

    /// <summary>
    /// Checks that every rendition of the plan is reported once with the files and segments the plan allows, and a summary
    /// that agrees with the plan.
    /// </summary>
    /// <param name="plan">The plan of the job.</param>
    /// <param name="renditions">The reported renditions.</param>
    /// <exception cref="ArgumentException">The outputs do not fit the plan; the message says why.</exception>
    public static void Validate(HlsEncodePlan plan, IReadOnlyList<HlsRenditionOutput>? renditions)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (renditions is null || renditions.Count == 0)
        {
            throw new ArgumentException("The completion reports no renditions.", nameof(renditions));
        }

        var targets = plan.Renditions.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rendition in renditions)
        {
            if (rendition?.Key is null || !targets.ContainsKey(rendition.Key))
            {
                throw new ArgumentException("The completion reports a rendition that is not part of the job: " + rendition?.Key, nameof(renditions));
            }

            if (!reported.Add(rendition.Key))
            {
                throw new ArgumentException("The completion reports rendition " + rendition.Key + " twice.", nameof(renditions));
            }
        }

        var missing = targets.Keys.FirstOrDefault(key => !reported.Contains(key));
        if (missing is not null)
        {
            throw new ArgumentException("The completion lacks rendition " + missing + ".", nameof(renditions));
        }

        foreach (var rendition in renditions)
        {
            var target = targets[rendition.Key];
            var segments = ValidateFiles(rendition, target.Kind, plan.ExpectedSegments);
            ValidateDurations(rendition, target, plan, segments);
            ValidateSummary(rendition, target);
        }
    }

    // Returns the number of segments.
    private static int ValidateFiles(HlsRenditionOutput rendition, HlsRenditionKind kind, int expectedSegments)
    {
        var files = rendition.Files;
        if (files is not { Count: > 0 and <= MaxFilesPerRendition })
        {
            throw new ArgumentException("Rendition " + rendition.Key + " reports no files or too many.", nameof(rendition));
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var lastSegment = -1;
        foreach (var file in files)
        {
            // Required names are matched exactly as well: the server writes and serves them with this spelling.
            if (file?.Path is null || !HlsRenditionPaths.IsAllowedFile(kind, file.Path, expectedSegments))
            {
                throw new ArgumentException("Rendition " + rendition.Key + " reports a file that is not allowed: " + file?.Path, nameof(rendition));
            }

            if (file.Length < 0 || !names.Add(file.Path))
            {
                throw new ArgumentException("Rendition " + rendition.Key + " reports file " + file.Path + " with a negative length or twice.", nameof(rendition));
            }

            if (HlsRenditionPaths.TryGetSegmentIndex(file.Path, out var index))
            {
                lastSegment = Math.Max(lastSegment, index);
            }
        }

        var lacking = HlsRenditionPaths.GetRequiredFiles(kind).FirstOrDefault(required => !names.Contains(required));
        if (lacking is not null)
        {
            throw new ArgumentException("Rendition " + rendition.Key + " lacks " + lacking + ".", nameof(rendition));
        }

        if (kind is not (HlsRenditionKind.Video or HlsRenditionKind.Audio))
        {
            return 0;
        }

        // The names are distinct, so the segments are numbered from 0 without gaps exactly when the last one is their count minus one.
        var count = names.Count - HlsRenditionPaths.GetRequiredFiles(kind).Count;
        if (count == 0 || lastSegment != count - 1)
        {
            throw new ArgumentException("The segments of rendition " + rendition.Key + " are not numbered from 0 without gaps.", nameof(rendition));
        }

        if (Math.Abs(count - expectedSegments) > 1)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Rendition {rendition.Key} has {count} segments where {expectedSegments} are expected."),
                nameof(rendition));
        }

        return count;
    }

    private static void ValidateDurations(HlsRenditionOutput rendition, HlsPlannedRendition target, HlsEncodePlan plan, int segments)
    {
        var durations = rendition.SegmentDurations ?? [];
        if (durations.Count != segments)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Rendition {rendition.Key} reports {durations.Count} segment durations for {segments} segments."),
                nameof(rendition));
        }

        if (segments == 0)
        {
            return;
        }

        // Every segment but the last is cut at the first keyframe after a multiple of the segment length: within a frame of it.
        var tolerance = DurationSlackSeconds + (target.Kind == HlsRenditionKind.Video
            ? 1 / target.Video!.FramesPerSecond
            : (double)AudioFrameSamples / plan.AudioSampleRate);
        for (var i = 0; i < durations.Count; i++)
        {
            var duration = durations[i];
            var valid = double.IsFinite(duration) && duration > 0 && (i == durations.Count - 1
                ? duration <= plan.SegmentSeconds + tolerance
                : Math.Abs(duration - plan.SegmentSeconds) <= tolerance);
            if (!valid)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Segment {i} of rendition {rendition.Key} lasts {duration} seconds."),
                    nameof(rendition));
            }
        }
    }

    private static void ValidateSummary(HlsRenditionOutput rendition, HlsPlannedRendition target)
    {
        var summary = rendition.Summary ?? throw new ArgumentException("Rendition " + rendition.Key + " has no summary.", nameof(rendition));
        switch (target.Kind)
        {
            case HlsRenditionKind.Video:
                if (target.Video is not { } video || summary.Quality != video.Name || summary.Width != video.Width || summary.Height != video.Height)
                {
                    throw new ArgumentException("The quality or size of video rendition " + rendition.Key + " differs from the job.", nameof(rendition));
                }

                if (summary.Bandwidth <= 0 || summary.AverageBandwidth <= 0)
                {
                    throw new ArgumentException("The bit rates of video rendition " + rendition.Key + " must be positive.", nameof(rendition));
                }

                if (!IsAvcCodec(summary.Codec))
                {
                    throw new ArgumentException("The codec of video rendition " + rendition.Key + " is not valid: " + summary.Codec, nameof(rendition));
                }

                break;
            case HlsRenditionKind.Audio:
                if (summary.SourceIndex != target.SourceIndex)
                {
                    throw new ArgumentException("The stream index of audio rendition " + rendition.Key + " differs from the job.", nameof(rendition));
                }

                if (summary.Channels is < 1 or > 2 || summary.Bandwidth <= 0)
                {
                    throw new ArgumentException("The channels or bit rate of audio rendition " + rendition.Key + " are not valid.", nameof(rendition));
                }

                break;
            case HlsRenditionKind.Subtitle:
                if (summary.SourceIndex != target.SourceIndex)
                {
                    throw new ArgumentException("The stream index of subtitle rendition " + rendition.Key + " differs from the job.", nameof(rendition));
                }

                break;
        }
    }

    // "avc1." followed by exactly six hexadecimal digits (profile, constraints, level).
    private static bool IsAvcCodec(string? codec)
        => codec is { Length: 11 } && codec.StartsWith("avc1.", StringComparison.Ordinal) && codec[5..].All(char.IsAsciiHexDigit);
}
