using System.Collections.Generic;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// Where a worker uploads one file.
/// </summary>
/// <param name="Path">The file, as the worker named it.</param>
/// <param name="Method">The HTTP method.</param>
/// <param name="Url">The address: absolute for storage, otherwise relative to the address of the server.</param>
/// <param name="Headers">The headers the request carries.</param>
public sealed record HlsUploadAddress(string Path, string Method, string Url, IReadOnlyDictionary<string, string> Headers);
