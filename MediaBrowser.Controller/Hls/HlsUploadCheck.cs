using System;

namespace MediaBrowser.Controller.Hls;

/// <summary>
/// A test file a worker uploads to see that its uploads reach the store. It is deleted once its address expires.
/// </summary>
/// <param name="Id">The check.</param>
/// <param name="Path">The file in the store.</param>
/// <param name="Address">The signed address in the storage, or <c>null</c> when the store is local and the worker uploads to the server.</param>
public sealed record HlsUploadCheck(Guid Id, string Path, Uri? Address);
