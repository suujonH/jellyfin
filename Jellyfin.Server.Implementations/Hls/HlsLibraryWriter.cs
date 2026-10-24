using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net.S3;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Hls;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// Writes the files HLS workers add to libraries into the library folders, and records them.
/// </summary>
public sealed class HlsLibraryWriter : IHlsLibraryWriter, IDisposable
{
    private const int ChunkLimit = 16 << 20;
    private const int BufferSize = 1 << 20;
    private const long MaxLength = 10L << 40;
    private const long MaxEntrySize = 1L << 30;
    private const int MaxPath = 1024;
    private const int MaxTarget = 4096;
    private const string EntriesDirectory = "__true_cache__";
    private const string WorkingDirectory = "__working__";

    private static readonly TimeSpan _uploadIdle = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, Upload> _uploads = new(StringComparer.Ordinal);

    // Claims, reads and releases of the working markers, and the files put in place for an operation, take turns.
    private readonly SemaphoreSlim _working = new(1, 1);
    private readonly ILibraryManager _libraryManager;
    private readonly HlsJobStore _store;
    private readonly ILogger<HlsLibraryWriter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsLibraryWriter"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="store">The data of pre-encoded HLS, where the files are recorded.</param>
    /// <param name="logger">The logger.</param>
    public HlsLibraryWriter(ILibraryManager libraryManager, HlsJobStore store, ILogger<HlsLibraryWriter> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _logger = logger;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <inheritdoc />
    public void Dispose() => _working.Dispose();

    /// <inheritdoc />
    public async Task<string> StartAsync(Guid libraryId, HlsLibraryUploadStart request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = DateTime.UtcNow;
        await RemoveIdleAsync(now).ConfigureAwait(false);

        var final = GetFinalPath(libraryId, request.Location, request.Path);
        if (request.Length is < 0 or > MaxLength)
        {
            throw new ArgumentException("The length of a file is 0 to 10 TiB.", nameof(request));
        }

        if (request.WorkerId.Equals(Guid.Empty))
        {
            throw new ArgumentException("The worker id is required; register the worker first.", nameof(request));
        }

        // The hidden name keeps the scan of the library away from the file until it is complete.
        var directory = Path.GetDirectoryName(final)!;
        Directory.CreateDirectory(directory);
        var uploadId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var temporary = Path.Combine(directory, ".hls-upload-" + uploadId + ".part");
        await new FileStream(temporary, FileMode.CreateNew, FileAccess.Write).DisposeAsync().ConfigureAwait(false);
        _uploads[uploadId] = new Upload(libraryId, request.WorkerId, final, temporary, request.Length) { LastTouched = now };
        return uploadId;
    }

    /// <inheritdoc />
    public async Task<bool> WriteAsync(string uploadId, long offset, Stream body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!_uploads.TryGetValue(uploadId, out var upload))
        {
            return false;
        }

        await upload.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (upload.Finished)
            {
                return false;
            }

            if (offset < 0 || offset > upload.Written)
            {
                throw new ArgumentException("The chunk does not follow what was written, " + upload.Written + " bytes.", nameof(offset));
            }

            // A chunk sent again drops what a failed attempt left after it.
            var written = 0L;
            var file = new FileStream(upload.TemporaryPath, FileMode.Open, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
            await using (file.ConfigureAwait(false))
            {
                file.SetLength(offset);
                upload.Written = offset;
                file.Position = offset;
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > ChunkLimit)
                    {
                        throw new ArgumentException("A chunk is at most 16 MiB.", nameof(body));
                    }

                    if (offset + written > upload.Length)
                    {
                        throw new ArgumentException("The chunk goes past the length of the file, " + upload.Length + " bytes.", nameof(body));
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            upload.Written = offset + written;
            upload.LastTouched = DateTime.UtcNow;
            return true;
        }
        finally
        {
            upload.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(string uploadId, HlsLibraryUploadComplete request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_uploads.TryGetValue(uploadId, out var upload))
        {
            return false;
        }

        await upload.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<HlsLibraryFileRecord> files;
        var held = false;
        try
        {
            if (upload.Finished)
            {
                return false;
            }

            upload.Finished = true;
            _uploads.TryRemove(uploadId, out _);
            if (upload.Written != upload.Length || new FileInfo(upload.TemporaryPath).Length != upload.Length)
            {
                throw new ArgumentException("The upload is not complete: " + upload.Written + " of " + upload.Length + " bytes were written.", nameof(uploadId));
            }

            // The file goes in place only while the operation holds it, and no claim comes in between.
            if (request.OperationId is { } operation)
            {
                ValidateOperationId(operation);
                await _working.WaitAsync(cancellationToken).ConfigureAwait(false);
                held = true;
                if (!string.Equals(ReadMarker(GetMarker(upload.FinalPath)), operation, StringComparison.Ordinal))
                {
                    throw new HlsWorkingTakenException("The file " + upload.FinalPath + " is held by another operation.");
                }
            }

            files = request.ShortcutTarget is null ? PutFile(upload) : PutShortcut(upload, request.ShortcutTarget);
        }
        finally
        {
            if (held)
            {
                _working.Release();
            }

            TryDeleteFile(upload.TemporaryPath);
            upload.Gate.Release();
        }

        _logger.LogInformation("Worker {WorkerId} wrote {Path} into library {LibraryId}", upload.WorkerId, upload.FinalPath, upload.LibraryId);
        await _store.RecordLibraryFilesAsync(files, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> AbortAsync(string uploadId)
    {
        if (!_uploads.TryRemove(uploadId, out var upload))
        {
            return false;
        }

        await upload.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            upload.Finished = true;
            TryDeleteFile(upload.TemporaryPath);
        }
        finally
        {
            upload.Gate.Release();
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<string?> ClaimWorkingAsync(Guid libraryId, HlsWorkingClaim request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateOperationId(request.OperationId);
        var marker = GetMarker(GetFinalPath(libraryId, request.Location, request.Path));
        var directory = Path.GetDirectoryName(marker)!;
        var temporary = Path.Combine(directory, "." + Path.GetFileName(marker) + "-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12]);
        await _working.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = ReadMarker(marker);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(temporary, request.OperationId + "\n", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, marker, true);
            _logger.LogInformation("Operation {OperationId} holds {Path} in library {LibraryId}, held before by {Previous}", request.OperationId, request.Path, libraryId, previous ?? "none");
            return previous;
        }
        finally
        {
            TryDeleteFile(temporary);
            _working.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetWorkingAsync(Guid libraryId, HlsWorkingPath request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var marker = GetMarker(GetFinalPath(libraryId, request.Location, request.Path));
        await _working.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ReadMarker(marker);
        }
        finally
        {
            _working.Release();
        }
    }

    /// <inheritdoc />
    public async Task ReleaseWorkingAsync(Guid libraryId, HlsWorkingRelease request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateOperationId(request.OperationId);
        var marker = GetMarker(GetFinalPath(libraryId, request.Location, request.Path));
        await _working.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(ReadMarker(marker), request.OperationId, StringComparison.Ordinal))
            {
                return;
            }

            File.Delete(marker);
            var directory = Path.GetDirectoryName(marker)!;
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Hidden from the scan of the library; an empty folder left behind does no harm.
            }
        }
        finally
        {
            _working.Release();
        }
    }

    // The marker of a file: Dir/__working__/Film.strm for Dir/Film.strm.
    private static string GetMarker(string final) => Path.Combine(Path.GetDirectoryName(final)!, WorkingDirectory, Path.GetFileName(final));

    private static bool IsOperationId(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigitLower);

    private static void ValidateOperationId(string? id)
    {
        if (!IsOperationId(id))
        {
            throw new ArgumentException("The operation id is 32 lower-case hexadecimal characters.", nameof(id));
        }
    }

    // The operation a marker names; null when there is none, or what is there is not an operation id.
    private static string? ReadMarker(string marker)
    {
        if (!File.Exists(marker))
        {
            return null;
        }

        var text = File.ReadAllText(marker).Trim();
        return IsOperationId(text) ? text : null;
    }

    // Where a file of a library goes: in the folder named, or the only one, by a path that stays in it.
    private string GetFinalPath(Guid libraryId, string? location, string path)
    {
        var folder = _libraryManager.GetVirtualFolders().FirstOrDefault(f => Guid.TryParse(f.ItemId, out var id) && id.Equals(libraryId))
            ?? throw new ArgumentException("Library " + libraryId + " does not exist.", nameof(libraryId));
        var root = GetLocation(folder, location);
        ValidateRelative(path);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(root))
        {
            throw new IOException("The library folder " + root + " does not exist or cannot be read.");
        }

        // A root of the file system, such as / or D:\, keeps its separator.
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var final = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!final.StartsWith(prefix, PathComparison))
        {
            throw new ArgumentException("The path " + path + " is not in the library folder.", nameof(path));
        }

        return final;
    }

    // The folder of the library a file goes to: the one named, or the only one.
    private static string GetLocation(VirtualFolderInfo folder, string? location)
    {
        var locations = folder.Locations ?? [];
        if (string.IsNullOrEmpty(location))
        {
            return locations.Length switch
            {
                1 => locations[0],
                0 => throw new ArgumentException("The library " + folder.Name + " has no folder.", nameof(location)),
                _ => throw new ArgumentException("The library " + folder.Name + " has several folders; name the one to write to.", nameof(location))
            };
        }

        var wanted = Path.TrimEndingDirectorySeparator(location);
        return locations.FirstOrDefault(l => string.Equals(Path.TrimEndingDirectorySeparator(l), wanted, PathComparison))
            ?? throw new ArgumentException(location + " is not a folder of the library " + folder.Name + ".", nameof(location));
    }

    // A path in a library folder, with forward slashes; it leaves the folder by no segment and stays out of hidden files, the
    // TrueCache entries and the working markers.
    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPath || path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal) || path.Any(char.IsControl))
        {
            throw new ArgumentException("The path of a file is 1 to 1024 characters, with forward slashes, no colon and no control character.", nameof(path));
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0
                || segment.StartsWith('.')
                || segment.EndsWith('.')
                || segment.EndsWith(' ')
                || string.Equals(segment, EntriesDirectory, StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, WorkingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A segment of the path " + path + " is empty, starts or ends with a dot, ends with a space, or is " + EntriesDirectory + " or " + WorkingDirectory + ".", nameof(path));
            }
        }
    }

    // A target a .strm file may name: an S3 object, a file on the machine of a worker, or an http(s) address.
    private static void ValidateTarget(string target)
    {
        if (target.Length is 0 or > MaxTarget
            || target.Any(char.IsControl)
            || !(S3Address.TryParse(target, out _)
                || HlsWorkerLocalAddress.TryParse(target, out _, out _)
                || (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")))
        {
            throw new ArgumentException("The target of a .strm file is one line of s3://, worker-local://, http:// or https://.", nameof(target));
        }
    }

    private static HlsLibraryFileRecord Record(Upload upload, string path) => new(upload.LibraryId, path, new FileInfo(path).Length, upload.WorkerId);

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The upload is over either way; a hidden file left behind is not scanned.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Hidden, so the scan of the library leaves it alone.
        }
    }

    // A plain file replaces what is there; the TrueCache entry of a file it replaces would describe another file, and is put back
    // when the file cannot be replaced.
    private IReadOnlyList<HlsLibraryFileRecord> PutFile(Upload upload)
    {
        var name = Path.GetFileName(upload.FinalPath);
        var entry = Path.Combine(Path.GetDirectoryName(upload.FinalPath)!, EntriesDirectory, name);
        if (!Directory.Exists(entry))
        {
            File.Move(upload.TemporaryPath, upload.FinalPath, true);
            return [Record(upload, upload.FinalPath)];
        }

        var previous = Path.Combine(Path.GetDirectoryName(entry)!, "." + name + "-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12] + "-old");
        Directory.Move(entry, previous);
        try
        {
            File.Move(upload.TemporaryPath, upload.FinalPath, true);
        }
        catch
        {
            Directory.Move(previous, entry);
            throw;
        }

        try
        {
            Directory.Delete(previous, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The old TrueCache entry {Path} could not be deleted: {Message}", previous, ex.Message);
        }

        return [Record(upload, upload.FinalPath)];
    }

    // The entry goes in place before the .strm file, so the .strm file is never read without it; an entry it replaces is put
    // back when this fails.
    private List<HlsLibraryFileRecord> PutShortcut(Upload upload, string target)
    {
        var directory = Path.GetDirectoryName(upload.FinalPath)!;
        var name = Path.GetFileName(upload.FinalPath);
        if (!name.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A TrueCache entry is uploaded for a .strm file, not " + name + ".", nameof(upload));
        }

        ValidateTarget(target);
        var entries = Path.Combine(directory, EntriesDirectory);
        var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
        var staging = Path.Combine(entries, "." + name + "-" + suffix);
        var text = Path.Combine(directory, ".hls-strm-" + suffix);
        try
        {
            Extract(upload.TemporaryPath, staging);
            File.WriteAllText(text, target + "\n", new UTF8Encoding(false));

            var final = Path.Combine(entries, name);
            var previous = staging + "-old";
            var replaced = Directory.Exists(final);
            if (replaced)
            {
                Directory.Move(final, previous);
            }

            try
            {
                Directory.Move(staging, final);
                File.Move(text, upload.FinalPath, true);
            }
            catch when (!replaced)
            {
                // The .strm file there, if any, is not of the new entry.
                TryDeleteDirectory(final);
                throw;
            }
            catch
            {
                // The .strm file there is still the one the previous entry is of.
                TryDeleteDirectory(final);
                Directory.Move(previous, final);
                throw;
            }

            if (replaced)
            {
                try
                {
                    Directory.Delete(previous, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("The old TrueCache entry {Path} could not be deleted: {Message}", previous, ex.Message);
                }
            }

            var files = new List<HlsLibraryFileRecord> { Record(upload, upload.FinalPath) };
            files.AddRange(Directory.EnumerateFiles(final, "*", SearchOption.AllDirectories).Select(f => Record(upload, f)));
            return files;
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteFile(text);
        }
    }

    // Extracts the zip of an entry; one that leaves the directory or is too large is refused before anything is written.
    private static void Extract(string zipPath, string directory)
    {
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(zipPath);
        }
        catch (InvalidDataException ex)
        {
            throw new ArgumentException("The TrueCache entry is not a zip: " + ex.Message, nameof(zipPath), ex);
        }

        using (zip)
        {
            var size = 0L;
            foreach (var entry in zip.Entries)
            {
                size += entry.Length;
                if (size > MaxEntrySize || !Path.GetFullPath(Path.Combine(root, entry.FullName)).StartsWith(root, PathComparison))
                {
                    throw new ArgumentException("The TrueCache entry is larger than 1 GiB, or has a file outside of it: " + entry.FullName, nameof(zipPath));
                }
            }

            Directory.CreateDirectory(directory);
            zip.ExtractToDirectory(directory);
        }
    }

    // Uploads a worker left are dropped after a day.
    private async Task RemoveIdleAsync(DateTime now)
    {
        foreach (var (id, upload) in _uploads)
        {
            if (upload.LastTouched < now - _uploadIdle && await upload.Gate.WaitAsync(0).ConfigureAwait(false))
            {
                try
                {
                    if (!upload.Finished && _uploads.TryRemove(id, out _))
                    {
                        upload.Finished = true;
                        TryDeleteFile(upload.TemporaryPath);
                    }
                }
                finally
                {
                    upload.Gate.Release();
                }
            }
        }
    }

    private sealed class Upload(Guid libraryId, Guid workerId, string finalPath, string temporaryPath, long length)
    {
        public Guid LibraryId { get; } = libraryId;

        public Guid WorkerId { get; } = workerId;

        public string FinalPath { get; } = finalPath;

        public string TemporaryPath { get; } = temporaryPath;

        public long Length { get; } = length;

        public long Written { get; set; }

        public DateTime LastTouched { get; set; }

        public bool Finished { get; set; }

        // Never disposed: it is only waited on asynchronously, so it holds no handle.
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
