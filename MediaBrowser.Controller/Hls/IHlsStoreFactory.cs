using System;

namespace MediaBrowser.Controller.Hls;

/// <summary>
/// Opens the HLS store a setting names.
/// </summary>
public interface IHlsStoreFactory
{
    /// <summary>
    /// Opens a store. Whether it can be reached is not checked: that is up to <see cref="IHlsStore.CheckRootAsync"/>.
    /// </summary>
    /// <param name="location">An absolute local path, or <c>s3://bucket/prefix</c>, where the prefix may be empty.</param>
    /// <returns>The store.</returns>
    /// <exception cref="ArgumentException">The location is neither.</exception>
    IHlsStore Create(string location);
}
