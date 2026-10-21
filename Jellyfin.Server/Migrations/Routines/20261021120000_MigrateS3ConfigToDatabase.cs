using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Migrations.Stages;
using Jellyfin.Server.ServerSetupApp;
using MediaBrowser.Common.Net.S3;
using MediaBrowser.Controller;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.NetworkStorage;
using MediaBrowser.Model.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Migrations.Routines;

/// <summary>
/// Moves the S3 buckets of <c>s3.xml</c> into network storage accesses in the database, where the dashboard manages them.
/// The file is renamed to <c>s3.xml.migrated</c>.
/// </summary>
[JellyfinMigration("2026-10-21T12:00:00", nameof(MigrateS3ConfigToDatabase), Stage = JellyfinMigrationStageTypes.AppInitialisation)]
internal class MigrateS3ConfigToDatabase : IAsyncMigrationRoutine
{
    private const string FileName = "s3.xml";

    private readonly IServerApplicationPaths _paths;
    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
    private readonly IXmlSerializer _xmlSerializer;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MigrateS3ConfigToDatabase"/> class.
    /// </summary>
    /// <param name="paths">The application paths.</param>
    /// <param name="dbProvider">The database context factory.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    /// <param name="startupLogger">The startup logger for Startup UI integration.</param>
    /// <param name="logger">The logger.</param>
    public MigrateS3ConfigToDatabase(
        IServerApplicationPaths paths,
        IDbContextFactory<JellyfinDbContext> dbProvider,
        IXmlSerializer xmlSerializer,
        IStartupLogger<MigrateS3ConfigToDatabase> startupLogger,
        ILogger<MigrateS3ConfigToDatabase> logger)
    {
        _paths = paths;
        _dbProvider = dbProvider;
        _xmlSerializer = xmlSerializer;
        _logger = startupLogger.With(logger);
    }

    /// <inheritdoc />
    public async Task PerformAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_paths.ConfigurationDirectoryPath, FileName);
        if (!File.Exists(path))
        {
            return;
        }

        var options = (S3Options)_xmlSerializer.DeserializeFromFile(typeof(S3Options), path)!;
        var context = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var existing = (await context.NetworkStorageAccesses
                    .Where(e => e.Protocol == NetworkStorageProtocols.S3)
                    .Select(e => e.Bucket)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);

            // An entry without a name covered every bucket no other entry names; an access covers one bucket,
            // so it becomes one access per bucket the library names.
            var fallback = options.Buckets.FirstOrDefault(b => string.IsNullOrEmpty(b.Name));
            var buckets = options.Buckets
                .Where(b => !string.IsNullOrEmpty(b.Name))
                .Select(b => (Name: b.Name, Options: b))
                .ToList();
            if (fallback is not null)
            {
                var named = buckets.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var name in await GetReferencedBucketsAsync(context, cancellationToken).ConfigureAwait(false))
                {
                    if (named.Add(name))
                    {
                        buckets.Add((name, fallback));
                    }
                }
            }

            var now = DateTime.UtcNow;
            foreach (var (name, bucket) in buckets)
            {
                if (!existing.Add(name))
                {
                    _logger.LogInformation("An access for S3 bucket {Bucket} already exists; the one in {File} is not migrated", name, FileName);
                    continue;
                }

                context.NetworkStorageAccesses.Add(new NetworkStorageAccess
                {
                    Id = Guid.NewGuid(),
                    Protocol = NetworkStorageProtocols.S3,
                    Bucket = name,
                    Endpoint = string.IsNullOrEmpty(bucket.Endpoint) ? null : bucket.Endpoint,
                    Region = string.IsNullOrEmpty(bucket.Region) ? null : bucket.Region,
                    UsePathStyle = bucket.UsePathStyle,
                    AccessKey = string.IsNullOrEmpty(bucket.AccessKey) ? null : bucket.AccessKey,
                    SecretKey = string.IsNullOrEmpty(bucket.SecretKey) ? null : bucket.SecretKey,
                    SessionToken = string.IsNullOrEmpty(bucket.SessionToken) ? null : bucket.SessionToken,
                    UrlLifetimeSeconds = Math.Clamp(options.UrlLifetimeSeconds, 60, (int)S3UrlSigner.MaxLifetime.TotalSeconds),
                    DateCreated = now,
                    DateModified = now
                });
                _logger.LogInformation("Migrated S3 bucket {Bucket} from {File} to network storage", name, FileName);
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(path, path + ".migrated", true);
    }

    private static async Task<IReadOnlyCollection<string>> GetReferencedBucketsAsync(JellyfinDbContext context, CancellationToken cancellationToken)
    {
        // The target of a .strm file is kept only in the serialized item.
        var data = await context.BaseItems
            // LIKE ignores case in SQLite, so S3:// reaches the parser, which takes it too.
            .Where(e => e.Data != null && EF.Functions.Like(e.Data, "%\"ShortcutPath\":\"s3://%"))
            .Select(e => e.Data!)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var buckets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var json in data)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("ShortcutPath", out var value)
                    && S3Address.TryParse(value.GetString(), out var address))
                {
                    buckets.Add(address.Bucket);
                }
            }
            catch (JsonException)
            {
                // Not an item this migration can read.
            }
        }

        return buckets;
    }
}
