using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Model.NetworkStorage;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Manages the network storage <c>.strm</c> files name resources in: accesses, how storage is published to clients,
/// and which resources can be read.
/// </summary>
[Route("NetworkStorage")]
[Authorize(Policy = Policies.RequiresElevation)]
public class NetworkStorageController : BaseJellyfinApiController
{
    private readonly INetworkStorageManager _networkStorage;
    private readonly INetworkStorageInventory _inventory;
    private readonly ITaskManager _taskManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkStorageController"/> class.
    /// </summary>
    /// <param name="networkStorage">The network storage manager.</param>
    /// <param name="inventory">What the library has in each network storage.</param>
    /// <param name="taskManager">The task manager.</param>
    public NetworkStorageController(INetworkStorageManager networkStorage, INetworkStorageInventory inventory, ITaskManager taskManager)
    {
        _networkStorage = networkStorage;
        _inventory = inventory;
        _taskManager = taskManager;
    }

    /// <summary>
    /// Gets statistics of every storage the library has resources in, and of every access. Nothing is read from
    /// the storage: the statistics come from what the library already knows.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Storages returned.</response>
    /// <returns>The storages.</returns>
    [HttpGet("Storages")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<NetworkStorageSummary>>> GetStorages(CancellationToken cancellationToken)
        => Ok(await _inventory.GetStoragesAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Gets the groups of the resources of one storage: its libraries with their series, and the contents no item refers to.
    /// </summary>
    /// <param name="key">The key of the storage, such as <c>s3:bucket</c>.</param>
    /// <param name="states">The states to include; none includes all.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Groups returned.</response>
    /// <returns>The groups that have resources of those states, the orphans last.</returns>
    [HttpGet("Storages/{key}/Groups")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<NetworkStorageGroup>>> GetGroups(
        [FromRoute, Required] string key,
        [FromQuery] NetworkStorageResourceState[] states,
        CancellationToken cancellationToken)
        => Ok(await _inventory.GetGroupsAsync(key, states, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Gets the resources of one storage, invalid first, or of one group of it, in episode order.
    /// </summary>
    /// <param name="key">The key of the storage, such as <c>s3:bucket</c>.</param>
    /// <param name="states">The states to include; none includes all.</param>
    /// <param name="group">The key of a group: <c>library:{id}</c>, <c>library:none</c>, <c>series:{id}</c> or <c>orphans</c>; none for the whole storage.</param>
    /// <param name="startIndex">The index of the first resource.</param>
    /// <param name="limit">The most resources to return; none returns all.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Resources returned.</response>
    /// <returns>The resources.</returns>
    [HttpGet("Storages/{key}/Resources")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<QueryResult<NetworkStorageResource>>> GetResources(
        [FromRoute, Required] string key,
        [FromQuery] NetworkStorageResourceState[] states,
        [FromQuery] string? group,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
        => Ok(await _inventory.GetResourcesAsync(key, states, group, startIndex ?? 0, limit, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Starts checking every resource that can be checked: that it exists and has its declared size.
    /// The failures recorded before are replaced. Progress is reported by the scheduled task.
    /// </summary>
    /// <response code="200">The check started, or was already running.</response>
    /// <response code="404">The task is not registered.</response>
    /// <returns>The id of the scheduled task that runs the check.</returns>
    [HttpPost("Check")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<string> StartCheck()
    {
        var task = _taskManager.ScheduledTasks.FirstOrDefault(t => string.Equals(t.ScheduledTask.Key, NetworkStorageTasks.CheckKey, StringComparison.Ordinal));
        if (task is null)
        {
            return NotFound();
        }

        if (task.State == TaskState.Idle)
        {
            _taskManager.Execute(task, new TaskOptions());
        }

        return Ok(task.Id);
    }

    /// <summary>
    /// Gets the accesses. Secrets are not returned.
    /// </summary>
    /// <response code="200">Accesses returned.</response>
    /// <returns>The accesses.</returns>
    [HttpGet("Accesses")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<NetworkStorageAccessDto>> GetAccesses()
        => _networkStorage.GetAccesses().Select(HideSecrets).ToList();

    /// <summary>
    /// Adds an access.
    /// </summary>
    /// <param name="access">The access.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The access was added.</response>
    /// <response code="400">The access is incomplete, or another one covers the same storage.</response>
    /// <returns>The access, without its secrets.</returns>
    [HttpPost("Accesses")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NetworkStorageAccessDto>> AddAccess([FromBody, Required] NetworkStorageAccessDto access, CancellationToken cancellationToken)
    {
        access.Id = Guid.Empty;
        try
        {
            return Ok(HideSecrets(await _networkStorage.SaveAccessAsync(access, cancellationToken).ConfigureAwait(false)));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Changes an access. A secret left out keeps the stored one; an empty secret removes it.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="access">The access.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The access was changed.</response>
    /// <response code="400">The access is incomplete, or another one covers the same storage.</response>
    /// <response code="404">No access has the id.</response>
    /// <returns>The access, without its secrets.</returns>
    [HttpPut("Accesses/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NetworkStorageAccessDto>> UpdateAccess(
        [FromRoute, Required] Guid id,
        [FromBody, Required] NetworkStorageAccessDto access,
        CancellationToken cancellationToken)
    {
        if (id.Equals(Guid.Empty))
        {
            return NotFound();
        }

        access.Id = id;
        try
        {
            return Ok(HideSecrets(await _networkStorage.SaveAccessAsync(access, cancellationToken).ConfigureAwait(false)));
        }
        catch (ResourceNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Removes an access and how its storage is published.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">The access was removed.</response>
    /// <response code="404">No access has the id.</response>
    /// <returns>No content.</returns>
    [HttpDelete("Accesses/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteAccess([FromRoute, Required] Guid id, CancellationToken cancellationToken)
        => await _networkStorage.DeleteAccessAsync(id, cancellationToken).ConfigureAwait(false) ? NoContent() : NotFound();

    /// <summary>
    /// Tests that the root of a storage can be read with an access, saved or not. Whether particular resources exist
    /// is not checked. A secret left out of a saved access is taken from the stored one.
    /// </summary>
    /// <param name="access">The access.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The result of the test.</response>
    /// <returns>The result.</returns>
    [HttpPost("Accesses/Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<NetworkStorageTestResult>> TestAccess([FromBody, Required] NetworkStorageAccessDto access, CancellationToken cancellationToken)
        => Ok(await _networkStorage.TestAccessAsync(access, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Gets how the storage of an access is published to clients. The secret is not returned.
    /// </summary>
    /// <param name="id">The id of the access.</param>
    /// <response code="200">Settings returned.</response>
    /// <response code="404">No access has the id.</response>
    /// <returns>The settings.</returns>
    [HttpGet("Accesses/{id}/External")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<NetworkStorageExternalAddressDto> GetExternalAddress([FromRoute, Required] Guid id)
        => _networkStorage.GetAccess(id) is null ? NotFound() : Ok(HideSecret(_networkStorage.GetExternalAddress(id)));

    /// <summary>
    /// Sets how the storage of an access is published to clients. A secret left out keeps the stored one.
    /// </summary>
    /// <param name="id">The id of the access.</param>
    /// <param name="external">The settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The settings were saved.</response>
    /// <response code="400">A public storage lacks what its authentication needs.</response>
    /// <response code="404">No access has the id.</response>
    /// <returns>The settings, without the secret.</returns>
    [HttpPut("Accesses/{id}/External")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NetworkStorageExternalAddressDto>> SetExternalAddress(
        [FromRoute, Required] Guid id,
        [FromBody, Required] NetworkStorageExternalAddressDto external,
        CancellationToken cancellationToken)
    {
        external.AccessId = id;
        try
        {
            return Ok(HideSecret(await _networkStorage.SaveExternalAddressAsync(external, cancellationToken).ConfigureAwait(false)));
        }
        catch (ResourceNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private static NetworkStorageAccessDto HideSecrets(NetworkStorageAccessDto access) => new()
    {
        Id = access.Id,
        Protocol = access.Protocol,
        Name = access.Name,
        Endpoint = access.Endpoint,
        Bucket = access.Bucket,
        Region = access.Region,
        UsePathStyle = access.UsePathStyle,
        AccessKey = access.AccessKey,
        HasSecretKey = !string.IsNullOrEmpty(access.SecretKey),
        HasSessionToken = !string.IsNullOrEmpty(access.SessionToken),
        UrlLifetimeSeconds = access.UrlLifetimeSeconds,
        IsArchive = access.IsArchive,
        Settings = access.Settings
    };

    private static NetworkStorageExternalAddressDto HideSecret(NetworkStorageExternalAddressDto external) => new()
    {
        AccessId = external.AccessId,
        AuthMode = external.AuthMode,
        BaseUrl = external.BaseUrl,
        HasSecret = !string.IsNullOrEmpty(external.Secret),
        UrlLifetimeSeconds = external.UrlLifetimeSeconds
    };
}
