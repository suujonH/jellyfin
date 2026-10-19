using System;

namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// Thrown instead of reading a resource whose <c>__true_cache__</c> entry is incomplete or invalid.
/// </summary>
public class TrueCacheRejectedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TrueCacheRejectedException"/> class.
    /// </summary>
    public TrueCacheRejectedException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TrueCacheRejectedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public TrueCacheRejectedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TrueCacheRejectedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public TrueCacheRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Creates the exception for a rejected resource.
    /// </summary>
    /// <param name="resourcePath">The resource.</param>
    /// <param name="lookup">The lookup result.</param>
    /// <returns>The exception.</returns>
    public static TrueCacheRejectedException For(string resourcePath, TrueCacheLookup lookup)
        => new($"{resourcePath} has an incomplete __true_cache__ entry and is not read: {lookup.Reason}");
}
