using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.MediaEncoding.Hls.Published;
using Jellyfin.Server.Migrations.Stages;
using Jellyfin.Server.ServerSetupApp;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net.S3;
using MediaBrowser.Controller;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Hls;
using MediaBrowser.Model.NetworkStorage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Migrations.Routines;

/// <summary>
/// Moves the settings and the data of the DistributedHls plugin into the server, whose pre-encoded HLS took its place.
/// </summary>
/// <remarks>
/// The settings go to <c>hls.xml</c> unless it exists already, and the users the plugin let play originals get the permission.
/// The library files, contents, published renditions, version items and the deletion queue of <c>distributed-hls/jobs.db</c>
/// are imported while the tables of pre-encoded HLS are empty, and the file is renamed to <c>jobs.db.migrated</c>. Unfinished
/// jobs are not: the server plans them again, and the directories of their outputs are queued for deletion. The renditions
/// are kept where they are, in the output directory of the plugin, or in the bucket the S3Storage plugin mapped it to; the
/// durations of their segments, which the plugin did not keep, are read from their playlists when they are first played.
/// </remarks>
[JellyfinMigration("2026-10-22T12:00:00", nameof(MigrateDistributedHlsPlugin), Stage = JellyfinMigrationStageTypes.AppInitialisation)]
internal class MigrateDistributedHlsPlugin : IAsyncMigrationRoutine
{
    private const string ConfigurationFileName = "Jellyfin.Plugin.DistributedHls.xml";
    private const string S3StorageConfigurationFileName = "Jellyfin.Plugin.S3Storage.xml";

    // The state of a published rendition in the database of the plugin.
    private const int PublishedState = 2;

    private const string UnfinishedReason = "The job of the DistributedHls plugin was not finished.";

    private readonly IServerApplicationPaths _paths;
    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
    private readonly IConfigurationManager _configurationManager;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MigrateDistributedHlsPlugin"/> class.
    /// </summary>
    /// <param name="paths">The application paths.</param>
    /// <param name="dbProvider">The database context factory.</param>
    /// <param name="configurationManager">The configuration manager, which saves the settings of pre-encoded HLS.</param>
    /// <param name="startupLogger">The startup logger for Startup UI integration.</param>
    /// <param name="logger">The logger.</param>
    public MigrateDistributedHlsPlugin(
        IServerApplicationPaths paths,
        IDbContextFactory<JellyfinDbContext> dbProvider,
        IConfigurationManager configurationManager,
        IStartupLogger<MigrateDistributedHlsPlugin> startupLogger,
        ILogger<MigrateDistributedHlsPlugin> logger)
    {
        _paths = paths;
        _dbProvider = dbProvider;
        _configurationManager = configurationManager;
        _logger = startupLogger.With(logger);
    }

