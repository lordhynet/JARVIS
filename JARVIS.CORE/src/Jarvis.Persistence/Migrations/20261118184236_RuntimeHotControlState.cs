using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RuntimeHotControlState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ControlsJson",
                table: "RuntimeAudioState",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "CredentialsJson",
                table: "RuntimeAudioState",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "DevicesJson",
                table: "RuntimeAudioState",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "WakeAttemptsJson",
                table: "RuntimeAudioState",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.UpdateData(
                table: "RuntimeAudioState",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "ControlsJson", "CredentialsJson", "DevicesJson", "WakeAttemptsJson" },
                values: new object[] { "{}", "{}", "{}", "{}" });
            migrationBuilder.Sql("""
                UPDATE "RuntimeAudioState" hot
                SET "ControlsJson" = COALESCE(NULLIF(runtime."Json"->'AgentControls', 'null'::jsonb), '{}'::jsonb),
                    "DevicesJson" = COALESCE(NULLIF(runtime."Json"->'Devices', 'null'::jsonb), '{}'::jsonb),
                    "CredentialsJson" = COALESCE(NULLIF(runtime."Json"->'DeviceCredentials', 'null'::jsonb), '{}'::jsonb),
                    "WakeAttemptsJson" = COALESCE(NULLIF(runtime."Json"->'WakeCandidateAttempts', 'null'::jsonb), '{}'::jsonb)
                FROM "RuntimeDocuments" runtime WHERE runtime."Id" = 1 AND hot."Id" = 1;
                UPDATE "RuntimeDocuments" SET "Json" = "Json" - ARRAY['AgentControls', 'Devices', 'DeviceCredentials', 'WakeCandidateAttempts'], "Fingerprint" = '' WHERE "Id" = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "RuntimeDocuments" runtime SET "Json" = runtime."Json" || jsonb_build_object(
                    'AgentControls', hot."ControlsJson", 'Devices', hot."DevicesJson",
                    'DeviceCredentials', hot."CredentialsJson", 'WakeCandidateAttempts', hot."WakeAttemptsJson"), "Fingerprint" = ''
                FROM "RuntimeAudioState" hot WHERE runtime."Id" = 1 AND hot."Id" = 1;
                """);
            migrationBuilder.DropColumn(
                name: "ControlsJson",
                table: "RuntimeAudioState");

            migrationBuilder.DropColumn(
                name: "CredentialsJson",
                table: "RuntimeAudioState");

            migrationBuilder.DropColumn(
                name: "DevicesJson",
                table: "RuntimeAudioState");

            migrationBuilder.DropColumn(
                name: "WakeAttemptsJson",
                table: "RuntimeAudioState");
        }
    }
}
