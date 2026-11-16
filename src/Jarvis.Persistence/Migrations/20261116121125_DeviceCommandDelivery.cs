using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceCommandDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DispatchRequestId",
                table: "DeviceCommands",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DispatchedAt",
                table: "DeviceCommands",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCommands_DeviceId_CredentialHash_DispatchRequestId",
                table: "DeviceCommands",
                columns: new[] { "DeviceId", "CredentialHash", "DispatchRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCommands_DeviceId_CredentialHash_Status_CreatedAt_Id",
                table: "DeviceCommands",
                columns: new[] { "DeviceId", "CredentialHash", "Status", "CreatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DeviceCommands_DeviceId_CredentialHash_DispatchRequestId",
                table: "DeviceCommands");

            migrationBuilder.DropIndex(
                name: "IX_DeviceCommands_DeviceId_CredentialHash_Status_CreatedAt_Id",
                table: "DeviceCommands");

            migrationBuilder.DropColumn(
                name: "DispatchRequestId",
                table: "DeviceCommands");

            migrationBuilder.DropColumn(
                name: "DispatchedAt",
                table: "DeviceCommands");
        }
    }
}
