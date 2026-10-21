using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>
/// HlsVersionItem Configuration.
/// </summary>
public class HlsVersionItemConfiguration : IEntityTypeConfiguration<HlsVersionItem>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<HlsVersionItem> builder)
    {
        builder.HasKey(e => new { e.ItemId, e.Quality });
    }
}
