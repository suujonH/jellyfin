using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>
/// HlsDeletion Configuration.
/// </summary>
public class HlsDeletionConfiguration : IEntityTypeConfiguration<HlsDeletion>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<HlsDeletion> builder)
    {
        builder.HasKey(e => e.RelativeDirectory);
        builder.HasIndex(e => e.NotBefore);
    }
}
