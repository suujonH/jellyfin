using System;

namespace MediaBrowser.Controller.Hls;

/// <summary>
/// A file in the HLS store.
/// </summary>
/// <param name="Path">The path, relative to the directory it was listed in, with forward slashes.</param>
/// <param name="Length">The size, in bytes.</param>
/// <param name="LastModified">When the file was written, in UTC.</param>
public sealed record HlsStoreFile(string Path, long Length, DateTime LastModified);
