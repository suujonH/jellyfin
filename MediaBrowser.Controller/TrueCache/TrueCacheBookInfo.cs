#pragma warning disable CS1591

namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// The <c>[book]</c> section of a manifest. Each flag declares whether the book contains the document,
/// and a document it contains is stored in the <c>book</c> folder.
/// </summary>
public sealed class TrueCacheBookInfo
{
    /// <summary>Gets the number of pages; required for comic archives and PDF.</summary>
    public long? PageCount { get; init; }

    /// <summary>Gets whether a CBZ contains <c>ComicInfo.xml</c>.</summary>
    public bool? ComicInfo { get; init; }

    /// <summary>Gets whether a CBZ carries ComicBookInfo in its archive comment.</summary>
    public bool? ComicBookInfo { get; init; }

    /// <summary>Gets whether an EPUB has an OPF package document.</summary>
    public bool? Opf { get; init; }

    /// <summary>Gets whether the book has a cover image.</summary>
    public bool? Cover { get; init; }
}
