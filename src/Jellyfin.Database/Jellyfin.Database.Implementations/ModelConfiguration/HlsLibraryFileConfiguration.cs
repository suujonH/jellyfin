using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>
/// HlsLibraryFile Configuration.
/// </summary>
public class HlsLibraryFileConfiguration : IEntityTypeConfiguration<HlsLibraryFile>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<HlsLibraryFile> builder)
    {
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.Path);
    }
}
