using System;

namespace MediaBrowser.Controller.Hls;

/// <summary>
/// Where a worker reads the file of its job.
/// </summary>
/// <param name="FilePath">The local file the server sends, or <c>null</c>.</param>
/// <param name="Address">The address of the target of a .strm file the worker is redirected to, or <c>null</c>.</param>
public sealed record HlsJobInput(string? FilePath, Uri? Address);
