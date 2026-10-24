using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Database.Providers.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddHlsWorkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WorkerId",
                table: "HlsRenditions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkerId",
                table: "HlsJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HlsLibraryFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LibraryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Length = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DateCreated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HlsLibraryFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HlsWorkers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    DateCreated = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DateLastSeen = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HlsWorkers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HlsLibraryFiles_Path",
                table: "HlsLibraryFiles",
                column: "Path");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HlsLibraryFiles");

            migrationBuilder.DropTable(
                name: "HlsWorkers");

            migrationBuilder.DropColumn(
                name: "WorkerId",
                table: "HlsRenditions");

            migrationBuilder.DropColumn(
                name: "WorkerId",
                table: "HlsJobs");
        }
    }
}
