using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Naming.Common;
using MediaBrowser.Controller.TrueCache;
using Microsoft.Extensions.Logging;

namespace Jellyfin.TrueCache.Tool;

/// <summary>
/// The entry point of the generator.
/// </summary>
public static class Program
{
    private static readonly string[] _bookExtensions = [".azw", ".azw3", ".cb7", ".cbr", ".cbt", ".cbz", ".epub", ".mobi", ".pdf"];

    /// <summary>
    /// Runs a command.
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <returns>0 when every entry is complete, 1 when one is not, 2 for an invalid command line.</returns>
    public static async Task<int> Main(string[] args)
    {
        ToolOptions options;
        try
        {
            options = ToolOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message + Environment.NewLine + Environment.NewLine + ToolOptions.Usage).ConfigureAwait(false);
            return 2;
        }

        // Tag values must be split exactly as the audio prober configures ATL.
        ATL.Settings.DisplayValueSeparator = TrueCacheAudioTags.ValueSeparator[0];
        ATL.Settings.UseFileNameWhenNoTitle = false;
        ATL.Settings.ID3v2_separatev2v3Values = false;

        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleErrorLoggerProvider()).SetMinimumLevel(LogLevel.Warning));
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            using var services = await ToolServices.CreateAsync(options, loggerFactory).ConfigureAwait(false);
            return await RunAsync(options, services, cancellation.Token).ConfigureAwait(false) ? 0 : 1;
        }
        catch (InvalidOperationException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<bool> RunAsync(ToolOptions options, ToolServices services, CancellationToken cancellationToken)
    {
        var naming = new NamingOptions();
        var generator = new EntryGenerator(services);
        var success = true;

        foreach (var argument in options.Paths)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(argument));
            TrueCacheLookup result;
            try
            {
                if (options.Command == "album")
                {
                    var albumEntry = TrueCacheLayout.GetAlbumEntryDirectory(path);
                    if (!ShouldGenerate(albumEntry, options.Force))
                    {
                        Console.WriteLine($"skipped  {path}: an album entry exists (use --force)");
                        continue;
                    }

                    result = await generator.GenerateAlbumAsync(path, naming.AudioFileExtensions, cancellationToken).ConfigureAwait(false);
                }
                else if (options.Command == "validate")
                {
                    result = services.Loader.Load(path, services.Requirements);
                }
                else if (options.Command == "strm")
                {
                    var kind = options.Kind ?? DetectShortcutKind(path, naming);
                    var output = Path.GetFullPath(options.OutputDirectory!);
                    if (!ShouldGenerate(Path.Combine(output, options.EntryName!), options.Force))
                    {
                        Console.WriteLine($"skipped  {path}: an entry exists in {output} (use --force)");
                        continue;
                    }

                    result = await generator.GenerateForShortcutAsync(path, options.EntryName!, output, kind, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var kind = options.Kind ?? DetectKind(path, naming, services);
                    var entry = TrueCacheLayout.GetEntryDirectory(path)!;
                    if (!ShouldGenerate(entry, options.Force))
                    {
                        Console.WriteLine($"skipped  {path}: an entry exists (use --force)");
                        continue;
                    }

                    result = await generator.GenerateAsync(path, kind, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = TrueCacheLookup.Rejected(ex.Message);
            }

            switch (result.State)
            {
                case TrueCacheState.Valid:
                    Console.WriteLine($"complete {path}");
                    break;
                case TrueCacheState.Rejected:
                    success = false;
                    Console.WriteLine($"rejected {path}: {result.Reason}");
                    break;
                default:
                    success = false;
                    Console.WriteLine($"missing  {path}: no entry");
                    break;
            }
        }

        return success;
    }

    // An existing entry is not removed here: the generator replaces it only once the new entry is complete.
    private static bool ShouldGenerate(string entry, bool force) => force || !Directory.Exists(entry);

    private static TrueCacheKind DetectShortcutKind(string path, NamingOptions naming)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("The media file does not exist.");
        }

        var extension = Path.GetExtension(path);
        if (naming.VideoFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return TrueCacheKind.Video;
        }

        return naming.AudioFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? TrueCacheKind.Audio
            : throw new InvalidOperationException("A .strm names a video or an audio file; pass --kind for " + extension + " files.");
    }

    private static TrueCacheKind DetectKind(string path, NamingOptions naming, ToolServices services)
    {
        if (Directory.Exists(path))
        {
            if (Directory.Exists(Path.Combine(path, "VIDEO_TS")) || File.Exists(Path.Combine(path, "VIDEO_TS.IFO")))
            {
                return TrueCacheKind.Dvd;
            }

            if (Directory.Exists(Path.Combine(path, "BDMV")))
            {
                return TrueCacheKind.BluRay;
            }

            throw new InvalidOperationException("The folder is neither a DVD nor a Blu-ray folder.");
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException("The path does not exist.");
        }

        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".strm", StringComparison.OrdinalIgnoreCase)
            || naming.VideoFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return TrueCacheKind.Video;
        }

        if (naming.AudioFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return TrueCacheKind.Audio;
        }

        if (_bookExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return TrueCacheKind.Book;
        }

        if (services.ImageEncoder.SupportedInputFormats.Contains(extension.TrimStart('.'), StringComparer.OrdinalIgnoreCase))
        {
            return TrueCacheKind.Photo;
        }

        throw new InvalidOperationException("The kind of " + extension + " files is unknown; pass --kind.");
    }

    private sealed class ConsoleErrorLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ConsoleErrorLogger(categoryName);

        public void Dispose()
        {
        }

        private sealed class ConsoleErrorLogger(string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                Console.Error.WriteLine($"{logLevel}: {category}: {formatter(state, exception)}");
                if (exception is not null)
                {
                    Console.Error.WriteLine(exception.Message);
                }
            }
        }
    }
}
