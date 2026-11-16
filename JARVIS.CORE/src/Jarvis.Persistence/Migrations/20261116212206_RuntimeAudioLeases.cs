using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RuntimeAudioLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimeAudioStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    LeaseSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseDeviceId = table.Column<string>(type: "text", nullable: true),
                    LeaseFence = table.Column<long>(type: "bigint", nullable: true),
                    LeaseExpiresAt = table.Column<string>(type: "character varying(48)", nullable: true),
                    DrainSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DrainDeviceId = table.Column<string>(type: "text", nullable: true),
                    DrainFence = table.Column<long>(type: "bigint", nullable: true),
                    DrainExpiresAt = table.Column<string>(type: "character varying(48)", nullable: true),
                    DrainUntil = table.Column<string>(type: "character varying(48)", nullable: true),
                    NextFence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeAudioStates", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "RuntimeAudioStates",
                columns: new[] { "Id", "DrainDeviceId", "DrainExpiresAt", "DrainFence", "DrainSessionId", "DrainUntil", "LeaseDeviceId", "LeaseExpiresAt", "LeaseFence", "LeaseSessionId", "NextFence" },
                values: new object[] { 1, null, null, null, null, null, null, null, null, null, 1L });

            migrationBuilder.Sql("""
                UPDATE "RuntimeAudioStates" AS audio
                SET "LeaseSessionId" = (document."Json"->'AudioLease'->>'SessionId')::uuid,
                    "LeaseDeviceId" = document."Json"->'AudioLease'->>'DeviceId',
                    "LeaseFence" = (document."Json"->'AudioLease'->>'Fence')::bigint,
                    "LeaseExpiresAt" = document."Json"->'AudioLease'->>'ExpiresAt',
                    "DrainSessionId" = (document."Json"->'AudioDrainLease'->>'SessionId')::uuid,
                    "DrainDeviceId" = document."Json"->'AudioDrainLease'->>'DeviceId',
                    "DrainFence" = (document."Json"->'AudioDrainLease'->>'Fence')::bigint,
                    "DrainExpiresAt" = document."Json"->'AudioDrainLease'->>'ExpiresAt',
                    "DrainUntil" = document."Json"->>'AudioDrainUntil',
                    "NextFence" = COALESCE((document."Json"->>'NextAudioFence')::bigint, 1)
                FROM "RuntimeDocuments" AS document
                WHERE audio."Id" = 1 AND document."Id" = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "RuntimeDocuments" AS document
                SET "Json" = document."Json" || jsonb_build_object(
                    'AudioLease', CASE WHEN audio."LeaseSessionId" IS NOT NULL THEN jsonb_build_object(
                        'SessionId', audio."LeaseSessionId", 'DeviceId', audio."LeaseDeviceId",
                        'Fence', audio."LeaseFence", 'ExpiresAt', replace(audio."LeaseExpiresAt", ' ', 'T')) END,
                    'AudioDrainLease', CASE WHEN audio."DrainSessionId" IS NOT NULL THEN jsonb_build_object(
                        'SessionId', audio."DrainSessionId", 'DeviceId', audio."DrainDeviceId",
                        'Fence', audio."DrainFence", 'ExpiresAt', replace(audio."DrainExpiresAt", ' ', 'T')) END,
                    'AudioDrainUntil', replace(audio."DrainUntil", ' ', 'T'),
                    'NextAudioFence', audio."NextFence")
                FROM "RuntimeAudioStates" AS audio
                WHERE audio."Id" = 1 AND document."Id" = 1;
                """);

            migrationBuilder.DropTable(
                name: "RuntimeAudioStates");
        }
    }
}
