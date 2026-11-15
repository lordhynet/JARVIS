using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Jarvis.Persistence;

#nullable disable

namespace Jarvis.Persistence.Migrations;

[DbContext(typeof(SessionDbContext))]
[Migration("20261115233000_PendingVoiceProjection")]
public sealed class PendingVoiceProjection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PendingVoiceProjectionIdsJson",
            table: "Sessions",
            type: "jsonb",
            nullable: false,
            defaultValue: "[]");

        // One-time adoption of any voice transcript whose projection may have been lost
        // before this marker existed. Regular repair sweeps never inspect MessagesJson.
        migrationBuilder.Sql("""
            UPDATE "Sessions" AS s
            SET "PendingVoiceProjectionIdsJson" = COALESCE((
                SELECT jsonb_agg(m.value->>'RequestId')
                FROM jsonb_array_elements(s."MessagesJson"::jsonb) AS m(value)
                WHERE m.value->>'IsVoice' = 'true'
            ), '[]'::jsonb)
            WHERE jsonb_path_exists(s."MessagesJson"::jsonb, '$[*] ? (@.IsVoice == true)');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "PendingVoiceProjectionIdsJson", table: "Sessions");
}
