using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// A library whose files are pre-encoded, as a worker adding media sees it; its folders are those of <c>/Library/VirtualFolders</c>.
/// </summary>
/// <param name="Id">The library.</param>
/// <param name="Name">The name of the library.</param>
/// <param name="Qualities">The video qualities its files are encoded in, the highest first.</param>
public sealed record HlsWorkerLibrary(Guid Id, string Name, IReadOnlyList<int> Qualities);
