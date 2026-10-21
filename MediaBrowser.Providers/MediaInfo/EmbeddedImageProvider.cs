#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Providers.MediaInfo
{
    /// <summary>
    /// Uses <see cref="IMediaEncoder"/> to extract embedded images.
    /// </summary>
    public class EmbeddedImageProvider : IDynamicImageProvider, IHasOrder
    {
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly ILogger<EmbeddedImageProvider> _logger;
        private readonly ITrueCacheService _trueCache;

        /// <summary>
        /// Initializes a new instance of the <see cref="EmbeddedImageProvider"/> class.
        /// </summary>
        /// <param name="mediaSourceManager">The media source manager for fetching item streams and attachments.</param>
        /// <param name="mediaEncoder">The media encoder for extracting attached/embedded images.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="trueCache">The <c>__true_cache__</c> lookup.</param>
        public EmbeddedImageProvider(IMediaSourceManager mediaSourceManager, IMediaEncoder mediaEncoder, ILogger<EmbeddedImageProvider> logger, ITrueCacheService trueCache)
        {
            _mediaSourceManager = mediaSourceManager;
            _mediaEncoder = mediaEncoder;
            _logger = logger;
            _trueCache = trueCache;
        }

        /// <inheritdoc />
        public string Name => "Embedded Image Extractor";

        /// <inheritdoc />
        // Default to after internet image providers but before Screen Grabber
        public int Order => 99;

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            if (item is Video)
            {
                if (item is Episode)
                {
                    return new[]
                    {
                        ImageType.Primary,
                    };
                }

                return TrueCacheRules.EmbeddedImageTypes;
            }

            return Array.Empty<ImageType>();
        }

        /// <inheritdoc />
        public Task<DynamicImageResponse> GetImage(BaseItem item, ImageType type, CancellationToken cancellationToken)
        {
            var video = (Video)item;

            // No support for these
            if (video.IsPlaceHolder || video.VideoType == VideoType.Dvd)
            {
                return Task.FromResult(new DynamicImageResponse { HasImage = false });
            }

            // A declared video is not read: its embedded images come from its __true_cache__ entry.
            var lookup = _trueCache.Lookup(video.Path);
            if (lookup.IsDeclared)
            {
                return Task.FromResult(lookup.IsValid && lookup.Entry.TryGetEmbeddedImage(type, out var cached)
                    ? TrueCacheRules.CreateImageResponse(cached)
                    : new DynamicImageResponse { HasImage = false });
            }

            return GetEmbeddedImage(video, type, cancellationToken);
        }

        private async Task<DynamicImageResponse> GetEmbeddedImage(Video item, ImageType type, CancellationToken cancellationToken)
        {
            MediaSourceInfo mediaSource = new MediaSourceInfo
            {
                VideoType = item.VideoType,
                IsoType = item.IsoType,
                Protocol = item.PathProtocol ?? MediaProtocol.File,
            };

            if (!TrueCacheRules.EmbeddedImageTypes.Contains(type))
            {
                _logger.LogWarning("Attempted to load unexpected image type: {Type}", type);
                return new DynamicImageResponse { HasImage = false };
            }

            // Attachments are tried first, then EmbeddedImage streams.
            var imageStreams = _mediaSourceManager.GetMediaStreams(new MediaStreamQuery
            {
                ItemId = item.Id,
                Type = MediaStreamType.EmbeddedImage
            });
            var (attachment, imageStream, format) = TrueCacheRules.FindEmbeddedImage(type, _mediaSourceManager.GetMediaAttachments(item.Id), imageStreams);

            string extractedImagePath;
            if (attachment is not null)
            {
                extractedImagePath = await _mediaEncoder.ExtractVideoImage(item.Path, item.Container, mediaSource, null, attachment.Index, format, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (imageStream is not null)
            {
                extractedImagePath = await _mediaEncoder.ExtractVideoImage(item.Path, item.Container, mediaSource, imageStream, imageStream.Index, format, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                return new DynamicImageResponse { HasImage = false };
            }

            return new DynamicImageResponse
            {
                Format = format,
                HasImage = true,
                Path = extractedImagePath,
                Protocol = MediaProtocol.File
            };
        }

        /// <inheritdoc />
        public bool Supports(BaseItem item)
        {
            // A .strm is not read, unless its __true_cache__ entry declares what would be read from it.
            if (item.IsShortcut && !_trueCache.Lookup(item.Path).IsDeclared)
            {
                return false;
            }

            if (!item.IsFileProtocol)
            {
                return false;
            }

            return item is Video video && !video.IsPlaceHolder && video.IsCompleteMedia;
        }
    }
}
