using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VoiceHomeGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PreApproved",
                table: "DeviceGrants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "HomeEntityId",
                table: "DeviceCommands",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCommands_DispatchedAt",
                table: "DeviceCommands",
                column: "DispatchedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DeviceCommands_DispatchedAt",
                table: "DeviceCommands");

            migrationBuilder.DropColumn(
                name: "PreApproved",
                table: "DeviceGrants");

            migrationBuilder.DropColumn(
                name: "HomeEntityId",
                table: "DeviceCommands");
        }
    }
}
