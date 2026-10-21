namespace MediaBrowser.Model.Hls;

/// <summary>
/// The protocol between the server and the HLS workers.
/// </summary>
public static class HlsWorkerProtocol
{
    /// <summary>
    /// The header every request of a worker carries, with the version it speaks.
    /// </summary>
    public const string Header = "X-Jellyfin-Hls-Protocol";

    /// <summary>
    /// The version the server speaks.
    /// </summary>
    public const int Version = 1;
}
