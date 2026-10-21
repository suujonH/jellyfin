using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Emby.Server.Implementations;
using Emby.Server.Implementations.Configuration;
using Emby.Server.Implementations.IO;
using Emby.Server.Implementations.Library;
using Emby.Server.Implementations.Localization;
using Emby.Server.Implementations.Serialization;
using Jellyfin.Drawing.Skia;
using Jellyfin.Server.Implementations.Trickplay;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.MediaEncoding.Attachments;
using MediaBrowser.MediaEncoding.BdInfo;
using MediaBrowser.MediaEncoding.Configuration;
using MediaBrowser.MediaEncoding.Encoder;
using MediaBrowser.MediaEncoding.Subtitles;
using MediaBrowser.MediaEncoding.TrueCache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.TrueCache.Tool;

/// <summary>
/// The server components the generator runs, wired as the server wires them but in a private work folder.
/// Components the extraction code does not use are left out.
/// </summary>
internal sealed class ToolServices : IDisposable
{
    private ToolServices(ToolOptions options, ILoggerFactory loggerFactory)
    {
        WorkDirectory = Path.Combine(Path.GetTempPath(), "jellyfin-truecache-" + Guid.NewGuid().ToString("N"));
        var configDirectory = Path.Combine(WorkDirectory, "config");
        Directory.CreateDirectory(configDirectory);

        // The server's own settings decide what an entry must hold; the copy keeps its folder untouched.
        if (options.ServerConfigDirectory is not null)
        {
            foreach (var name in new[] { "system.xml", "encoding.xml" })
            {
                var source = Path.Combine(options.ServerConfigDirectory, name);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(configDirectory, name));
                }
            }
        }

        LoggerFactory = loggerFactory;
        AppPaths = new ServerApplicationPaths(
            Path.Combine(WorkDirectory, "data"),
            Path.Combine(WorkDirectory, "log"),
            configDirectory,
            Path.Combine(WorkDirectory, "cache"),
            Path.Combine(WorkDirectory, "web"));
        Config = new ServerConfigurationManager(AppPaths, loggerFactory, new MyXmlSerializer());
        Config.AddParts([new EncodingConfigurationFactory()]);
        NetworkStorage = ToolNetworkStorage.Load(options.ServerDataDirectory);
        ApplyOverrides(Config.Configuration, options);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [MediaBrowser.Controller.Extensions.ConfigurationExtensions.FfmpegPathKey] = options.FfmpegPath })
            .Build();

        FileSystem = new ManagedFileSystem(loggerFactory.CreateLogger<ManagedFileSystem>(), AppPaths, []);
        Localization = new LocalizationManager(Config, loggerFactory.CreateLogger<LocalizationManager>());
        BluRayExaminer = new BdInfoExaminer(FileSystem);
        Encoder = new MediaEncoder(
            loggerFactory.CreateLogger<MediaEncoder>(),
            Config,
            FileSystem,
            BluRayExaminer,
            Localization,
            configuration,
            Config,
            NoTrueCacheService.Instance);
        PathManager = new PathManager(loggerFactory.CreateLogger<PathManager>(), Config, AppPaths);

        // Extraction never reaches the parser, the HTTP client or the media source manager.
        SubtitleEncoder = new SubtitleEncoder(
            loggerFactory.CreateLogger<SubtitleEncoder>(),
            FileSystem,
            Encoder,
            null!,
            null!,
            null!,
            PathManager,
            Config,
            Localization,
            NoTrueCacheService.Instance);
        AttachmentExtractor = new AttachmentExtractor(
            loggerFactory.CreateLogger<AttachmentExtractor>(),
            FileSystem,
            Encoder,
            null!,
            PathManager,
            NoTrueCacheService.Instance);
        EncodingHelper = new EncodingHelper(AppPaths, Encoder, SubtitleEncoder, configuration, Config, PathManager);
        ImageEncoder = new SkiaEncoder(loggerFactory.CreateLogger<SkiaEncoder>(), AppPaths);

        // Only the tile creation is used, which needs no database.
        Trickplay = new TrickplayManager(
            loggerFactory.CreateLogger<TrickplayManager>(),
            Encoder,
            FileSystem,
            EncodingHelper,
            Config,
            ImageEncoder,
            null!,
            AppPaths,
            PathManager,
            NoTrueCacheService.Instance);
        Loader = new TrueCacheLoader(loggerFactory.CreateLogger<TrueCacheLoader>(), Localization);
    }

    public string WorkDirectory { get; }

    public ILoggerFactory LoggerFactory { get; }

    public IApplicationPaths AppPaths { get; }

    public ServerConfigurationManager Config { get; }

    public ToolNetworkStorage NetworkStorage { get; }

    public ManagedFileSystem FileSystem { get; }

    public LocalizationManager Localization { get; }

    public BdInfoExaminer BluRayExaminer { get; }

    public MediaEncoder Encoder { get; }

    public PathManager PathManager { get; }

    public SubtitleEncoder SubtitleEncoder { get; }

    public AttachmentExtractor AttachmentExtractor { get; }

    public EncodingHelper EncodingHelper { get; }

    public SkiaEncoder ImageEncoder { get; }

    public TrickplayManager Trickplay { get; }

    public TrueCacheLoader Loader { get; }

    public TrueCacheRequirements Requirements
    {
        get
        {
            var configuration = Config.Configuration;
            return new TrueCacheRequirements(configuration.TrickplayOptions.WidthResolutions, configuration.TrickplayOptions.Interval, configuration.DummyChapterDuration);
        }
    }

    /// <summary>
    /// Creates the components and checks ffmpeg the way the server does at startup.
    /// </summary>
    /// <param name="options">The command line.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <returns>The components.</returns>
    /// <exception cref="InvalidOperationException">ffmpeg is missing or unsupported.</exception>
    public static async Task<ToolServices> CreateAsync(ToolOptions options, ILoggerFactory loggerFactory)
    {
        var services = new ToolServices(options, loggerFactory);
        try
        {
            await services.Localization.LoadAll().ConfigureAwait(false);
            if (!services.Encoder.SetFFmpegPath())
            {
                throw new InvalidOperationException("ffmpeg was not found or is not supported: " + options.FfmpegPath);
            }

            return services;
        }
        catch
        {
            services.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Encoder.Dispose();
        try
        {
            Directory.Delete(WorkDirectory, true);
        }
        catch (IOException)
        {
        }
    }

    private static void ApplyOverrides(MediaBrowser.Model.Configuration.ServerConfiguration configuration, ToolOptions options)
    {
        var trickplay = configuration.TrickplayOptions;
        if (options.TrickplayWidths is not null)
        {
            trickplay.WidthResolutions = options.TrickplayWidths;
        }

        if (options.TrickplayInterval.HasValue)
        {
            trickplay.Interval = options.TrickplayInterval.Value;
        }

        if (options.TrickplayTile is { } tile)
        {
            trickplay.TileWidth = tile.Width;
            trickplay.TileHeight = tile.Height;
        }

        if (options.DummyChapterDuration.HasValue)
        {
            configuration.DummyChapterDuration = options.DummyChapterDuration.Value;
        }
    }
}
