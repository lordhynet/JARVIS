using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GeneratedPracticeQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GeneratedPracticeQuestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Topic = table.Column<int>(type: "integer", nullable: false),
                    OutlineKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OutlineVersion = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: true),
                    Provenance = table.Column<string>(type: "jsonb", nullable: false),
                    Lineage = table.Column<string>(type: "jsonb", nullable: false),
                    Verified = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PurgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneratedPracticeQuestions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedPracticeQuestions_OperationId",
                table: "GeneratedPracticeQuestions",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedPracticeQuestions_SessionId_CreatedAt",
                table: "GeneratedPracticeQuestions",
                columns: new[] { "SessionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedPracticeQuestions_SessionId_Topic_OutlineKey",
                table: "GeneratedPracticeQuestions",
                columns: new[] { "SessionId", "Topic", "OutlineKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback baseline: downgrading below this migration is supported only while no generated question exists.
            // Retained rows (including purged tombstones) may be referenced by canonical attempts and cards that schema 26
            // cannot resolve, so refuse without mutation instead of dropping them.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM "GeneratedPracticeQuestions")
                  THEN RAISE EXCEPTION 'Generated practice questions exist; downgrade below this schema is not supported without an owner restore'; END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "GeneratedPracticeQuestions");
        }
    }
}
