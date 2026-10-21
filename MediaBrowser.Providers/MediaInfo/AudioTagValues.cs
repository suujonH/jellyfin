using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ATL;
using MediaBrowser.Controller.TrueCache;

namespace MediaBrowser.Providers.MediaInfo
{
    /// <summary>
    /// The tag values of an audio file, read with ATL or taken from the file's <c>__true_cache__</c> entry.
    /// Multi-valued fields are joined with <see cref="TrueCacheAudioTags.ValueSeparator"/>, as ATL is configured to report them.
    /// </summary>
    internal sealed class AudioTagValues
    {
        private AudioTagValues(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public string? Title { get; private init; }

        public string? Album { get; private init; }

        public string? AlbumArtist { get; private init; }

        public string? Artist { get; private init; }

        public string? Composer { get; private init; }

        public string? Genre { get; private init; }

        public int? Year { get; private init; }

        public int? TrackNumber { get; private init; }

        public int? DiscNumber { get; private init; }

        public DateTime? Date { get; private init; }

        public string? Description { get; private init; }

        public string? Comment { get; private init; }

        public string? Publisher { get; private init; }

        public bool Id3v1Only { get; private init; }

        public IReadOnlyDictionary<string, string> AdditionalFields { get; private init; } = new Dictionary<string, string>();

        /// <summary>Gets the embedded lyrics the server saves: the first synchronized LRC-style lyrics, otherwise the unsynchronized text.</summary>
        public string? Lyrics { get; private init; }

        public static AudioTagValues FromFile(string path)
        {
            var track = new Track(path);

            // ATL supports both SRT and LRC formats as synchronized lyrics, but we only want to save LRC format.
            var supportedLyrics = track.Lyrics.Where(l => l.Format != LyricsInfo.LyricsFormat.SRT).ToList();
            var candidateSynchronizedLyric = supportedLyrics.FirstOrDefault(l => l.Format is not LyricsInfo.LyricsFormat.UNSYNCHRONIZED and not LyricsInfo.LyricsFormat.OTHER && l.SynchronizedLyrics is not null);
            var candidateUnsynchronizedLyric = supportedLyrics.FirstOrDefault(l => l.Format is LyricsInfo.LyricsFormat.UNSYNCHRONIZED or LyricsInfo.LyricsFormat.OTHER && l.UnsynchronizedLyrics is not null);

            // We should never use the property setters of the ATL.Track class; values are only copied here.
            return new AudioTagValues(path)
            {
                Title = track.Title,
                Album = track.Album,
                AlbumArtist = track.AlbumArtist,
                Artist = track.Artist,
                Composer = track.Composer,
                Genre = track.Genre,
                Year = track.Year,
                TrackNumber = track.TrackNumber,
                DiscNumber = track.DiscNumber,
                Date = track.Date,
                Description = track.Description,
                Comment = track.Comment,
                Publisher = track.Publisher,
                Id3v1Only = track.MetadataFormats.All(mf => string.Equals(mf.ShortName, "ID3v1", StringComparison.OrdinalIgnoreCase)),
                AdditionalFields = new Dictionary<string, string>(track.AdditionalFields, StringComparer.Ordinal),
                Lyrics = candidateSynchronizedLyric is not null ? candidateSynchronizedLyric.FormatSynch() : candidateUnsynchronizedLyric?.UnsynchronizedLyrics
            };
        }

        public static AudioTagValues FromTrueCache(TrueCacheEntry entry)
        {
            var audio = entry.Manifest.Audio!;
            var tags = audio.Tags!;
            return new AudioTagValues(entry.ResourcePath)
            {
                Title = tags.Title,
                Album = tags.Album,
                AlbumArtist = Join(tags.AlbumArtists),
                Artist = Join(tags.Artists),
                Composer = Join(tags.Composers),
                Genre = Join(tags.Genres),
                Year = tags.Year,
                TrackNumber = tags.TrackNumber,
                DiscNumber = tags.DiscNumber,
                Date = tags.Date,
                Description = tags.Description,
                Comment = tags.Comment,
                Publisher = tags.Publisher,
                Id3v1Only = tags.Id3v1Only,
                AdditionalFields = tags.AdditionalFields,
                Lyrics = audio.EmbeddedLyrics == true ? File.ReadAllText(entry.LyricsPath) : null
            };
        }

        private static string? Join(IReadOnlyList<string> values)
            => values.Count == 0 ? null : string.Join(TrueCacheAudioTags.ValueSeparator, values);
    }
}
