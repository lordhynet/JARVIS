using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CapabilityId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CapabilityVersion = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ConstraintsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CredentialHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceGrants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGrants_CredentialHash",
                table: "DeviceGrants",
                column: "CredentialHash");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGrants_DeviceId_CapabilityId_CapabilityVersion_Expire~",
                table: "DeviceGrants",
                columns: new[] { "DeviceId", "CapabilityId", "CapabilityVersion", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceGrants");
        }
    }
}
