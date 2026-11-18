using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TravelRoutesAndMonitor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TravelLineStates",
                columns: table => new
                {
                    LineId = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravelLineStates", x => x.LineId);
                });

            migrationBuilder.CreateTable(
                name: "TravelMonitorState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravelMonitorState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TravelRoutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NameKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravelRoutes", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "TravelMonitorState",
                columns: new[] { "Id", "Json" },
                values: new object[] { 1, "{}" });

            migrationBuilder.CreateIndex(
                name: "IX_TravelRoutes_NameKey",
                table: "TravelRoutes",
                column: "NameKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TravelLineStates");

            migrationBuilder.DropTable(
                name: "TravelMonitorState");

            migrationBuilder.DropTable(
                name: "TravelRoutes");
        }
    }
}
