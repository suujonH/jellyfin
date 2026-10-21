using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Checks that every network resource exists and has its declared size. It only runs when started,
/// from the Network Storage page of the dashboard.
/// </summary>
public class CheckNetworkStorageTask : IScheduledTask
{
    private readonly ILocalizationManager _localization;
    private readonly INetworkStorageManager _networkStorage;

    /// <summary>
    /// Initializes a new instance of the <see cref="CheckNetworkStorageTask"/> class.
    /// </summary>
    /// <param name="localization">The localization manager.</param>
    /// <param name="networkStorage">The network storage manager.</param>
    public CheckNetworkStorageTask(ILocalizationManager localization, INetworkStorageManager networkStorage)
    {
        _localization = localization;
        _networkStorage = networkStorage;
    }

    /// <inheritdoc />
    public string Name => _localization.GetLocalizedString("TaskCheckNetworkStorage");

    /// <inheritdoc />
    public string Key => NetworkStorageTasks.CheckKey;

    /// <inheritdoc />
    public string Description => _localization.GetLocalizedString("TaskCheckNetworkStorageDescription");

    /// <inheritdoc />
    public string Category => _localization.GetLocalizedString("TasksLibraryCategory");

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _networkStorage.CheckAllAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
