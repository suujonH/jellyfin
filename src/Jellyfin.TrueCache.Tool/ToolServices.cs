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
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.TrueCache.Tool;

/// <summary>
/// The server components the generator runs, wired as the server wires them but in a private work folder.
/// Components the extraction code does not use are left out.
/// </summary>
internal sealed class ToolServices : IDisposable
{
    private ToolServices(ToolOptions options, ILoggerFactory loggerFactory, ServerSettings? serverSettings)
    {
        WorkDirectory = Path.Combine(Path.GetTempPath(), "jellyfin-truecache-" + Guid.NewGuid().ToString("N"));
        var configDirectory = Path.Combine(WorkDirectory, "config");
        Directory.CreateDirectory(configDirectory);
        var xmlSerializer = new MyXmlSerializer();

        // The server's own settings decide what an entry must hold: those it reports, or a copy of its files, which keeps its folder
        // untouched. Its paths are those of the server's machine and stay out: the tool works in its own folder and its own ffmpeg.
        var (system, encoding) = serverSettings is not null
            ? (serverSettings.System, serverSettings.Encoding)
            : (ReadServerFile<ServerConfiguration>(xmlSerializer, options.ServerConfigDirectory, "system.xml"),
                ReadServerFile<EncodingOptions>(xmlSerializer, options.ServerConfigDirectory, "encoding.xml"));
        if (system is not null)
        {
            system.CachePath = null;
            system.MetadataPath = string.Empty;
            xmlSerializer.SerializeToFile(system, Path.Combine(configDirectory, "system.xml"));
        }

        if (encoding is not null)
        {
            encoding.TranscodingTempPath = null!;
            encoding.EncoderAppPath = null!;
            xmlSerializer.SerializeToFile(encoding, Path.Combine(configDirectory, "encoding.xml"));
        }

        LoggerFactory = loggerFactory;
        AppPaths = new ServerApplicationPaths(
            Path.Combine(WorkDirectory, "data"),
            Path.Combine(WorkDirectory, "log"),
            configDirectory,
            Path.Combine(WorkDirectory, "cache"),
            Path.Combine(WorkDirectory, "web"));
        Config = new ServerConfigurationManager(AppPaths, loggerFactory, xmlSerializer);
        Config.AddParts([new EncodingConfigurationFactory()]);
        NetworkStorage = ToolNetworkStorage.Load(options.ServerDataDirectory);
        ApplyOverrides(Config.Configuration, options);
        ApplyHardware(Config.GetEncodingOptions(), Config.Configuration.TrickplayOptions, options);

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
    /// <exception cref="InvalidOperationException">ffmpeg is missing or unsupported, or the settings of the server cannot be read.</exception>
    public static async Task<ToolServices> CreateAsync(ToolOptions options, ILoggerFactory loggerFactory)
    {
        var serverSettings = options.ServerUrl is null ? null : await ServerSettings.FetchAsync(options.ServerUrl, options.ApiKey!).ConfigureAwait(false);
        var services = new ToolServices(options, loggerFactory, serverSettings);
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

    private static T? ReadServerFile<T>(MyXmlSerializer xmlSerializer, string? directory, string name)
        where T : class
    {
        var path = directory is null ? null : Path.Combine(directory, name);
        return path is not null && File.Exists(path) ? xmlSerializer.DeserializeFromFile(typeof(T), path) as T : null;
    }

    private static void ApplyOverrides(ServerConfiguration configuration, ToolOptions options)
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

    // The machine the tool runs on decodes with its own hardware, which the server's settings do not describe.
    private static void ApplyHardware(EncodingOptions encoding, TrickplayOptions trickplay, ToolOptions options)
    {
        if (options.HardwareAcceleration is not { } type)
        {
            return;
        }

        encoding.HardwareAccelerationType = type;
        trickplay.EnableHwAcceleration = type != HardwareAccelerationType.none;
        trickplay.EnableHwEncoding = false;
        if (type == HardwareAccelerationType.none)
        {
            return;
        }

        // What every GPU of recent years decodes; AV1 needs a newer one.
        encoding.HardwareDecodingCodecs = ["h264", "hevc", "mpeg2video", "vc1", "vp9"];
        encoding.EnableDecodingColorDepth10Hevc = true;
        encoding.EnableDecodingColorDepth10Vp9 = true;
        if (options.HardwareDevice is { } device)
        {
            if (type == HardwareAccelerationType.vaapi)
            {
                encoding.VaapiDevice = device;
            }
            else
            {
                // The server always takes CUDA device 0; ffmpeg, started by this process, sees only the chosen one.
                Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", device);
            }
        }
    }
}
