using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>
/// NetworkStorageAccess Configuration.
/// </summary>
public class NetworkStorageAccessConfiguration : IEntityTypeConfiguration<NetworkStorageAccess>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<NetworkStorageAccess> builder)
    {
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.Protocol, e.Bucket }).IsUnique();
        builder.HasOne(e => e.ExternalAddress)
            .WithOne(e => e.Access)
            .HasForeignKey<NetworkStorageExternalAddress>(e => e.AccessId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
