using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Database.Providers.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddNetworkStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NetworkStorageAccesses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Endpoint = table.Column<string>(type: "TEXT", nullable: true),
                    Bucket = table.Column<string>(type: "TEXT", nullable: false),
                    Region = table.Column<string>(type: "TEXT", nullable: true),
                    UsePathStyle = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccessKey = table.Column<string>(type: "TEXT", nullable: true),
                    SecretKey = table.Column<string>(type: "TEXT", nullable: true),
                    SessionToken = table.Column<string>(type: "TEXT", nullable: true),
                    UrlLifetimeSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Settings = table.Column<string>(type: "TEXT", nullable: true),
                    DateCreated = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DateModified = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetworkStorageAccesses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NetworkStorageFailures",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StorageKey = table.Column<string>(type: "TEXT", nullable: false),
                    Target = table.Column<string>(type: "TEXT", nullable: false),
                    Issue = table.Column<int>(type: "INTEGER", nullable: false),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    DateFirstFailed = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DateLastFailed = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetworkStorageFailures", x => x.ItemId);
                    table.ForeignKey(
                        name: "FK_NetworkStorageFailures_BaseItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "BaseItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NetworkStorageExternalAddresses",
                columns: table => new
                {
                    AccessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsPublic = table.Column<bool>(type: "INTEGER", nullable: false),
                    AuthMode = table.Column<int>(type: "INTEGER", nullable: true),
                    BaseUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Secret = table.Column<string>(type: "TEXT", nullable: true),
                    UrlLifetimeSeconds = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetworkStorageExternalAddresses", x => x.AccessId);
                    table.ForeignKey(
                        name: "FK_NetworkStorageExternalAddresses_NetworkStorageAccesses_AccessId",
                        column: x => x.AccessId,
                        principalTable: "NetworkStorageAccesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NetworkStorageAccesses_Protocol_Bucket",
                table: "NetworkStorageAccesses",
                columns: new[] { "Protocol", "Bucket" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NetworkStorageFailures_StorageKey",
                table: "NetworkStorageFailures",
                column: "StorageKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NetworkStorageExternalAddresses");

            migrationBuilder.DropTable(
                name: "NetworkStorageFailures");

            migrationBuilder.DropTable(
                name: "NetworkStorageAccesses");
        }
    }
}
