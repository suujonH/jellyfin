using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>
/// HlsRendition Configuration.
/// </summary>
public class HlsRenditionConfiguration : IEntityTypeConfiguration<HlsRendition>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<HlsRendition> builder)
    {
        builder.HasKey(e => new { e.Sha256, e.Key });
        builder.HasIndex(e => e.JobId);
    }
}
