using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Database.Providers.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddHlsTaskCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HlsDeletions",
                columns: table => new
                {
                    RelativeDirectory = table.Column<string>(type: "TEXT", nullable: false),
                    NotBefore = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HlsDeletions", x => x.RelativeDirectory);
                });

            migrationBuilder.CreateTable(
                name: "HlsJobOutputs",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    RelativeDirectory = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    Files = table.Column<string>(type: "TEXT", nullable: false),
                    Segments = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HlsJobOutputs", x => new { x.JobId, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "HlsJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkKey = table.Column<string>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: true),
                    Input = table.Column<string>(type: "TEXT", nullable: true),
                    Plan = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Worker = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseToken = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseExpires = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    DateCreated = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DateModified = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HlsJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HlsMedia",
                columns: table => new
                {
                    Sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    MediaKey = table.Column<string>(type: "TEXT", nullable: false),
                    LibraryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Probe = table.Column<string>(type: "TEXT", nullable: false),
                    DateOrphaned = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DateCreated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HlsMedia", x => x.Sha256);
                });

            migrationBuilder.CreateTable(
                name: "HlsRenditions",
                columns: table => new
                {
                    Sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PublishedJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RelativeDirectory = table.Column<string>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    Profile = table.Column<string>(type: "TEXT", nullable: true),
                    DatePublished = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HlsRenditions", x => new { x.Sha256, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "HlsSources",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LibraryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Length = table.Column<long>(type: "INTEGER", nullable: false),
                    DateModified = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HlsSources", x => x.ItemId);
                });

            migrationBuilder.CreateTable(
                name: "HlsVersionItems",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Quality = table.Column<int>(type: "INTEGER", nullable: false),
                    VersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Signature = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HlsVersionItems", x => new { x.ItemId, x.Quality });
                });

            migrationBuilder.CreateIndex(
                name: "IX_HlsDeletions_NotBefore",
                table: "HlsDeletions",
                column: "NotBefore");

            migrationBuilder.CreateIndex(
                name: "IX_HlsJobs_Sha256",
                table: "HlsJobs",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_HlsJobs_Status_DateCreated",
                table: "HlsJobs",
                columns: new[] { "Status", "DateCreated" });

            migrationBuilder.CreateIndex(
                name: "IX_HlsJobs_WorkKey",
                table: "HlsJobs",
                column: "WorkKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HlsMedia_MediaKey",
                table: "HlsMedia",
                column: "MediaKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HlsRenditions_JobId",
                table: "HlsRenditions",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_HlsSources_Sha256",
                table: "HlsSources",
                column: "Sha256");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HlsDeletions");

            migrationBuilder.DropTable(
                name: "HlsJobOutputs");

            migrationBuilder.DropTable(
                name: "HlsJobs");

            migrationBuilder.DropTable(
                name: "HlsMedia");

            migrationBuilder.DropTable(
                name: "HlsRenditions");

            migrationBuilder.DropTable(
                name: "HlsSources");

            migrationBuilder.DropTable(
                name: "HlsVersionItems");
        }
    }
}
