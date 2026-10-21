using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>
/// NetworkStorageExternalAddress Configuration.
/// </summary>
public class NetworkStorageExternalAddressConfiguration : IEntityTypeConfiguration<NetworkStorageExternalAddress>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<NetworkStorageExternalAddress> builder)
    {
        builder.HasKey(e => e.AccessId);
    }
}
