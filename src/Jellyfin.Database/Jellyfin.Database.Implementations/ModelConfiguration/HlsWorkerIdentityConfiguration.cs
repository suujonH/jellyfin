using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>
/// HlsWorkerIdentity Configuration.
/// </summary>
public class HlsWorkerIdentityConfiguration : IEntityTypeConfiguration<HlsWorkerIdentity>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<HlsWorkerIdentity> builder)
    {
        builder.HasKey(e => e.Id);
    }
}
