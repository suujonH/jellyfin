using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Hls;

/// <summary>
/// A root that HLS renditions are kept under: a local directory, or a prefix of network storage. Workers write it with their
/// own credentials; the server only reads and deletes in it. Paths are relative to the root, with forward slashes, such as <c>{media}/video/1080/{token}/init.mp4</c>; <c>.</c> and <c>..</c>
/// are not allowed in them.
/// </summary>
public interface IHlsStore
{
    /// <summary>
    /// Gets the root in its normal form: an absolute local path, or <c>s3://bucket/prefix</c>.
    /// </summary>
    string Location { get; }

    /// <summary>
    /// Gets a value indicating whether the store is a local directory, which the server sends files of from <see cref="GetServerPath"/>.
    /// </summary>
    bool IsLocal { get; }

    /// <summary>
    /// Checks whether the root can be trusted before anything is decided from a file missing in it. A root that cannot be
    /// read, such as a mount point without its mount or a bucket the access no longer reaches, looks empty. The root has to
    /// carry the marker, which the worker writes, or at least one of the directories published last, unless nothing is published
    /// in it yet; the server never writes the marker. A network store also needs an access that is not an archive.
    /// </summary>
    /// <param name="publishedDirectories">Some of the directories published last.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>null</c> when the root can be trusted, otherwise why not.</returns>
    Task<string?> CheckRootAsync(IReadOnlyCollection<string> publishedDirectories, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the files under a directory, in its subdirectories too.
    /// </summary>
    /// <param name="directory">The directory.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The files; none when there is no such directory.</returns>
    /// <exception cref="IOException">The store cannot be read.</exception>
    Task<IReadOnlyList<HlsStoreFile>> ListAsync(string directory, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a directory and the files under it; in a local store, also the parents it leaves empty, up to the root.
    /// A directory that does not exist is deleted already.
    /// </summary>
    /// <param name="directory">The directory.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="IOException">The store cannot be written.</exception>
    Task DeleteDirectoryAsync(string directory, CancellationToken cancellationToken);

    /// <summary>
    /// Gets where the server reads a file to send it to a client: a local path for a local store, otherwise an http
    /// address, which is the external address when the storage is published at one, as for media.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The path or address, or <c>null</c> when no access covers the store.</returns>
    string? GetServerPath(string path);

    /// <summary>
    /// Gets the address a client reads a file at directly.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The address, or <c>null</c> when the store is local or its storage is not public, and the server sends the file.</returns>
    Uri? GetClientAddress(string path);

    /// <summary>
    /// Gets whether the root carries the marker, under its current or its legacy name.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether it does.</returns>
    /// <exception cref="IOException">The store cannot be read.</exception>
    Task<bool> HasMarkerAsync(CancellationToken cancellationToken);
}
