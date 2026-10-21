namespace MediaBrowser.Model.Hls;

/// <summary>
/// The version of the worker protocol the server speaks.
/// </summary>
/// <param name="Version">The version.</param>
public sealed record HlsProtocolVersion(int Version);
