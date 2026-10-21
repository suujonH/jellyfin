using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>
/// NetworkStorageFailure Configuration.
/// </summary>
public class NetworkStorageFailureConfiguration : IEntityTypeConfiguration<NetworkStorageFailure>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<NetworkStorageFailure> builder)
    {
        builder.HasKey(e => e.ItemId);
        builder.HasIndex(e => e.StorageKey);
        builder.HasOne(e => e.Item).WithMany().HasForeignKey(e => e.ItemId).OnDelete(DeleteBehavior.Cascade);
    }
}
