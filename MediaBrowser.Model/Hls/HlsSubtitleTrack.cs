namespace MediaBrowser.Model.Hls;

/// <summary>
/// A subtitle track of a media file.
/// </summary>
/// <param name="Index">The ffprobe index of the stream.</param>
/// <param name="Codec">The codec.</param>
/// <param name="Language">The language tag, or empty.</param>
/// <param name="Title">The title tag, or empty.</param>
/// <param name="Default">Whether the track is marked as the default one.</param>
public sealed record HlsSubtitleTrack(int Index, string Codec, string Language, string Title, bool Default);
