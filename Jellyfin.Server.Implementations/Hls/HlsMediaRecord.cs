using System;
using MediaBrowser.Model.Hls;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// An identified content.
/// </summary>
/// <param name="Sha256">The hash.</param>
/// <param name="MediaKey">The directory of the content in the HLS store.</param>
/// <param name="LibraryId">The library the content was first found in.</param>
/// <param name="Probe">The probe kept for the content.</param>
/// <param name="DateOrphaned">When the content was first found without a library file.</param>
public sealed record HlsMediaRecord(string Sha256, string MediaKey, Guid LibraryId, HlsProbe Probe, DateTime? DateOrphaned);
