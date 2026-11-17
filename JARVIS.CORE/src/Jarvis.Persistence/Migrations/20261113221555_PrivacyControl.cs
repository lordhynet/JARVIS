using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PrivacyControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrivacyControls",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Epoch = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Erased = table.Column<bool>(type: "boolean", nullable: false),
                    ErasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErasureRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    ErasureKeyFingerprint = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrivacyControls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrivacyJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Fingerprint = table.Column<string>(type: "text", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CountsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrivacyJobs", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "PrivacyControls",
                columns: new[] { "Id", "Epoch", "Erased", "ErasedAt", "ErasureKeyFingerprint", "ErasureRequestId", "Revision" },
                values: new object[] { 1, 0L, false, null, null, null, 1 });

            migrationBuilder.CreateIndex(
                name: "IX_PrivacyJobs_CompletedAt",
                table: "PrivacyJobs",
                column: "CompletedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrivacyControls");

            migrationBuilder.DropTable(
                name: "PrivacyJobs");
        }
    }
}
