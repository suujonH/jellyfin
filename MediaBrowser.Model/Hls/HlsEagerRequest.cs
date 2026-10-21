using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// A worker adding media to a library that encodes some of its qualities right away.
/// </summary>
/// <param name="Worker">The name of the worker, 1 to 128 characters.</param>
/// <param name="LibraryId">The library the media is added to.</param>
/// <param name="Sha256">The SHA-256 hash of the file, as 64 hexadecimal characters.</param>
/// <param name="ProbeJson">The output of <c>ffprobe -show_streams -show_format -of json</c> for the file.</param>
/// <param name="Base">The root of the HLS store the worker uploads the plan to: <c>s3://bucket/prefix</c>, or an absolute path on the server.</param>
/// <param name="Qualities">The video qualities to encode now, some of those of the library.</param>
public sealed record HlsEagerRequest(string Worker, Guid LibraryId, string Sha256, string ProbeJson, string Base, IReadOnlyList<int> Qualities);
