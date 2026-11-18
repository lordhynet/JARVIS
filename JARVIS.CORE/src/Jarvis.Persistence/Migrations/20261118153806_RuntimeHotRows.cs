using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RuntimeHotRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Fingerprint",
                table: "RuntimeDocuments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "RuntimeAudioState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    LeaseJson = table.Column<string>(type: "jsonb", nullable: false),
                    DrainLeaseJson = table.Column<string>(type: "jsonb", nullable: false),
                    DrainUntil = table.Column<string>(type: "text", nullable: true),
                    NextFence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeAudioState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeVoiceTickets",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeVoiceTickets", x => x.Hash);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeVoiceTurns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Text = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    SpeechSeconds = table.Column<double>(type: "double precision", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AtText = table.Column<string>(type: "text", nullable: false),
                    Restricted = table.Column<bool>(type: "boolean", nullable: false),
                    TimingJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeVoiceTurns", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "RuntimeAudioState",
                columns: new[] { "Id", "DrainLeaseJson", "DrainUntil", "LeaseJson", "NextFence" },
                values: new object[] { 1, "null", null, "null", 1L });

            migrationBuilder.UpdateData(
                table: "RuntimeDocuments",
                keyColumn: "Id",
                keyValue: 1,
                column: "Fingerprint",
                value: "");

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeVoiceTurns_SessionId_At",
                table: "RuntimeVoiceTurns",
                columns: new[] { "SessionId", "At" });

            migrationBuilder.Sql("""
                UPDATE "RuntimeAudioState" hot
                SET "LeaseJson" = COALESCE(runtime."Json"->'AudioLease', 'null'::jsonb),
                    "DrainLeaseJson" = COALESCE(runtime."Json"->'AudioDrainLease', 'null'::jsonb),
                    "DrainUntil" = runtime."Json"->>'AudioDrainUntil',
                    "NextFence" = COALESCE((runtime."Json"->>'NextAudioFence')::bigint, 1)
                FROM "RuntimeDocuments" runtime WHERE runtime."Id" = 1 AND hot."Id" = 1;

                INSERT INTO "RuntimeVoiceTickets" ("Hash", "Json")
                SELECT ticket.key, ticket.value FROM "RuntimeDocuments" runtime,
                LATERAL jsonb_each(COALESCE(NULLIF(runtime."Json"->'VoiceTickets', 'null'::jsonb), '{}'::jsonb)) ticket
                WHERE runtime."Id" = 1;

                DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM "RuntimeDocuments" runtime,
                LATERAL jsonb_each(COALESCE(NULLIF(runtime."Json"->'VoiceTurns', 'null'::jsonb), '{}'::jsonb)) turn
                WHERE runtime."Id" = 1 AND turn.key::uuid IS DISTINCT FROM (turn.value->>'Id')::uuid)
                THEN RAISE EXCEPTION 'Runtime voice ID differs from dictionary identity; reconcile before migration'; END IF;
                END $$;

                INSERT INTO "RuntimeVoiceTurns" ("Id", "SessionId", "Role", "Text", "SpeechSeconds", "At", "AtText", "Restricted", "TimingJson")
                SELECT turn.key::uuid, (turn.value->>'SessionId')::uuid, turn.value->>'Role', turn.value->>'Text',
                    (turn.value->>'SpeechSeconds')::double precision, (turn.value->>'At')::timestamptz, turn.value->>'At',
                    COALESCE((turn.value->>'Restricted')::boolean, false), NULLIF(turn.value->'Timing', 'null'::jsonb)
                FROM "RuntimeDocuments" runtime,
                LATERAL jsonb_each(COALESCE(NULLIF(runtime."Json"->'VoiceTurns', 'null'::jsonb), '{}'::jsonb)) turn
                WHERE runtime."Id" = 1;

                UPDATE "RuntimeDocuments" SET "Json" = "Json" - ARRAY['AudioLease', 'AudioDrainLease', 'AudioDrainUntil', 'NextAudioFence', 'VoiceTickets', 'VoiceTurns'] WHERE "Id" = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reconstruct the current state before dropping any hot table. Stop all writers first.
            migrationBuilder.Sql("""
                UPDATE "RuntimeDocuments" runtime
                SET "Json" = runtime."Json" || jsonb_build_object(
                    'AudioLease', audio."LeaseJson", 'AudioDrainLease', audio."DrainLeaseJson",
                    'AudioDrainUntil', audio."DrainUntil", 'NextAudioFence', audio."NextFence",
                    'VoiceTickets', COALESCE((SELECT jsonb_object_agg("Hash", "Json") FROM "RuntimeVoiceTickets"), '{}'::jsonb),
                    'VoiceTurns', COALESCE((SELECT jsonb_object_agg("Id"::text,
                        jsonb_build_object('Id', "Id", 'SessionId', "SessionId", 'Role', "Role", 'Text', "Text",
                            'SpeechSeconds', "SpeechSeconds", 'At', "AtText", 'Restricted', "Restricted") ||
                            CASE WHEN "TimingJson" IS NULL THEN '{}'::jsonb ELSE jsonb_build_object('Timing', "TimingJson") END)
                        FROM "RuntimeVoiceTurns"), '{}'::jsonb))
                FROM "RuntimeAudioState" audio WHERE runtime."Id" = 1 AND audio."Id" = 1;
                """);
            migrationBuilder.DropTable(
                name: "RuntimeAudioState");

            migrationBuilder.DropTable(
                name: "RuntimeVoiceTickets");

            migrationBuilder.DropTable(
                name: "RuntimeVoiceTurns");

            migrationBuilder.DropColumn(
                name: "Fingerprint",
                table: "RuntimeDocuments");
        }
    }
}
