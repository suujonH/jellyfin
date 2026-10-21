using System;

namespace MediaBrowser.Controller.Hls;

/// <summary>
/// Where a worker uploads one file.
/// </summary>
/// <param name="Path">The file, as the worker named it.</param>
/// <param name="Address">The signed address in the storage, or <c>null</c> when the store is local and the worker uploads to the server.</param>
/// <param name="ContentType">The type of the content.</param>
public sealed record HlsUploadTarget(string Path, Uri? Address, string ContentType);
