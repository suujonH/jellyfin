using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.Hls;
using MediaBrowser.Model.Hls;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Hls;

public sealed class LocalHlsStoreTests : IDisposable
{
    private const string Published = "0123456789abcdef0123456789abcdef/video/1080/fedcba9876543210fedcba9876543210";
    private const string Other = "0123456789abcdef0123456789abcdef/audio/1/00112233445566778899aabbccddeeff";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jellyfin-hls-store-tests", Guid.NewGuid().ToString("N"));

    public LocalHlsStoreTests()
    {
        Directory.CreateDirectory(_directory);
    }

    private string Root => Path.Combine(_directory, "hls");

    private string Marker => Path.Combine(Root, ".jellyfin-hls");

    private LocalHlsStore Store => new(Root);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task CheckRoot_Missing_IsAProblem()
    {
        Assert.Equal("The HLS store does not exist or cannot be read: " + Root, await Store.CheckRootAsync([], CancellationToken));
        Assert.Equal("The HLS store does not exist or cannot be read: " + Root, await Store.CheckRootAsync([Published], CancellationToken));
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public async Task CheckRoot_EmptyWithNothingPublished_IsUsableAndStaysEmpty()
    {
        Directory.CreateDirectory(Root);

        Assert.Null(await Store.CheckRootAsync([], CancellationToken));

        // It may be a mount point that has to stay empty until it is mounted.
        Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
    }

    [Theory]
    [InlineData(".jellyfin-hls")]
    [InlineData(".distributed-hls")]
    public async Task CheckRoot_Marked_IsUsableWhateverIsPublished(string marker)
    {
        Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(Path.Combine(Root, marker), "kept", CancellationToken);

        // The directories may be gone for other reasons, such as being deleted by hand; that is for the verification to find.
        Assert.Null(await Store.CheckRootAsync([Published, Other], CancellationToken));
        Assert.Equal(["kept"], Directory.EnumerateFiles(Root).Select(File.ReadAllText));
    }

    [Fact]
    public async Task CheckRoot_PredatesTheMarker_GetsItWhenAPublishedDirectoryIsThere()
    {
        Directory.CreateDirectory(Full(Published));

        Assert.Null(await Store.CheckRootAsync([Other, Published], CancellationToken));
        Assert.Equal("Jellyfin HLS store. Do not delete.\n", await File.ReadAllTextAsync(Marker, CancellationToken));
    }

    [Fact]
    public async Task CheckRoot_UnmarkedWithoutAPublishedDirectory_IsAProblemAndIsNotMarked()
    {
        // What an empty mount point looks like while the mount is missing.
        Directory.CreateDirectory(Path.Combine(Root, "lost+found"));

        Assert.Equal(
            "The HLS store has no marker .jellyfin-hls and none of the published renditions; it may not be mounted: " + Root,
            await Store.CheckRootAsync([Published, Other], CancellationToken));
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public async Task CheckRoot_Unreadable_IsAProblem()
    {
        // EACCES: a protected mount point whose FUSE mount exited with mode 000. Windows and root have no such failure.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        Directory.CreateDirectory(Root);
        File.SetUnixFileMode(Root, UnixFileMode.None);
        try
        {
            Assert.StartsWith("The HLS store cannot be used: ", await Store.CheckRootAsync([], CancellationToken), StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public async Task ListAsync_Directory_ListsTheFilesUnderItRelativeToIt()
    {
        await WriteFileAsync(Published + "/segment-000000.m4s", 5);
        await WriteFileAsync(Published + "/init.mp4", 3);
        await WriteFileAsync(Published + "/sub/x", 1);
        await WriteFileAsync(Other + "/init.mp4", 2);

        var files = await Store.ListAsync(Published, CancellationToken);

        Assert.Equal(["init.mp4", "segment-000000.m4s", "sub/x"], files.Select(f => f.Path));
        Assert.Equal([3L, 5L, 1L], files.Select(f => f.Length));
        Assert.Equal(File.GetLastWriteTimeUtc(Full(Published + "/init.mp4")), files[0].LastModified);
        Assert.Empty(await Store.ListAsync(Published + "/missing", CancellationToken));
    }

    [Fact]
    public async Task ListAndDelete_ParentUnreadable_Throw()
    {
        // A directory that cannot be reached is not missing. Windows and root have no such failure.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        await WriteFileAsync(Published + "/init.mp4", 3);
        var media = Full("0123456789abcdef0123456789abcdef");
        File.SetUnixFileMode(media, UnixFileMode.None);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => Store.ListAsync(Published, CancellationToken));
            await Assert.ThrowsAsync<IOException>(() => Store.DeleteDirectoryAsync(Published, CancellationToken));
        }
        finally
        {
            File.SetUnixFileMode(media, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.True(File.Exists(Full(Published + "/init.mp4")));
    }

    [Fact]
    public async Task DeleteDirectory_LastOfItsKind_DeletesTheParentsLeftEmpty()
    {
        await WriteFileAsync(Published + "/init.mp4", 3);
        await WriteFileAsync(Other + "/init.mp4", 3);
        await File.WriteAllTextAsync(Marker, "kept", CancellationToken);

        await Store.DeleteDirectoryAsync(Published, CancellationToken);

        Assert.False(Directory.Exists(Full("0123456789abcdef0123456789abcdef/video")));
        Assert.True(File.Exists(Full(Other + "/init.mp4")));

        await Store.DeleteDirectoryAsync(Other, CancellationToken);

        Assert.Equal([Marker], Directory.EnumerateFileSystemEntries(Root));

        // Gone already.
        await Store.DeleteDirectoryAsync(Other, CancellationToken);
    }

    [Theory]
    [InlineData("...")]
    [InlineData(".. ")]
    public async Task DeleteDirectory_NameOfTheParentOnWindows_IsRefused(string directory)
    {
        await WriteFileAsync(Published + "/init.mp4", 3);

        await Assert.ThrowsAsync<ArgumentException>(() => Store.DeleteDirectoryAsync(directory, CancellationToken));

        Assert.True(File.Exists(Full(Published + "/init.mp4")));
    }

    [Fact]
    public async Task WriteAsync_File_ReplacesItThroughATemporaryFile()
    {
        var path = Published + "/init.mp4";
        await WriteFileAsync(path, 10);

        await Store.WriteAsync(path, new MemoryStream(Encoding.UTF8.GetBytes("new")), CancellationToken);

        Assert.Equal("new", await File.ReadAllTextAsync(Full(path), CancellationToken));
        Assert.Equal([Full(path)], Directory.EnumerateFileSystemEntries(Full(Published)));
    }

    [Fact]
    public async Task WriteAsync_UploadBroken_LeavesNothing()
    {
        Directory.CreateDirectory(Root);

        await Assert.ThrowsAsync<IOException>(() => Store.WriteAsync(Published + "/init.mp4", new BrokenStream(), CancellationToken));

        Assert.Empty(Directory.EnumerateFileSystemEntries(Full(Published)));
    }

    [Fact]
    public async Task WriteAsync_WithoutTheRoot_CreatesNothing()
    {
        // The root may be a mount point.
        await Assert.ThrowsAsync<IOException>(() => Store.WriteAsync(Published + "/init.mp4", new MemoryStream([1]), CancellationToken));

        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public void Addresses_File_IsReadByTheServerFromItsPath()
    {
        var path = Published + "/init.mp4";

        Assert.Equal(Full(path), Store.GetServerPath(path));
        Assert.Null(Store.GetUploadAddress(path, TimeSpan.FromHours(1)));
        Assert.Null(Store.GetClientAddress(path));
        Assert.Throws<ArgumentException>(() => Store.GetServerPath("../x"));
    }

    [Fact]
    public async Task TestAsync_WritableRoot_SucceedsAndLeavesNothing()
    {
        Directory.CreateDirectory(Root);

        Assert.Equal(new HlsStoreTestResult(true, false, null), await Store.TestAsync(CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Root));

        await File.WriteAllTextAsync(Marker, "kept", CancellationToken);
        Assert.Equal(new HlsStoreTestResult(true, true, null), await Store.TestAsync(CancellationToken));
    }

    [Fact]
    public async Task TestAsync_MissingRoot_FailsWithoutCreatingIt()
    {
        var result = await Store.TestAsync(CancellationToken);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(Root));
    }

    private string Full(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));

    private async Task WriteFileAsync(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Full(path))!);
        await File.WriteAllBytesAsync(Full(path), new byte[length], CancellationToken);
    }

    // An upload whose connection is lost.
    private sealed class BrokenStream : MemoryStream
    {
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            => Task.FromException(new IOException("The connection was reset."));
    }
}
