using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.Hls;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Hls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Jellyfin.Server.Implementations.Tests.Hls.HlsTestEnvironment;

namespace Jellyfin.Server.Implementations.Tests.Hls;

public sealed class HlsLibraryWriterTests : IDisposable
{
    private readonly HlsTestEnvironment _environment = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly string _movies;
    private readonly string _shows;
    private readonly HlsLibraryWriter _writer;

    public HlsLibraryWriterTests()
    {
        _movies = Directory.CreateDirectory(Path.Combine(_environment.TempDirectory, "movies")).FullName;
        _shows = Directory.CreateDirectory(Path.Combine(_environment.TempDirectory, "shows")).FullName;
        SetLocations(_movies);
        _writer = new HlsLibraryWriter(_libraryManager.Object, _environment.Store, NullLogger<HlsLibraryWriter>.Instance);
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _writer.Dispose();
        _environment.Dispose();
    }

    [Fact]
    public async Task AFile_IsWrittenIntoTheOnlyFolder_ReplacesWhatIsThere_AndIsRecorded()
    {
        var entry = Directory.CreateDirectory(Path.Combine(_movies, "Film", "__true_cache__", "a.nfo")).FullName;

        await PutAsync("Film/a.nfo", "first");
        await PutAsync("Film/a.nfo", "second");

        var path = Path.Combine(_movies, "Film", "a.nfo");
        Assert.Equal("second", await File.ReadAllTextAsync(path, CancellationToken));
        Assert.False(Directory.Exists(entry));
        Assert.Equal([path], Directory.GetFileSystemEntries(Path.Combine(_movies, "Film")).Where(p => !p.EndsWith("__true_cache__", StringComparison.Ordinal)));
        await using var context = _environment.CreateDbContext();
        var file = Assert.Single(await context.HlsLibraryFiles.AsNoTracking().ToListAsync(CancellationToken));
        Assert.Equal((LibraryId, path, 6L, (Guid?)WorkerId), (file.LibraryId, file.Path, file.Length, file.WorkerId));
    }

