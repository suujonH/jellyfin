using System;

namespace MediaBrowser.Controller.Hls;

/// <summary>
/// A file is put in place by an operation that no longer holds it: its working marker names another operation.
/// </summary>
public sealed class HlsWorkingTakenException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HlsWorkingTakenException"/> class.
    /// </summary>
    public HlsWorkingTakenException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsWorkingTakenException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public HlsWorkingTakenException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsWorkingTakenException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public HlsWorkingTakenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
