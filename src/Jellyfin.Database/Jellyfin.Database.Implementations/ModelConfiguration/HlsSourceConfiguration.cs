using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>
/// HlsSource Configuration.
/// </summary>
public class HlsSourceConfiguration : IEntityTypeConfiguration<HlsSource>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<HlsSource> builder)
    {
        builder.HasKey(e => e.ItemId);
        builder.HasIndex(e => e.Sha256);
    }
}
