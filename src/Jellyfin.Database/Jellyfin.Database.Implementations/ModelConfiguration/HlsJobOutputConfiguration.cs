using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>
/// HlsJobOutput Configuration.
/// </summary>
public class HlsJobOutputConfiguration : IEntityTypeConfiguration<HlsJobOutput>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<HlsJobOutput> builder)
    {
        builder.HasKey(e => new { e.JobId, e.Key });
    }
}
