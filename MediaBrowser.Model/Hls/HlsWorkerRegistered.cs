using System;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// A worker the server registered.
/// </summary>
/// <param name="Id">The id of the worker.</param>
public sealed record HlsWorkerRegistered(Guid Id);
