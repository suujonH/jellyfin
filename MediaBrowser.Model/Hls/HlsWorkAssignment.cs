namespace MediaBrowser.Model.Hls;

/// <summary>
/// A job a worker claimed.
/// </summary>
/// <param name="Lease">The lease.</param>
/// <param name="Source">The library file the job reads: its path, or the target of a .strm file, its size and its modification time.</param>
/// <param name="InputUrl">Where the worker reads the file, relative to the address of the server.</param>
public sealed record HlsWorkAssignment(HlsWorkLease Lease, HlsSourceFile Source, string InputUrl);
