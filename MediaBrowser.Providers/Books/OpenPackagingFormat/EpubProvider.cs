using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Providers.Books.OpenPackagingFormat
{
    /// <summary>
    /// Provides book metadata from OPF content in an EPUB item.
    /// </summary>
    public class EpubProvider : ILocalMetadataProvider<Book>
    {
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<EpubProvider> _logger;
        private readonly ITrueCacheService _trueCache;

        /// <summary>
        /// Initializes a new instance of the <see cref="EpubProvider"/> class.
        /// </summary>
        /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{EpubProvider}"/> interface.</param>
        /// <param name="trueCache">Instance of the <see cref="ITrueCacheService"/> interface.</param>
        public EpubProvider(IFileSystem fileSystem, ILogger<EpubProvider> logger, ITrueCacheService trueCache)
        {
            _fileSystem = fileSystem;
            _logger = logger;
            _trueCache = trueCache;
        }

        /// <inheritdoc />
        public string Name => "EPUB Metadata";

        /// <inheritdoc />
        public Task<MetadataResult<Book>> GetMetadata(ItemInfo info, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            var path = GetEpubFile(info.Path)?.FullName;

            if (path is null)
            {
                return Task.FromResult(new MetadataResult<Book> { HasMetadata = false });
            }

            // A declared EPUB is not opened: its package document comes from its __true_cache__ entry.
            var lookup = _trueCache.Lookup(path);
            if (lookup.IsDeclared)
            {
                if (!lookup.IsValid || lookup.Entry.Manifest.Book?.Opf != true)
                {
                    return Task.FromResult(new MetadataResult<Book> { HasMetadata = false });
                }

                var opfDocument = new XmlDocument();
                opfDocument.Load(lookup.Entry.GetBookFile(TrueCacheLayout.OpfFile));
                return Task.FromResult(new OpfReader<EpubProvider>(opfDocument, _logger).ReadOpfData(cancellationToken));
            }

            var result = ReadEpubAsZip(path, cancellationToken);

            if (result is null)
            {
                return Task.FromResult(new MetadataResult<Book> { HasMetadata = false });
            }
            else
            {
                return Task.FromResult(result);
            }
        }

        private FileSystemMetadata? GetEpubFile(string path)
        {
            var fileInfo = _fileSystem.GetFileSystemInfo(path);

            if (fileInfo.IsDirectory)
            {
                return null;
            }

            if (!string.Equals(Path.GetExtension(fileInfo.FullName), ".epub", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return fileInfo;
        }

        private MetadataResult<Book>? ReadEpubAsZip(string path, CancellationToken cancellationToken)
        {
            using var epub = ZipFile.OpenRead(path);

            var opfFilePath = EpubUtils.ReadContentFilePath(epub);
            if (opfFilePath == null)
            {
                return null;
            }

            var opf = epub.GetEntry(opfFilePath);
            if (opf == null)
            {
                return null;
            }

            using var opfStream = opf.Open();

            var opfDocument = new XmlDocument();
            opfDocument.Load(opfStream);

            var utilities = new OpfReader<EpubProvider>(opfDocument, _logger);
            return utilities.ReadOpfData(cancellationToken);
        }
    }
}
