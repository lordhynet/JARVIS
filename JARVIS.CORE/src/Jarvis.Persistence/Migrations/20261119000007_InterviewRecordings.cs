using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InterviewRecordings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InterviewRecordings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InterviewRecordings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecordingChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Track = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordingChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecordingChunks_InterviewRecordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "InterviewRecordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecordingChunks_RecordingId_Track_Sequence",
                table: "RecordingChunks",
                columns: new[] { "RecordingId", "Track", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Old binaries have no capture jobs. Remove their per-device voice exclusion as well.
            migrationBuilder.Sql("""
                UPDATE "RuntimeAudioState" SET "DevicesJson" = COALESCE(
                    (SELECT jsonb_object_agg(key, value - 'Recording') FROM jsonb_each("DevicesJson")), '{}'::jsonb)
                WHERE "Id" = 1;
                """);
            migrationBuilder.DropTable(
                name: "RecordingChunks");

            migrationBuilder.DropTable(
                name: "InterviewRecordings");
        }
    }
}
