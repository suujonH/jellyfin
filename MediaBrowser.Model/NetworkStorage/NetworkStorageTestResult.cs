namespace MediaBrowser.Model.NetworkStorage;

/// <summary>
/// The result of testing an access.
/// </summary>
/// <param name="Success">Whether the storage could be read with the access.</param>
/// <param name="StatusCode">The HTTP status the storage answered with, if it answered.</param>
/// <param name="Message">What went wrong.</param>
public sealed record NetworkStorageTestResult(bool Success, int? StatusCode, string? Message);
