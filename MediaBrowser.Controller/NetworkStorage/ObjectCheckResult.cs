namespace MediaBrowser.Controller.NetworkStorage;

/// <summary>
/// What reading the headers of a resource showed.
/// </summary>
/// <param name="Status">The outcome.</param>
/// <param name="StatusCode">The HTTP status the storage answered with, if it answered.</param>
/// <param name="Size">The size the storage reported.</param>
public sealed record ObjectCheckResult(ObjectCheckStatus Status, int? StatusCode, long? Size)
{
    /// <summary>
    /// Gets a value indicating whether the outcome is about the resource itself, missing or not permitted,
    /// rather than about the storage being busy or unreachable.
    /// </summary>
    public bool IsResourceFailure => Status is ObjectCheckStatus.NotFound or ObjectCheckStatus.Forbidden or ObjectCheckStatus.SizeMismatch;

    /// <summary>
    /// Gets the outcome of an HTTP answer.
    /// </summary>
    /// <param name="statusCode">The status.</param>
    /// <param name="size">The reported size.</param>
    /// <param name="expectedSize">The size the resource should have, if known.</param>
    /// <returns>The outcome: rate limits, server errors and other statuses are transient and say nothing about the resource.</returns>
    public static ObjectCheckResult FromStatus(int statusCode, long? size, long? expectedSize)
    {
        var status = statusCode switch
        {
            >= 200 and < 300 when expectedSize.HasValue && size.HasValue && size != expectedSize => ObjectCheckStatus.SizeMismatch,
            >= 200 and < 300 => ObjectCheckStatus.Ok,
            404 or 410 => ObjectCheckStatus.NotFound,
            401 or 403 => ObjectCheckStatus.Forbidden,
            _ => ObjectCheckStatus.Transient
        };

        return new ObjectCheckResult(status, statusCode, size);
    }
}
