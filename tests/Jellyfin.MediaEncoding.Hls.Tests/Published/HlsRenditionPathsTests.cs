using System;
using System.Linq;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Model.Hls;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Published;

public class HlsRenditionPathsTests
{
    private const string Sha = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
    private const string Media = "00112233445566778899aabbccddeeff";
    private const string Attempt = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(Sha)]
    [InlineData("00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF")]
    public void GetMediaKey_Hash_IsItsFirstHalfInLowerCase(string sha)
        // Published directories are named after it, as the plugin named them: changing it orphans every published rendition.
        => Assert.Equal(Media, HlsRenditionPaths.GetMediaKey(sha));

    [Fact]
    public void GetMediaKey_NoHash_Throws()
        => Assert.Throws<ArgumentException>(() => HlsRenditionPaths.GetMediaKey(Media));

    [Fact]
    public void GetRelativeDirectory_EachKind_FollowsTheLayout()
    {
        Assert.Equal($"{Media}/video/1080/{Attempt}", HlsRenditionPaths.GetRelativeDirectory(Media, new("v1080", HlsRenditionKind.Video, new HlsVideoQuality(1080, 1080, 1920, 25), -1, 0), Attempt));
        Assert.Equal($"{Media}/audio/3/{Attempt}", HlsRenditionPaths.GetRelativeDirectory(Media, new("a3", HlsRenditionKind.Audio, null, 3, 2), Attempt));
        Assert.Equal($"{Media}/subtitles/12/{Attempt}", HlsRenditionPaths.GetRelativeDirectory(Media, new("s12", HlsRenditionKind.Subtitle, null, 12, 0), Attempt));
        Assert.Equal($"{Media}/preview/{Attempt}", HlsRenditionPaths.GetRelativeDirectory(Media, new("preview", HlsRenditionKind.Preview, null, -1, 0), Attempt));
    }

    [Fact]
    public void GetRelativeDirectory_SpecThatCannotBePlaced_Throws()
    {
        Assert.Throws<ArgumentException>(() => HlsRenditionPaths.GetRelativeDirectory(Media, new("v1080", HlsRenditionKind.Video, null, -1, 0), Attempt));
        Assert.Throws<ArgumentOutOfRangeException>(() => HlsRenditionPaths.GetRelativeDirectory(Media, new("x", (HlsRenditionKind)99, null, -1, 0), Attempt));
    }

    [Fact]
    public void GetRelativeDirectory_EveryPlannedRendition_IsValid()
    {
        var specs = HlsEncodingLadder.Heights.Select(h => new HlsRenditionSpec("v" + h, HlsRenditionKind.Video, new HlsVideoQuality(h, h, h * 16 / 9, 25), -1, 0))
            .Append(new("a0", HlsRenditionKind.Audio, null, 0, 2))
            .Append(new("a999", HlsRenditionKind.Audio, null, 999, 2))
            .Append(new("s7", HlsRenditionKind.Subtitle, null, 7, 0))
            .Append(new("preview", HlsRenditionKind.Preview, null, -1, 0));

        Assert.All(specs, spec => Assert.True(HlsRenditionPaths.IsValidRelativeDirectory(HlsRenditionPaths.GetRelativeDirectory(Media, spec, Attempt)), spec.Key));
    }

    [Theory]
    [InlineData($"{Media}/video/1080/{Attempt}")]
    [InlineData($"{Media}/video/144/{Attempt}")]
    [InlineData($"{Media}/video/2160/{Attempt}")]
    [InlineData($"{Media}/audio/0/{Attempt}")]
    [InlineData($"{Media}/audio/123/{Attempt}")]
    [InlineData($"{Media}/subtitles/5/{Attempt}")]
    [InlineData($"{Media}/preview/{Attempt}")]
    public void IsValidRelativeDirectory_DirectoryOfTheLayout_IsAccepted(string directory)
        => Assert.True(HlsRenditionPaths.IsValidRelativeDirectory(directory));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("00112233445566778899AABBCCDDEEFF/preview/0123456789abcdef0123456789abcdef")] // Upper case.
    [InlineData("00112233445566778899aabbccddeef/preview/0123456789abcdef0123456789abcdef")] // 31 characters.
    [InlineData("00112233445566778899aabbccddeeff0/preview/0123456789abcdef0123456789abcdef")] // 33 characters.
    [InlineData($"{Media}/preview/0123456789abcdef0123456789abcde")]
    [InlineData($"{Media}/preview/0123456789abcdef0123456789abcdef0")]
    [InlineData($"{Media}/preview/0123456789ABCDEF0123456789abcdef")]
    [InlineData($"{Media}/preview/0123456789abcdeg0123456789abcdef")]
    [InlineData($"{Media}/video/12/{Attempt}")] // A quality has 3 or 4 digits.
    [InlineData($"{Media}/video/12345/{Attempt}")]
    [InlineData($"{Media}/video/abc/{Attempt}")]
    [InlineData($"{Media}/audio/1000/{Attempt}")] // A stream index has at most 3 digits.
    [InlineData($"{Media}/audio//{Attempt}")]
    [InlineData($"{Media}/thumbs/1/{Attempt}")]
    [InlineData($"{Media}/preview")]
    [InlineData($"{Media}/video/1080")]
    [InlineData($"{Media}/video/1080/{Attempt}/x")]
    [InlineData($"{Media}/video/1080/{Attempt}/..")]
    [InlineData($"../{Media}/video/1080/{Attempt}")]
    [InlineData($"/{Media}/preview/{Attempt}")]
    [InlineData($"{Media}/preview/{Attempt}/")]
    [InlineData($"{Media}\\preview\\{Attempt}")]
    [InlineData($"{Media}/preview/{Attempt}\n")] // "$" would let a trailing line feed through.
    [InlineData($"{Media}/video/١٠٨٠/{Attempt}")] // "\d" would accept other digits.
    public void IsValidRelativeDirectory_AnythingElse_IsRefused(string? directory)
        => Assert.False(HlsRenditionPaths.IsValidRelativeDirectory(directory));

    [Theory]
    [InlineData(0, "segment-000000.m4s")]
    [InlineData(17, "segment-000017.m4s")]
    [InlineData(999999, "segment-999999.m4s")]
    public void GetSegmentFile_Index_IsReadBack(int index, string name)
    {
        Assert.Equal(name, HlsRenditionPaths.GetSegmentFile(index));
        Assert.True(HlsRenditionPaths.TryGetSegmentIndex(name, out var read));
        Assert.Equal(index, read);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("segment-00001.m4s")]
    [InlineData("segment-0000001.m4s")]
    [InlineData("segment-00000a.m4s")]
    [InlineData("segment-+00001.m4s")]
    [InlineData("Segment-000001.m4s")]
    [InlineData("segment-000001.M4S")]
    [InlineData("segment-000001.ts")]
    [InlineData("segment-١٠٠٠٠٠.m4s")]
    public void TryGetSegmentIndex_OtherName_IsNoSegment(string? name)
        => Assert.False(HlsRenditionPaths.TryGetSegmentIndex(name, out _));

    [Theory]
    [InlineData(HlsRenditionKind.Video, "init.mp4", true)]
    [InlineData(HlsRenditionKind.Video, "segment-000000.m4s", true)]
    [InlineData(HlsRenditionKind.Video, "segment-000010.m4s", true)] // One more than expected.
    [InlineData(HlsRenditionKind.Video, "segment-000011.m4s", false)]
    [InlineData(HlsRenditionKind.Video, "index.m3u8", false)] // The server writes the playlists.
    [InlineData(HlsRenditionKind.Audio, "init.mp4", true)]
    [InlineData(HlsRenditionKind.Audio, "subtitle.vtt", false)]
    [InlineData(HlsRenditionKind.Subtitle, "subtitle.vtt", true)]
    [InlineData(HlsRenditionKind.Subtitle, "segment-000000.m4s", false)]
    [InlineData(HlsRenditionKind.Preview, "preview.jpg", true)]
    [InlineData(HlsRenditionKind.Preview, "PREVIEW.JPG", false)]
    [InlineData((HlsRenditionKind)99, "preview.jpg", false)]
    public void IsAllowedFile_Name_DependsOnTheKind(HlsRenditionKind kind, string name, bool allowed)
        => Assert.Equal(allowed, HlsRenditionPaths.IsAllowedFile(kind, name, 10));

    [Theory]
    [InlineData(HlsRenditionKind.Video, "init.mp4")]
    [InlineData(HlsRenditionKind.Audio, "init.mp4")]
    [InlineData(HlsRenditionKind.Subtitle, "subtitle.vtt")]
    [InlineData(HlsRenditionKind.Preview, "preview.jpg")]
    public void GetRequiredFiles_Kind_ListsItsFiles(HlsRenditionKind kind, string expected)
        => Assert.Equal(expected, string.Join(',', HlsRenditionPaths.GetRequiredFiles(kind)));

    [Fact]
    public void GetRequiredFiles_UnknownKind_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => HlsRenditionPaths.GetRequiredFiles((HlsRenditionKind)99));
}
