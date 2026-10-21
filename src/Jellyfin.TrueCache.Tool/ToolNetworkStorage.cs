using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Server.Implementations.NetworkStorage;
using MediaBrowser.Common.Net.S3;
using MediaBrowser.Model.NetworkStorage;
using Microsoft.Data.Sqlite;

namespace Jellyfin.TrueCache.Tool;

/// <summary>
/// The network storage accesses of a server, read from its database without changing it, to read the
/// <c>s3://</c> targets of <c>.strm</c> files as the server does.
/// </summary>
internal sealed class ToolNetworkStorage
{
    private const string DatabaseFile = "jellyfin.db";

    private readonly IReadOnlyList<NetworkStorageAccessDto> _accesses;

    private ToolNetworkStorage(IReadOnlyList<NetworkStorageAccessDto> accesses)
    {
        _accesses = accesses;
    }

    /// <summary>
    /// Reads the accesses of a server.
    /// </summary>
    /// <param name="dataDirectory">The data folder of the server, or <c>null</c> for none.</param>
    /// <returns>The accesses.</returns>
    /// <exception cref="FileNotFoundException">The folder holds no database.</exception>
    public static ToolNetworkStorage Load(string? dataDirectory)
    {
        if (dataDirectory is null)
        {
            return new ToolNetworkStorage([]);
        }

        var path = Path.Combine(dataDirectory, DatabaseFile);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The server data folder holds no " + DatabaseFile + ".", path);
        }

        var accesses = new List<NetworkStorageAccessDto>();
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Protocol, Bucket, Endpoint, Region, UsePathStyle, AccessKey, SecretKey, SessionToken, UrlLifetimeSeconds FROM NetworkStorageAccesses";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            accesses.Add(new NetworkStorageAccessDto
            {
                Protocol = reader.GetString(0),
                Bucket = reader.GetString(1),
                Endpoint = reader.IsDBNull(2) ? null : reader.GetString(2),
                Region = reader.IsDBNull(3) ? null : reader.GetString(3),
                UsePathStyle = reader.GetBoolean(4),
                AccessKey = reader.IsDBNull(5) ? null : reader.GetString(5),
                SecretKey = reader.IsDBNull(6) ? null : reader.GetString(6),
                SessionToken = reader.IsDBNull(7) ? null : reader.GetString(7),
                UrlLifetimeSeconds = reader.GetInt32(8)
            });
        }

        return new ToolNetworkStorage(accesses);
    }

    /// <summary>
    /// Gets the address the target of a <c>.strm</c> file is read from.
    /// </summary>
    /// <param name="target">The target.</param>
    /// <returns>A presigned address for an <c>s3://</c> target, the target itself for any other, or <c>null</c>
    /// when no access covers the bucket.</returns>
    public string? ResolveShortcutTarget(string target)
    {
        if (!S3Address.IsS3Path(target))
        {
            return target;
        }

        if (!S3Address.TryParse(target, out var address))
        {
            return null;
        }

        var access = _accesses.FirstOrDefault(a => a.Protocol == NetworkStorageProtocols.S3 && string.Equals(a.Bucket, address.Bucket, StringComparison.Ordinal));
        return access is null
            ? null
            : S3UrlSigner.GetUrl(S3StorageProtocol.ToS3Access(access), address, DateTime.UtcNow, TimeSpan.FromSeconds(access.UrlLifetimeSeconds)).AbsoluteUri;
    }
}
