using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Model.Hls;
using MediaBrowser.Model.IO;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// The HLS store in a local directory, which may be a mount point.
/// </summary>
internal sealed class LocalHlsStore : IHlsStore
{
    private readonly string _root;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalHlsStore"/> class.
    /// </summary>
    /// <param name="root">The directory, an absolute path.</param>
    public LocalHlsStore(string root)
    {
        _root = root;
    }

    /// <inheritdoc />
    public string Location => _root;

    /// <inheritdoc />
    public bool IsLocal => true;

    /// <inheritdoc />
    public async Task<string?> CheckRootAsync(IReadOnlyCollection<string> publishedDirectories, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publishedDirectories);
        var paths = publishedDirectories.Select(GetFullPath).ToArray();
        try
        {
            // Directory.Exists answers false for a root that cannot be read: EACCES of a protected mount point, ENOTCONN of a
            // dropped FUSE mount, ESTALE of NFS.
            if (!Directory.Exists(_root))
            {
                return "The HLS store does not exist or cannot be read: " + _root;
            }

            // A real read: EACCES and ENOTCONN surface here, not in Directory.Exists.
            _ = Directory.EnumerateFileSystemEntries(_root).Any();

            // Nothing published yet: usable, but nothing is written in it, since it may be a mount point that has to stay
            // empty until it is mounted.
            if (HasMarker() || paths.Length == 0)
            {
                return null;
            }

            // Neither marked nor holding a published directory: most likely the mount point without the mount, which must
            // not get a marker.
            if (!paths.Any(Directory.Exists))
            {
                return "The HLS store has no marker " + HlsStorePaths.MarkerName + " and none of the published renditions; it may not be mounted: " + _root;
            }

            // The store itself, checked the first time since something was published in it, or a store that predates the marker.
            await File.WriteAllTextAsync(Path.Combine(_root, HlsStorePaths.MarkerName), HlsStorePaths.MarkerContent, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The HLS store cannot be used: " + ex.Message;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HlsStoreFile>> ListAsync(string directory, CancellationToken cancellationToken)
    {
        var path = GetFullPath(directory);

        // Directory.Exists would answer false for a directory that cannot be read too; only one that is not found is missing.
        try
        {
            IReadOnlyList<HlsStoreFile> files = new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Select(file => new HlsStoreFile(
                    Path.GetRelativePath(path, file.FullName).Replace(Path.DirectorySeparatorChar, '/'),
                    file.Length,
                    file.LastWriteTimeUtc))
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .ToArray();
            return Task.FromResult(files);
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult<IReadOnlyList<HlsStoreFile>>([]);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public Task DeleteDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        var path = GetFullPath(directory);

        // As in ListAsync, only a directory that is not found is deleted already.
        try
        {
            Directory.Delete(path, true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(ex.Message, ex);
        }

        // Then the parents, such as {media}/{kind}/{index}, {media}/{kind} and {media}, each only while empty.
        var parts = directory.Split('/');
        for (var depth = parts.Length - 1; depth >= 1; depth--)
        {
            TryDeleteEmpty(GetFullPath(string.Join('/', parts[..depth])));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task WriteAsync(string path, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var fullPath = GetFullPath(path);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, IODefaults.FileStreamBufferSize, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, true);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(ex.Message, ex);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    /// <inheritdoc />
    public Uri? GetUploadAddress(string path, TimeSpan lifetime)
    {
        // Workers upload to the server, which writes the files.
        HlsStorePaths.Validate(path, nameof(path));
        return null;
    }

    /// <inheritdoc />
    public string? GetServerPath(string path) => GetFullPath(path);

    /// <inheritdoc />
    public Uri? GetClientAddress(string path)
    {
        HlsStorePaths.Validate(path, nameof(path));
        return null;
    }

    /// <inheritdoc />
    public async Task<HlsStoreTestResult> TestAsync(CancellationToken cancellationToken)
    {
        // The root is not created: it may be a mount point.
        if (!Directory.Exists(_root))
        {
            return new HlsStoreTestResult(false, false, "The HLS store does not exist or cannot be read: " + _root);
        }

        var hasMarker = false;
        var path = Path.Combine(_root, HlsStorePaths.TestNamePrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            hasMarker = HasMarker();
            var content = Guid.NewGuid().ToByteArray();
            await File.WriteAllBytesAsync(path, content, cancellationToken).ConfigureAwait(false);
            var read = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            File.Delete(path);
            return read.AsSpan().SequenceEqual(content)
                ? new HlsStoreTestResult(true, hasMarker, null)
                : new HlsStoreTestResult(false, hasMarker, "The test file read back is not what was written.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HlsStoreTestResult(false, hasMarker, ex.Message);
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    private static void TryDeleteEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A parent that is not empty, or just got a new entry, stays.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; the name tells what it is.
        }
    }

    private bool HasMarker()
        => File.Exists(Path.Combine(_root, HlsStorePaths.MarkerName)) || File.Exists(Path.Combine(_root, HlsStorePaths.LegacyMarkerName));

    private string GetFullPath(string path)
    {
        HlsStorePaths.Validate(path, nameof(path));

        // Whatever the file system makes of a name, the path stays below the root: deleting the root would lose everything.
        var fullPath = Path.GetFullPath(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
        return fullPath.Length > root.Length && fullPath.StartsWith(root, StringComparison.Ordinal)
            ? fullPath
            : throw new ArgumentException("The path leaves the HLS store: " + path, nameof(path));
    }
}
