using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Hls;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// The HLS store in a local directory, which may be a mount point. Workers write it over SFTP; the server only reads and deletes.
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
    public Task<string?> CheckRootAsync(IReadOnlyCollection<string> publishedDirectories, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publishedDirectories);
        var paths = publishedDirectories.Select(GetFullPath).ToArray();
        try
        {
            // Directory.Exists answers false for a root that cannot be read: EACCES of a protected mount point, ENOTCONN of a
            // dropped FUSE mount, ESTALE of NFS.
            if (!Directory.Exists(_root))
            {
                return Task.FromResult<string?>("The HLS store does not exist or cannot be read: " + _root);
            }

            // A real read: EACCES and ENOTCONN surface here, not in Directory.Exists.
            _ = Directory.EnumerateFileSystemEntries(_root).Any();

            // Neither marked nor holding a published directory: most likely the mount point without the mount.
            return Task.FromResult(HasMarker() || paths.Length == 0 || paths.Any(Directory.Exists)
                ? null
                : "The HLS store has no marker " + HlsStorePaths.MarkerName + " and none of the published renditions; it may not be mounted: " + _root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<string?>("The HLS store cannot be used: " + ex.Message);
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
    public string? GetServerPath(string path) => GetFullPath(path);

    /// <inheritdoc />
    public Uri? GetClientAddress(string path)
    {
        HlsStorePaths.Validate(path, nameof(path));
        return null;
    }

    /// <inheritdoc />
    public TimeSpan GetClientAddressLifetime() => TimeSpan.Zero;

    /// <inheritdoc />
    public Task<bool> HasMarkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(HasMarker());
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(ex.Message, ex);
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
