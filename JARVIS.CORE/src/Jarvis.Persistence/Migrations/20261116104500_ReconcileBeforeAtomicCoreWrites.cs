using Jarvis.Contracts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Jarvis.Persistence.Migrations;

[DbContext(typeof(SessionDbContext))]
[Migration("20261116104500_ReconcileBeforeAtomicCoreWrites")]
public sealed class ReconcileBeforeAtomicCoreWrites : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Exclude concurrent Core mutations while adopting records left by the previous writer.
        migrationBuilder.Sql("""
            SELECT 1 FROM "PrivacyControls" WHERE "Id" = 1 FOR UPDATE;

            WITH pending AS (
                SELECT DISTINCT ON (message.value->>'RequestId')
                    message.value->>'RequestId' AS id,
                    jsonb_build_object(
                        'Id', message.value->'RequestId',
                        'SessionId', session."Id",
                        'Role', message.value->'Role',
                        'Text', message.value->'Text',
                        'SpeechSeconds', message.value->'SpeechSeconds',
                        'At', message.value->'At'
                    ) AS turn
                FROM "Sessions" session
                CROSS JOIN LATERAL jsonb_array_elements(session."MessagesJson"::jsonb) message(value)
                JOIN "RuntimeDocuments" runtime ON runtime."Id" = 1
                WHERE session."PendingVoiceProjectionIdsJson" <> '[]'::jsonb
                  AND session."PendingVoiceProjectionIdsJson" ? (message.value->>'RequestId')
                  AND message.value->>'IsVoice' = 'true'
                  AND NOT (COALESCE(runtime."Json"->'RetiredSessions', '[]'::jsonb) ? (session."Id"::text))
                ORDER BY message.value->>'RequestId', session."UpdatedAt", session."Id"
            ), recovered AS (
                SELECT jsonb_object_agg(id, turn) AS turns FROM pending
            )
            UPDATE "RuntimeDocuments" runtime
            SET "Json" = jsonb_set(runtime."Json", '{VoiceTurns}',
                recovered.turns || COALESCE(NULLIF(runtime."Json"->'VoiceTurns', 'null'::jsonb), '{}'::jsonb))
            FROM recovered
            WHERE runtime."Id" = 1 AND recovered.turns IS NOT NULL;

            UPDATE "Sessions"
            SET "PendingVoiceProjectionIdsJson" = '[]'::jsonb
            WHERE "PendingVoiceProjectionIdsJson" <> '[]'::jsonb;
            """);

        migrationBuilder.Sql($$"""
            UPDATE "RuntimeDocuments" runtime
            SET "Json" = runtime."Json" || jsonb_build_object(
                'AudioLease', null,
                'AudioDrainLease', runtime."Json"->'AudioLease',
                'AudioDrainUntil', runtime."Json"->'AudioLease'->'ExpiresAt'
            )
            WHERE runtime."Id" = 1
              AND jsonb_typeof(runtime."Json"->'AudioLease') = 'object'
              AND runtime."Json"->'AudioLease'->>'SessionId' <> '00000000-0000-0000-0000-000000000000'
              AND NOT EXISTS (
                  SELECT 1 FROM "Sessions" session
                  WHERE session."Id" = (runtime."Json"->'AudioLease'->>'SessionId')::uuid
                    AND session."Status" = {{(int)SessionStatus.Active}}
                    AND session."ActiveDeviceId" = runtime."Json"->'AudioLease'->>'DeviceId'
              );

            UPDATE "DeviceGrants" grant_row
            SET "RevokedAt" = CURRENT_TIMESTAMP
            FROM "RuntimeDocuments" runtime
            WHERE runtime."Id" = 1 AND grant_row."RevokedAt" IS NULL
              AND grant_row."GrantedBy" IS NOT NULL AND grant_row."Reason" IS NOT NULL
              AND (
                  runtime."Json"->'DeviceCredentials'->grant_row."DeviceId"->>'SecretHash'
                      IS DISTINCT FROM grant_row."CredentialHash"
                  OR runtime."Json"->'DeviceCredentials'->grant_row."DeviceId"->>'Revoked' = 'true'
              );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Recovered transcripts and revoked ownership remain valid data on rollback.
    }
}
