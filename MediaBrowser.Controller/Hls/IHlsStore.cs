using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Hls;

namespace MediaBrowser.Controller.Hls;

/// <summary>
/// Where published HLS renditions are kept: a local directory, or a prefix of network storage. Paths are relative to the
/// root of the store, with forward slashes, such as <c>{media}/video/1080/{token}/init.mp4</c>; <c>.</c> and <c>..</c>
/// are not allowed in them.
/// </summary>
public interface IHlsStore
{
    /// <summary>
    /// Gets the store as it is configured: an absolute local path, or <c>s3://bucket/prefix</c>.
    /// </summary>
    string Location { get; }

    /// <summary>
    /// Gets a value indicating whether the store is a local directory, which Workers upload to through the server and the
    /// server sends files of from <see cref="GetServerPath"/>.
    /// </summary>
    bool IsLocal { get; }

    /// <summary>
    /// Checks whether the root can be trusted before anything is decided from a file missing in it. A root that cannot be
    /// read, such as a mount point without its mount or a bucket the access no longer reaches, looks empty. The root has to
    /// carry the marker, or at least one of the directories published last, unless nothing is published yet; the marker is
    /// written once such a directory is found. A network store also needs an access that is not an archive.
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
    /// Writes a file a Worker uploads through the server, to a temporary file next to it that then replaces it. Only a local
    /// store is written so.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="content">The content.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="IOException">The store cannot be written.</exception>
    /// <exception cref="NotSupportedException">The store is network storage, which Workers upload to directly.</exception>
    Task WriteAsync(string path, Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the address a Worker uploads a file to with PUT, straight to the storage.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="lifetime">How long the address stays valid.</param>
    /// <returns>The address, or <c>null</c> when the store is local or no access covers it.</returns>
    Uri? GetUploadAddress(string path, TimeSpan lifetime);

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
    /// Tests that a test object can be written, read and deleted in the root, and whether the root carries the marker.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    Task<HlsStoreTestResult> TestAsync(CancellationToken cancellationToken);
}
