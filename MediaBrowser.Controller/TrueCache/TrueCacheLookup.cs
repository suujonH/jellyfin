using System.Diagnostics.CodeAnalysis;

namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// The result of looking up the <c>__true_cache__</c> entry of a resource.
/// </summary>
public sealed class TrueCacheLookup
{
    private TrueCacheLookup(TrueCacheState state, TrueCacheEntry? entry, string? reason)
    {
        State = state;
        Entry = entry;
        Reason = reason;
    }

    /// <summary>Gets the result for a resource without an entry.</summary>
    public static TrueCacheLookup None { get; } = new(TrueCacheState.None, null, null);

    /// <summary>Gets the state of the entry.</summary>
    public TrueCacheState State { get; }

    /// <summary>Gets the entry when it is valid.</summary>
    public TrueCacheEntry? Entry { get; }

    /// <summary>Gets why the entry was rejected.</summary>
    public string? Reason { get; }

    /// <summary>Gets a value indicating whether the entry is complete.</summary>
    [MemberNotNullWhen(true, nameof(Entry))]
    public bool IsValid => State == TrueCacheState.Valid;

    /// <summary>Gets a value indicating whether the resource has an entry, valid or not. The server must not read such a resource.</summary>
    public bool IsDeclared => State != TrueCacheState.None;

    /// <summary>
    /// Creates the result for a valid entry.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The result.</returns>
    public static TrueCacheLookup Valid(TrueCacheEntry entry) => new(TrueCacheState.Valid, entry, null);

    /// <summary>
    /// Creates the result for a rejected entry.
    /// </summary>
    /// <param name="reason">Why the entry was rejected.</param>
    /// <returns>The result.</returns>
    public static TrueCacheLookup Rejected(string reason) => new(TrueCacheState.Rejected, null, reason);
}