    /// <inheritdoc />
    public async Task PerformAsync(CancellationToken cancellationToken)
    {
        var configurationPath = Path.Combine(_paths.PluginConfigurationsPath, ConfigurationFileName);
        var databasePath = Path.Combine(_paths.DataPath, "distributed-hls", "jobs.db");
        var hasDatabase = File.Exists(databasePath);
        XElement? plugin = null;
        if (File.Exists(configurationPath))
        {
            // Without its output directory the renditions cannot be found, so nothing is migrated.
            if ((plugin = LoadXml(configurationPath)) is null)
            {
                return;
            }
        }
        else if (!hasDatabase)
        {
            return;
        }

        // Settings already chosen on the server are kept.
        HlsOptions? options = null;
        if (plugin is not null && !File.Exists(Path.Combine(_paths.ConfigurationDirectoryPath, HlsConfigurationExtensions.ConfigurationKey + ".xml")))
        {
            options = GetOptions(plugin);
        }
        else if (plugin is not null)
        {
            _logger.LogInformation("Pre-encoded HLS has settings already; those of the DistributedHls plugin are not migrated");
        }

        var imported = false;
        var context = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            if (options is not null)
            {
                await GrantOriginalsAsync(context, Guids(plugin!, "OriginalUsers"), cancellationToken).ConfigureAwait(false);
            }

            if (hasDatabase)
            {
                var lease = TimeSpan.FromMinutes(Int(plugin, "LeaseMinutes") ?? new HlsOptions().LeaseMinutes);
                imported = await ImportAsync(context, databasePath, Text(plugin, "OutputPath") ?? GetDefaultOutputPath(), lease, cancellationToken).ConfigureAwait(false);
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (options is not null)
        {
            try
            {
                _configurationManager.SaveConfiguration(HlsConfigurationExtensions.ConfigurationKey, options);
                _logger.LogInformation("Migrated the settings of the DistributedHls plugin to pre-encoded HLS");
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning("The settings of the DistributedHls plugin are not valid and are not migrated: {Error}", ex.Message);
            }
        }

        if (imported)
        {
            File.Move(databasePath, databasePath + ".migrated", true);
        }
    }

    // The output directory of a plugin whose settings were never saved.
    private static string GetDefaultOutputPath() => OperatingSystem.IsWindows() ? Path.Combine(Path.GetTempPath(), "hls") : "/cache/hls";

    private static HlsOptions GetOptions(XElement plugin)
    {
        var defaults = new HlsOptions();
        var originalUsers = Guids(plugin, "OriginalUsers");
        var allowLocalOriginal = Bool(plugin, "AllowLocalOriginal") ?? defaults.AllowLocalOriginal;
        return new HlsOptions
        {
            Enabled = Bool(plugin, "Enabled") ?? defaults.Enabled,
            Libraries = Guids(plugin, "Libraries"),
            Qualities = Element(plugin, "Qualities") is null ? defaults.Qualities : Ints(plugin, "Qualities"),

            // The plugin allowed an original to the local network or to the users it named.
            AllowOriginal = allowLocalOriginal || originalUsers.Length > 0,
            AllowLocalOriginal = allowLocalOriginal,
            LeaseMinutes = Int(plugin, "LeaseMinutes") ?? defaults.LeaseMinutes,
            MaxAttempts = Int(plugin, "MaxAttempts") ?? defaults.MaxAttempts,
            OrphanRetentionHours = Int(plugin, "OrphanRetentionHours") ?? defaults.OrphanRetentionHours,
            PlaybackTokenHours = Int(plugin, "PlaybackTokenHours") ?? defaults.PlaybackTokenHours,

            // The grants the plugin issued stay valid.
            PlaybackSigningKey = Text(plugin, "PlaybackSigningKey") ?? string.Empty
        };
    }

    private static async Task GrantOriginalsAsync(JellyfinDbContext context, Guid[] userIds, CancellationToken cancellationToken)
    {
        if (userIds.Length == 0)
        {
            return;
        }

        var users = await context.Users.Include(u => u.Permissions).Where(u => userIds.Contains(u.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var user in users)
        {
            user.SetPermission(PermissionKind.EnableOriginalPlayback, true);
        }
    }

    // Whether the data was imported, so that the database of the plugin is done with.
    private async Task<bool> ImportAsync(JellyfinDbContext context, string databasePath, string outputPath, TimeSpan lease, CancellationToken cancellationToken)
    {
        if (await context.HlsSources.AnyAsync(cancellationToken).ConfigureAwait(false)
            || await context.HlsMedia.AnyAsync(cancellationToken).ConfigureAwait(false)
            || await context.HlsRenditions.AnyAsync(cancellationToken).ConfigureAwait(false)
            || await context.HlsJobs.AnyAsync(cancellationToken).ConfigureAwait(false)
            || await context.HlsDeletions.AnyAsync(cancellationToken).ConfigureAwait(false)
            || await context.HlsVersionItems.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Pre-encoded HLS has data already; the database {Path} of the DistributedHls plugin is not imported", databasePath);
            return false;
        }

        if (!Path.IsPathFullyQualified(outputPath))
        {
            _logger.LogWarning("The output directory {Path} of the DistributedHls plugin is not an absolute path; its database is not imported", outputPath);
            return false;
        }

        PluginData data;
        try
        {
            data = ReadDatabase(databasePath, lease);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "The database {Path} of the DistributedHls plugin cannot be read and is not imported", databasePath);
            return false;
        }

        var root = await GetRootAsync(context, outputPath, cancellationToken).ConfigureAwait(false);
        foreach (var rendition in data.Renditions)
        {
            rendition.Base = root;
        }

        foreach (var deletion in data.Deletions)
        {
            deletion.Base = root;
        }

        context.HlsMedia.AddRange(data.Media);
        context.HlsSources.AddRange(data.Sources);
        context.HlsRenditions.AddRange(data.Renditions);
        context.HlsJobOutputs.AddRange(data.Outputs);
        context.HlsDeletions.AddRange(data.Deletions);
        context.HlsVersionItems.AddRange(data.VersionItems);
        _logger.LogInformation(
            "Imported {Media} contents, {Sources} library files, {Renditions} published renditions, {VersionItems} version items and {Deletions} deletions of the DistributedHls plugin, kept in {Root}",
            data.Media.Count,
            data.Sources.Count,
            data.Renditions.Count,
            data.VersionItems.Count,
            data.Deletions.Count,
            root);
        return true;
    }

    // Rows that cannot be read are left out: a library file without its content is identified again, a content without a
    // published rendition is encoded again.
    private PluginData ReadDatabase(string databasePath, TimeSpan lease)
    {
        // Not pooled, so the file is closed for the rename.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        var data = new PluginData();

        foreach (var (sha256, libraryId, probe, orphaned, created) in Rows(command, "SELECT sha256,library_id,probe,orphaned_at,created_at FROM media", r => (r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? (long?)null : r.GetInt64(3), r.GetInt64(4))))
        {
            if (TryReadSha256(sha256) is not { } sha || !Guid.TryParse(libraryId, out var library) || TryReadJson<HlsProbe>(probe) is not { } info)
            {
                _logger.LogWarning("Content {Sha256} of the DistributedHls plugin cannot be read and is not imported", sha256);
                continue;
            }

            data.Media.Add(new HlsMedia
            {
                Sha256 = sha,
                MediaKey = HlsRenditionPaths.GetMediaKey(sha),
                LibraryId = library,
                Probe = JsonSerializer.Serialize(info),
                DateOrphaned = orphaned is { } time ? FromMilliseconds(time) : null,
                DateCreated = FromMilliseconds(created)
            });
        }

        var shas = data.Media.Select(m => m.Sha256).ToHashSet(StringComparer.Ordinal);
        foreach (var (itemId, libraryId, path, length, ticks, sha256) in Rows(command, "SELECT item_id,library_id,path,length,modified_ticks,sha256 FROM sources", r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5))))
        {
            if (!Guid.TryParse(itemId, out var item) || !Guid.TryParse(libraryId, out var library) || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            {
                _logger.LogWarning("Library file {ItemId} of the DistributedHls plugin cannot be read and is not imported", itemId);
                continue;
            }

            data.Sources.Add(new HlsSource
            {
                ItemId = item,
                LibraryId = library,
                Path = path,
                Length = length,
                DateModified = new DateTime(ticks, DateTimeKind.Utc),
                Sha256 = sha256 is not null && shas.Contains(sha256) ? sha256 : null
            });
        }

        // A published rendition with the outputs of the job that published it, in its directory.
        const string Published = """
            SELECT r.sha256,r.key,r.published_job_id,r.rel_dir,r.summary,r.profile,r.published_at,o.summary,o.files
            FROM renditions r JOIN job_outputs o ON o.job_id=r.published_job_id AND o.key=r.key AND o.rel_dir=r.rel_dir
            WHERE r.state=$state
            """;
        foreach (var row in Rows(command, Published, r => (Sha256: r.GetString(0), Key: r.GetString(1), JobId: r.GetString(2), Directory: r.GetString(3), Summary: r.IsDBNull(4) ? null : r.GetString(4), Profile: r.IsDBNull(5) ? null : r.GetString(5), PublishedAt: r.IsDBNull(6) ? (long?)null : r.GetInt64(6), OutputSummary: r.GetString(7), Files: r.GetString(8)), ("$state", PublishedState)))
        {
            if (!shas.Contains(row.Sha256)
                || !Guid.TryParse(row.JobId, out var jobId)
                || !HlsRenditionPaths.IsValidRelativeDirectory(row.Directory)
                || TryReadJson<HlsRenditionSummary>(row.Summary) is not { } summary
                || TryReadJson<HlsRenditionSummary>(row.OutputSummary) is not { } outputSummary
                || TryReadJson<List<HlsOutputFile>>(row.Files) is not { } files)
            {
                _logger.LogWarning("Rendition {Key} of content {Sha256} of the DistributedHls plugin cannot be read and is not imported", row.Key, row.Sha256);
                continue;
            }

            data.Renditions.Add(new HlsRendition
            {
                Sha256 = row.Sha256,
                Key = row.Key,
                State = (int)HlsRenditionState.Published,
                PublishedJobId = jobId,
                RelativeDirectory = row.Directory,
                Summary = JsonSerializer.Serialize(summary),
                Profile = row.Profile,
                DatePublished = row.PublishedAt is { } time ? FromMilliseconds(time) : null
            });
            data.Outputs.Add(new HlsJobOutput
            {
                JobId = jobId,
                Key = row.Key,
                RelativeDirectory = row.Directory,
                Summary = JsonSerializer.Serialize(outputSummary),
                Files = JsonSerializer.Serialize(files),
                Segments = "[]"
            });
        }

        var published = data.Renditions.Select(r => r.RelativeDirectory!).ToHashSet(StringComparer.Ordinal);
        var queued = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (directory, notBefore, attempts, reason, lastError) in Rows(command, "SELECT rel_dir,not_before,attempts,reason,last_error FROM deletions", r => (r.GetString(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4))))
        {
            if (HlsRenditionPaths.IsValidRelativeDirectory(directory) && !published.Contains(directory) && queued.Add(directory))
            {
                data.Deletions.Add(new HlsDeletion { RelativeDirectory = directory, Base = string.Empty, NotBefore = FromMilliseconds(notBefore), Attempts = attempts, Reason = reason, LastError = lastError });
            }
        }

