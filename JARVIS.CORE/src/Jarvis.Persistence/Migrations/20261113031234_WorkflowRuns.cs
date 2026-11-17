using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowEvents",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowEvents", x => x.EventId);
                    table.ForeignKey(
                        name: "FK_WorkflowEvents_WorkflowRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "WorkflowRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowEvents_RunId_Revision",
                table: "WorkflowEvents",
                columns: new[] { "RunId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_AvailableAt",
                table: "WorkflowRuns",
                column: "AvailableAt");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_CreatedAt",
                table: "WorkflowRuns",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowEvents");

            migrationBuilder.DropTable(
                name: "WorkflowRuns");
        }
    }
}
