using System;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// A worker asking to be registered.
/// </summary>
/// <param name="Id">The id the worker has, registered under it if the server does not know it, or <c>null</c> for a new one.</param>
/// <param name="Name">The name of the worker, 1 to 128 characters.</param>
public sealed record HlsWorkerRegistration(Guid? Id, string Name);
