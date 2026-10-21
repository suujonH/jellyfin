using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Server.Migrations.Stages;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Migrations.Routines;

/// <summary>
/// Adds the MP4 extensions to the on-demand keyframe extraction of a configuration that still has the old default, Matroska alone.
/// The MP4 sample tables are read without reading the media data, like Matroska cues.
/// </summary>
[JellyfinMigration("2026-10-21T20:10:00", nameof(AddMp4KeyframeExtraction), Stage = JellyfinMigrationStageTypes.PreInitialisation)]
internal class AddMp4KeyframeExtraction : IAsyncMigrationRoutine
{
    private static readonly string[] _mp4Extensions = ["mp4", "m4v", "mov"];

    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<AddMp4KeyframeExtraction> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AddMp4KeyframeExtraction"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public AddMp4KeyframeExtraction(IApplicationPaths applicationPaths, ILogger<AddMp4KeyframeExtraction> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task PerformAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = Path.Combine(_applicationPaths.ConfigurationDirectoryPath, "encoding.xml");
        if (!File.Exists(path))
        {
            return Task.CompletedTask;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            _logger.LogWarning(ex, "Could not parse encoding configuration; skipping the MP4 keyframe extraction migration");
            return Task.CompletedTask;
        }

        var extensions = document.Root?.Element(nameof(EncodingOptions.AllowOnDemandMetadataBasedKeyframeExtractionForExtensions));
        var values = extensions?.Elements().ToList();
        if (values is not [var only] || !string.Equals(only.Value.Trim().TrimStart('.'), "mkv", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        foreach (var extension in _mp4Extensions)
        {
            extensions!.Add(new XElement(only.Name, extension));
        }

        document.Save(path, SaveOptions.DisableFormatting);
        _logger.LogInformation("Enabled on-demand keyframe extraction for MP4 files in encoding configuration");
        return Task.CompletedTask;
    }
}
