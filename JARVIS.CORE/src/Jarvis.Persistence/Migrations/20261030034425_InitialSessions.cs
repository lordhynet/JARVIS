using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Activity = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ActiveDeviceId = table.Column<string>(type: "text", nullable: true),
                    CurrentTask = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Checkpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CurrentTask = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Checkpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Checkpoints_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Checkpoints_SessionId_Sequence",
                table: "Checkpoints",
                columns: new[] { "SessionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_UpdatedAt",
                table: "Sessions",
                column: "UpdatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Checkpoints");

            migrationBuilder.DropTable(
                name: "Sessions");
        }
    }
}
