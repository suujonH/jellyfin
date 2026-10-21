using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>
/// HlsJob Configuration.
/// </summary>
public class HlsJobConfiguration : IEntityTypeConfiguration<HlsJob>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<HlsJob> builder)
    {
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.WorkKey).IsUnique();
        builder.HasIndex(e => new { e.Status, e.DateCreated });
        builder.HasIndex(e => e.Sha256);
    }
}