        // The outputs of the jobs that are not imported.
        var now = DateTime.UtcNow;
        foreach (var directory in Rows(command, "SELECT DISTINCT rel_dir FROM job_outputs", r => r.GetString(0)))
        {
            if (HlsRenditionPaths.IsValidRelativeDirectory(directory) && !published.Contains(directory) && queued.Add(directory))
            {
                data.Deletions.Add(new HlsDeletion { RelativeDirectory = directory, Base = string.Empty, NotBefore = now, Reason = UnfinishedReason });
            }
        }

        // The directories of the encoding jobs a worker still holds, which it may be uploading to until its lease ends; as for a lost
        // lease, they are deleted one lease length after it.
        const string Leased = "SELECT payload,lease_until FROM jobs WHERE kind=1 AND status IN (1,2,5) AND lease_until IS NOT NULL";
        foreach (var (payload, leaseUntil) in Rows(command, Leased, r => (r.GetString(0), r.GetInt64(1))))
        {
            var notBefore = (FromMilliseconds(leaseUntil) > now ? FromMilliseconds(leaseUntil) : now) + lease;
            foreach (var directory in GetPlannedDirectories(payload))
            {
                if (HlsRenditionPaths.IsValidRelativeDirectory(directory) && !published.Contains(directory) && queued.Add(directory))
                {
                    data.Deletions.Add(new HlsDeletion { RelativeDirectory = directory, Base = string.Empty, NotBefore = notBefore, Reason = UnfinishedReason });
                }
            }
        }

