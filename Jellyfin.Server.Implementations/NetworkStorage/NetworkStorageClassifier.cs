using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Model.NetworkStorage;

namespace Jellyfin.Server.Implementations.NetworkStorage;

/// <summary>
/// Tells the state of a network resource from what the library knows of it, without reading it.
/// </summary>
public static class NetworkStorageClassifier
{
    /// <summary>
    /// Gets the state of a resource.
    /// </summary>
    /// <param name="located">Whether the target belongs to a known protocol.</param>
    /// <param name="requiresAccess">Whether its protocol needs an access.</param>
    /// <param name="hasAccess">Whether an access covers its storage.</param>
    /// <param name="hasTrueCache">Whether it has a valid <c>__true_cache__</c> entry.</param>
    /// <param name="failure">What reading it last failed with, if it did.</param>
    /// <returns>The state, and why it is not valid.</returns>
    public static (NetworkStorageResourceState State, NetworkStorageIssue? Issue) Classify(
        bool located,
        bool requiresAccess,
        bool hasAccess,
        bool hasTrueCache,
        NetworkStorageIssue? failure)
    {
        if (!located)
        {
            return (NetworkStorageResourceState.Unknown, NetworkStorageIssue.Unresolved);
        }

        if (requiresAccess && !hasAccess)
        {
            return (NetworkStorageResourceState.Invalid, NetworkStorageIssue.NoAccess);
        }

        if (failure.HasValue)
        {
            return (NetworkStorageResourceState.Invalid, failure);
        }

        // An access-less address, such as an http one, says nothing of the resource unless its entry declares it.
        if (!requiresAccess && !hasTrueCache)
        {
            return (NetworkStorageResourceState.Unknown, NetworkStorageIssue.Undeclared);
        }

        return (NetworkStorageResourceState.Valid, null);
    }

    /// <summary>
    /// Gets the issue a failed read is recorded with.
    /// </summary>
    /// <param name="status">The outcome of the read.</param>
    /// <returns>The issue, or <c>null</c> when the outcome says nothing about the resource.</returns>
    public static NetworkStorageIssue? ToIssue(ObjectCheckStatus status) => status switch
    {
        ObjectCheckStatus.NotFound => NetworkStorageIssue.NotFound,
        ObjectCheckStatus.Forbidden => NetworkStorageIssue.Forbidden,
        ObjectCheckStatus.SizeMismatch => NetworkStorageIssue.SizeMismatch,
        _ => null
    };
}
