using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>
/// HlsMedia Configuration.
/// </summary>
public class HlsMediaConfiguration : IEntityTypeConfiguration<HlsMedia>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<HlsMedia> builder)
    {
        builder.HasKey(e => e.Sha256);
        builder.HasIndex(e => e.MediaKey).IsUnique();
    }
}