        // Written again by the server, which describes them otherwise.
        foreach (var (itemId, quality, versionId) in Rows(command, "SELECT item_id,quality,version_id FROM version_items", r => (r.GetString(0), r.GetInt32(1), r.GetString(2))))
        {
            if (Guid.TryParse(itemId, out var item) && Guid.TryParse(versionId, out var version))
            {
                data.VersionItems.Add(new HlsVersionItem { ItemId = item, Quality = quality, VersionId = version, Signature = string.Empty });
            }
        }

        return data;
    }

    // The root the renditions are kept in: the output directory, or where in its bucket the S3Storage plugin mapped it to, which
    // then gets an access with the credentials of the mapping unless it has one.
    private async Task<string> GetRootAsync(JellyfinDbContext context, string outputPath, CancellationToken cancellationToken)
    {
        var local = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputPath));
        var s3StoragePath = Path.Combine(_paths.PluginConfigurationsPath, S3StorageConfigurationFileName);
        // A disabled S3Storage plugin did not mount its buckets, so the plugin wrote to the local directory.
        if (!File.Exists(s3StoragePath)
            || LoadXml(s3StoragePath) is not { } s3Storage
            || Bool(s3Storage, "Enabled") != true
            || FindMapping(s3Storage, local) is not { } mapping)
        {
            return local;
        }

        var (bucket, key) = mapping;
        var name = Text(bucket, "Bucket")!;
        var isArchive = await context.NetworkStorageAccesses
            .Where(a => a.Protocol == NetworkStorageProtocols.S3 && a.Bucket == name)
            .Select(a => (bool?)a.IsArchive)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (isArchive is null)
        {
            var now = DateTime.UtcNow;
            context.NetworkStorageAccesses.Add(new NetworkStorageAccess
            {
                Id = Guid.NewGuid(),
                Protocol = NetworkStorageProtocols.S3,
                Bucket = name,
                Endpoint = NonEmpty(Text(bucket, "Endpoint")),
                Region = NonEmpty(Text(bucket, "Region")),

                // The S3Storage plugin always used path-style addresses.
                UsePathStyle = true,
                AccessKey = NonEmpty(Text(bucket, "AccessKey")),
                SecretKey = NonEmpty(Text(bucket, "SecretKey")),
                UrlLifetimeSeconds = new NetworkStorageAccessDto().UrlLifetimeSeconds,
                IsArchive = false,
                DateCreated = now,
                DateModified = now
            });
            _logger.LogInformation("Created an access for S3 bucket {Bucket} of the pre-encoded HLS from the S3Storage plugin; clients read through the server until it has an external address", name);
        }
        else if (isArchive.Value)
        {
            _logger.LogWarning("The access of S3 bucket {Bucket} is an archive, which pre-encoded HLS is not played from; it has to be changed in network storage", name);
        }

        return S3Address.Prefix + name + (key.Length == 0 ? string.Empty : "/" + key);
    }

    // The bucket of the S3Storage plugin whose local directory holds the path, the longest first, as the plugin found it, and the key.
    private static (XElement Bucket, string Key)? FindMapping(XElement s3Storage, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var buckets = Element(s3Storage, "Buckets")?.Elements() ?? [];
        foreach (var bucket in buckets.OrderByDescending(b => Text(b, "LocalPath")?.Length ?? 0))
        {
            var localPath = Text(bucket, "LocalPath");
            if (string.IsNullOrEmpty(localPath) || !Path.IsPathFullyQualified(localPath) || !S3Address.IsValidBucket(Text(bucket, "Bucket")))
            {
                continue;
            }

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localPath));
            if (!path.Equals(root, comparison) && !path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            var parts = new[] { (Text(bucket, "KeyPrefix") ?? string.Empty).Trim('/'), relative == "." ? string.Empty : relative };
            return (bucket, string.Join('/', parts.Where(p => p.Length > 0)));
        }

        return null;
    }

    private XElement? LoadXml(string path)
    {
        try
        {
            return XDocument.Load(path).Root;
        }
        catch (XmlException ex)
        {
            _logger.LogError(ex, "The settings {Path} of a plugin cannot be read; the DistributedHls plugin is not migrated", path);
            return null;
        }
    }

    private static List<T> Rows<T>(SqliteCommand command, string sql, Func<SqliteDataReader, T> map, params (string Name, object Value)[] parameters)
    {
        command.CommandText = sql;
        command.Parameters.Clear();
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<T>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    // The directories in the plan of a claimed encoding job; a pending one has a list of keys instead.
    private static IEnumerable<string> GetPlannedDirectories(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Outputs", out var outputs)
                || outputs.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. outputs.EnumerateArray()
                .Select(o => o.ValueKind == JsonValueKind.Object && o.TryGetProperty("RelativeDirectory", out var directory) && directory.ValueKind == JsonValueKind.String ? directory.GetString() : null)
                .OfType<string>()];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? TryReadSha256(string sha256)
    {
        try
        {
            return HlsProbeReader.NormalizeSha256(sha256);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static T? TryReadJson<T>(string? json)
        where T : class
    {
        try
        {
            return json is null ? null : JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTime FromMilliseconds(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // The settings are written by XmlSerializer: elements without a namespace, arrays as one child element per value.
    private static XElement? Element(XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static string? Text(XElement? parent, string name) => Element(parent, name)?.Value;

    private static bool? Bool(XElement? parent, string name) => bool.TryParse(Text(parent, name), out var value) ? value : null;

    private static int? Int(XElement? parent, string name) => int.TryParse(Text(parent, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static Guid[] Guids(XElement parent, string name)
        => [.. (Element(parent, name)?.Elements() ?? []).Select(e => Guid.TryParse(e.Value, out var id) ? id : Guid.Empty).Where(id => !id.Equals(Guid.Empty))];

    private static int[] Ints(XElement parent, string name)
        => [.. (Element(parent, name)?.Elements() ?? []).Select(e => int.TryParse(e.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? (int?)value : null).OfType<int>()];

    private sealed class PluginData
    {
        public List<HlsMedia> Media { get; } = [];

        public List<HlsSource> Sources { get; } = [];

        public List<HlsRendition> Renditions { get; } = [];

        public List<HlsJobOutput> Outputs { get; } = [];

        public List<HlsDeletion> Deletions { get; } = [];

        public List<HlsVersionItem> VersionItems { get; } = [];
    }
}
