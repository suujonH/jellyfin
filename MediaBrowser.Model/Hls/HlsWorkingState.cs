namespace MediaBrowser.Model.Hls;

/// <summary>
/// The operation a working marker names.
/// </summary>
/// <param name="OperationId">The operation; <c>null</c> when the file is not marked.</param>
public sealed record HlsWorkingState(string? OperationId);
