using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Hls;

namespace MediaBrowser.Controller.Hls;

/// <summary>
/// Writes the files HLS workers add to libraries into the library folders: a file is uploaded in chunks to a hidden file next to
/// where it goes, and moved there once complete. A .strm file is uploaded as the zip of its TrueCache entry, which is put in place
/// before the .strm file. A worker marks a file it adds as held by one of its operations in a <c>__working__</c> folder next to
/// it. A request that does not make sense throws <see cref="ArgumentException"/>.
/// </summary>
public interface IHlsLibraryWriter
{
    /// <summary>
    /// Starts writing a file into a library folder.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="request">The file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The id of the upload.</returns>
    Task<string> StartAsync(Guid libraryId, HlsLibraryUploadStart request, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a chunk of a file: at the end of what was written, or again at an earlier offset, which drops what follows it.
    /// </summary>
    /// <param name="uploadId">The upload.</param>
    /// <param name="offset">Where the chunk goes in the file.</param>
    /// <param name="body">The chunk, at most 16 MiB.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the upload exists.</returns>
    Task<bool> WriteAsync(string uploadId, long offset, Stream body, CancellationToken cancellationToken);

    /// <summary>
    /// Ends an upload that has all of its file and puts the file in place; the upload ends even when this fails.
    /// </summary>
    /// <param name="uploadId">The upload.</param>
    /// <param name="request">The target of the .strm file, or none for a plain file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the upload exists.</returns>
    /// <exception cref="HlsWorkingTakenException">The request names an operation that does not hold the file; the upload ends.</exception>
    Task<bool> CompleteAsync(string uploadId, HlsLibraryUploadComplete request, CancellationToken cancellationToken);

    /// <summary>
    /// Ends an upload and drops what was written.
    /// </summary>
    /// <param name="uploadId">The upload.</param>
    /// <returns>Whether the upload existed.</returns>
    Task<bool> AbortAsync(string uploadId);

    /// <summary>
    /// Marks a file as held by an operation, whatever held it before.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="request">The file and the operation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The operation that held the file, or <c>null</c>.</returns>
    Task<string?> ClaimWorkingAsync(Guid libraryId, HlsWorkingClaim request, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the operation that holds a file.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="request">The file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The operation, or <c>null</c> when the file is not marked.</returns>
    Task<string?> GetWorkingAsync(Guid libraryId, HlsWorkingPath request, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the mark of a file while it names the operation; otherwise leaves it.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="request">The file and the operation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task ReleaseWorkingAsync(Guid libraryId, HlsWorkingRelease request, CancellationToken cancellationToken);
}
