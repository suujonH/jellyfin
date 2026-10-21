using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.IO;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// Hides <c>__true_cache__</c> folders from the library, and leaves out every resource whose entry is incomplete or invalid.
/// </summary>
public class TrueCacheIgnoreRule : IResolverIgnoreRule
{
    private readonly ITrueCacheService _trueCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrueCacheIgnoreRule"/> class.
    /// </summary>
    /// <param name="trueCache">The <c>__true_cache__</c> lookup.</param>
    public TrueCacheIgnoreRule(ITrueCacheService trueCache)
    {
        _trueCache = trueCache;
    }

    /// <inheritdoc />
    public bool ShouldIgnore(FileSystemMetadata fileInfo, BaseItem? parent)
    {
        if (fileInfo.IsDirectory && TrueCacheLayout.IsCacheFolderName(fileInfo.Name))
        {
            return true;
        }

        return _trueCache.Lookup(fileInfo.FullName).State == TrueCacheState.Rejected;
    }
}
