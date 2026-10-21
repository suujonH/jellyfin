namespace MediaBrowser.Model.Hls;

/// <summary>
/// What a worker found the file of its job to be.
/// </summary>
/// <param name="LeaseToken">The token of the lease.</param>
/// <param name="Sha256">The SHA-256 hash of the file, as 64 hexadecimal characters.</param>
/// <param name="ProbeJson">The output of <c>ffprobe -show_streams -show_format -of json</c> for the file.</param>
/// <param name="Base">The root of the HLS store the worker uploads the plan to: <c>s3://bucket/prefix</c>, or an absolute path on the server.</param>
public sealed record HlsIdentification(string LeaseToken, string Sha256, string ProbeJson, string Base);