    [Fact]
    public async Task ALibraryWithSeveralFolders_IsWrittenToTheOneNamed()
    {
        SetLocations(_movies, _shows);

        await Assert.ThrowsAsync<ArgumentException>(() => StartAsync("a.nfo", 1));
        await Assert.ThrowsAsync<ArgumentException>(() => StartAsync("a.nfo", 1, Path.Combine(_environment.TempDirectory, "other")));
        await Assert.ThrowsAsync<ArgumentException>(() => _writer.StartAsync(OtherLibraryId, new HlsLibraryUploadStart(null, "a.nfo", 1, WorkerId), CancellationToken));
        await PutAsync("a.nfo", "x", _shows + Path.DirectorySeparatorChar);

        Assert.True(File.Exists(Path.Combine(_shows, "a.nfo")));
        Assert.False(File.Exists(Path.Combine(_movies, "a.nfo")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../a.nfo")]
    [InlineData("Film/../../a.nfo")]
    [InlineData("Film/./a.nfo")]
    [InlineData("Film//a.nfo")]
    [InlineData(".hidden/a.nfo")]
    [InlineData("Film/.a.nfo")]
    [InlineData("Film./a.nfo")]
    [InlineData("Film /a.nfo")]
    [InlineData("Film/__TRUE_CACHE__/a.nfo")]
    [InlineData("Film/__working__/a.nfo")]
    [InlineData("/srv/a.nfo")]
    [InlineData("Film\\a.nfo")]
    [InlineData("C:/a.nfo")]
    [InlineData("Film/a\n.nfo")]
    public async Task APathOutsideTheFolder_OrOfAHiddenFile_IsRefused(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => StartAsync(path, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => _writer.StartAsync(LibraryId, new HlsLibraryUploadStart(null, "a.nfo", 1, Guid.Empty), CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => StartAsync("a.nfo", -1));
    }

    [Fact]
    public async Task Chunks_FollowWhatWasWritten_AndOneSentAgainReplacesWhatFollowsIt()
    {
        var id = await StartAsync("a.nfo", 6);

        await Assert.ThrowsAsync<ArgumentException>(() => WriteAsync(id, 1, "bc"));
        Assert.True(await WriteAsync(id, 0, "abc"));
        Assert.True(await WriteAsync(id, 3, "dxyz"[..2]));
        Assert.True(await WriteAsync(id, 3, "DE"));
        await Assert.ThrowsAsync<ArgumentException>(() => WriteAsync(id, 5, "FG"));
        Assert.True(await WriteAsync(id, 5, "F"));
        Assert.True(await _writer.CompleteAsync(id, new HlsLibraryUploadComplete(null), CancellationToken));

        Assert.Equal("abcDEF", await File.ReadAllTextAsync(Path.Combine(_movies, "a.nfo"), CancellationToken));
        Assert.False(await WriteAsync(id, 6, string.Empty));
        Assert.False(await _writer.CompleteAsync(id, new HlsLibraryUploadComplete(null), CancellationToken));
        Assert.False(await _writer.CompleteAsync("unknown", new HlsLibraryUploadComplete(null), CancellationToken));
    }

    [Fact]
    public async Task AnUploadNotComplete_EndsWithoutAFile()
    {
        var id = await StartAsync("a.nfo", 6);
        Assert.True(await WriteAsync(id, 0, "abc"));

        await Assert.ThrowsAsync<ArgumentException>(() => _writer.CompleteAsync(id, new HlsLibraryUploadComplete(null), CancellationToken));

        Assert.False(await WriteAsync(id, 3, "def"));
        Assert.Empty(Directory.GetFileSystemEntries(_movies));
    }

    [Fact]
    public async Task Abort_DropsWhatWasWritten()
    {
        var id = await StartAsync("Film/a.nfo", 6);
        Assert.True(await WriteAsync(id, 0, "abc"));

        Assert.True(await _writer.AbortAsync(id));

        Assert.False(await _writer.AbortAsync(id));
        Assert.Empty(Directory.GetFiles(Path.Combine(_movies, "Film")));
    }

    [Fact]
    public async Task AShortcut_PutsItsEntryInPlace_ReplacingTheOldOne_ThenTheStrmFile()
    {
        var old = Directory.CreateDirectory(Path.Combine(_movies, "Film", "__true_cache__", "a.strm")).FullName;
        await File.WriteAllTextAsync(Path.Combine(old, "old.json"), "{}", CancellationToken);
        var zip = CreateZip(("manifest.json", "{}"), ("images/primary.jpg", "jpg"));

        await PutShortcutAsync("Film/a.strm", zip, "s3://bucket/Film/a.mkv");

        Assert.Equal("s3://bucket/Film/a.mkv\n", await File.ReadAllTextAsync(Path.Combine(_movies, "Film", "a.strm"), CancellationToken));
        var entries = Path.Combine(_movies, "Film", "__true_cache__");
        Assert.Equal([Path.Combine(entries, "a.strm")], Directory.GetFileSystemEntries(entries));
        Assert.Equal(
            new[] { Path.Combine(entries, "a.strm", "images", "primary.jpg"), Path.Combine(entries, "a.strm", "manifest.json") }.Order(StringComparer.Ordinal),
            Directory.GetFiles(Path.Combine(entries, "a.strm"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        Assert.Equal([Path.Combine(_movies, "Film", "a.strm")], Directory.GetFiles(Path.Combine(_movies, "Film")));
        await using var context = _environment.CreateDbContext();
        Assert.Equal(3, await context.HlsLibraryFiles.CountAsync(CancellationToken));
    }

    [Fact]
    public async Task AShortcut_WithAnEntryLeavingItsDirectory_OrATargetThatIsNotRemote_IsRefused()
    {
        var evil = CreateZip(("../../evil.txt", "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => PutShortcutAsync("Film/a.strm", evil, "s3://bucket/a.mkv"));
        var zip = CreateZip(("manifest.json", "{}"));
        await Assert.ThrowsAsync<ArgumentException>(() => PutShortcutAsync("Film/a.strm", zip, "file:///etc/passwd"));
        await Assert.ThrowsAsync<ArgumentException>(() => PutShortcutAsync("Film/a.strm", zip, "s3://bucket/a.mkv\nhttps://x"));
        await Assert.ThrowsAsync<ArgumentException>(() => PutShortcutAsync("Film/a.mkv", zip, "s3://bucket/a.mkv"));

        Assert.Empty(Directory.GetFiles(_environment.TempDirectory, "evil.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Path.Combine(_movies, "Film")));
        Assert.False(Directory.Exists(Path.Combine(_movies, "Film", "__true_cache__")));
    }

    [Fact]
    public async Task AShortcutOnAWorker_NamesTheFileOnTheWorker()
    {
        var target = HlsWorkerLocalAddress.Format(WorkerId, @"D:\Movies\a.mkv");

        await PutShortcutAsync("a.strm", CreateZip(("manifest.json", "{}")), target);

        Assert.Equal(target + "\n", await File.ReadAllTextAsync(Path.Combine(_movies, "a.strm"), CancellationToken));
    }

    [Fact]
    public async Task AClaim_MarksTheFile_AndGivesTheOperationThatHeldIt()
    {
        var first = new string('a', 32);
        var second = new string('b', 32);

        Assert.Null(await _writer.ClaimWorkingAsync(LibraryId, new HlsWorkingClaim(null, "Film/Film.strm", first), CancellationToken));
        Assert.Equal(first, await _writer.ClaimWorkingAsync(LibraryId, new HlsWorkingClaim(null, "Film/Film.strm", second), CancellationToken));

        var marker = Path.Combine(_movies, "Film", "__working__", "Film.strm");
        Assert.Equal(second + "\n", await File.ReadAllTextAsync(marker, CancellationToken));
        Assert.Equal([marker], Directory.GetFileSystemEntries(Path.GetDirectoryName(marker)!));
        Assert.Equal(second, await _writer.GetWorkingAsync(LibraryId, new HlsWorkingPath(null, "Film/Film.strm"), CancellationToken));
        Assert.Null(await _writer.GetWorkingAsync(LibraryId, new HlsWorkingPath(null, "Film/Other.strm"), CancellationToken));
    }

    [Fact]
    public async Task ARelease_RemovesTheMark_OnlyOfTheOperationThatHoldsIt()
    {
        var holder = new string('a', 32);
        await _writer.ClaimWorkingAsync(LibraryId, new HlsWorkingClaim(null, "Film.strm", holder), CancellationToken);

        await _writer.ReleaseWorkingAsync(LibraryId, new HlsWorkingRelease(null, "Film.strm", new string('b', 32)), CancellationToken);
        Assert.Equal(holder, await _writer.GetWorkingAsync(LibraryId, new HlsWorkingPath(null, "Film.strm"), CancellationToken));

        await _writer.ReleaseWorkingAsync(LibraryId, new HlsWorkingRelease(null, "Film.strm", holder), CancellationToken);
        Assert.Null(await _writer.GetWorkingAsync(LibraryId, new HlsWorkingPath(null, "Film.strm"), CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(_movies, "__working__")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    public async Task AnOperationId_IsOf32LowerCaseHexadecimalCharacters(string id)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _writer.ClaimWorkingAsync(LibraryId, new HlsWorkingClaim(null, "Film.strm", id), CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => _writer.ReleaseWorkingAsync(LibraryId, new HlsWorkingRelease(null, "Film.strm", id), CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => _writer.ClaimWorkingAsync(LibraryId, new HlsWorkingClaim(null, "__working__/Film.strm", new string('a', 32)), CancellationToken));
    }

    [Fact]
    public async Task AFileOfAnOperation_IsPutInPlace_OnlyWhileTheOperationHoldsIt()
    {
        var holder = new string('a', 32);
        await _writer.ClaimWorkingAsync(LibraryId, new HlsWorkingClaim(null, "a.nfo", holder), CancellationToken);

        var id = await StartAsync("a.nfo", 1);
        Assert.True(await WriteAsync(id, 0, "x"));
        Assert.True(await _writer.CompleteAsync(id, new HlsLibraryUploadComplete(null, holder), CancellationToken));
        Assert.Equal("x", await File.ReadAllTextAsync(Path.Combine(_movies, "a.nfo"), CancellationToken));

        id = await StartAsync("a.nfo", 1);
        Assert.True(await WriteAsync(id, 0, "y"));
        await Assert.ThrowsAsync<HlsWorkingTakenException>(() => _writer.CompleteAsync(id, new HlsLibraryUploadComplete(null, new string('b', 32)), CancellationToken));

        Assert.Equal("x", await File.ReadAllTextAsync(Path.Combine(_movies, "a.nfo"), CancellationToken));
        Assert.Equal(["__working__", "a.nfo"], Directory.GetFileSystemEntries(_movies).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.False(await WriteAsync(id, 1, string.Empty));
    }

    private void SetLocations(params string[] locations)
        => _libraryManager.Setup(l => l.GetVirtualFolders()).Returns([new VirtualFolderInfo { ItemId = LibraryId.ToString("N"), Name = "Movies", Locations = locations }]);

    private Task<string> StartAsync(string path, long length, string? location = null)
        => _writer.StartAsync(LibraryId, new HlsLibraryUploadStart(location, path, length, WorkerId), CancellationToken);

    private async Task<bool> WriteAsync(string id, long offset, string text)
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return await _writer.WriteAsync(id, offset, body, CancellationToken);
    }

    private async Task PutAsync(string path, string text, string? location = null)
    {
        var id = await StartAsync(path, Encoding.UTF8.GetByteCount(text), location);
        Assert.True(await WriteAsync(id, 0, text));
        Assert.True(await _writer.CompleteAsync(id, new HlsLibraryUploadComplete(null), CancellationToken));
    }

    private async Task PutShortcutAsync(string path, byte[] zip, string target)
    {
        var id = await StartAsync(path, zip.Length);
        using (var body = new MemoryStream(zip))
        {
            Assert.True(await _writer.WriteAsync(id, 0, body, CancellationToken));
        }

        Assert.True(await _writer.CompleteAsync(id, new HlsLibraryUploadComplete(target), CancellationToken));
    }

    private static byte[] CreateZip(params (string Name, string Text)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (name, text) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
        }

        return stream.ToArray();
    }
}
