using System;

namespace MediaBrowser.Controller.NetworkStorage;

/// <summary>
/// An object a listing of network storage found.
/// </summary>
/// <param name="Key">The object key, in the bucket.</param>
/// <param name="Size">The size, in bytes.</param>
/// <param name="LastModified">When the object was written, in UTC.</param>
public sealed record StorageObject(string Key, long Size, DateTime LastModified);
