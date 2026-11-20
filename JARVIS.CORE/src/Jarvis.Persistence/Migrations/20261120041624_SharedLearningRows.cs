using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SharedLearningRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimeLearningRows",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeLearningRows", x => new { x.Kind, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "RuntimeLearningState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    NextTrainingSequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeLearningState", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "RuntimeLearningState",
                columns: new[] { "Id", "NextTrainingSequence" },
                values: new object[] { 1, 1L });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeLearningRows_Kind_DueAt",
                table: "RuntimeLearningRows",
                columns: new[] { "Kind", "DueAt" });

            migrationBuilder.Sql("""
                -- Fail before removing any source property if canonical identities differ.
                DO $$ BEGIN
                IF EXISTS (
                    SELECT 1 FROM "RuntimeDocuments" runtime,
                    LATERAL (VALUES ('Learning', runtime."Json"->'Learning', 'Id'),
                        ('Reviews', runtime."Json"->'Reviews', 'RequestId'),
                        ('TrainingBaselines', runtime."Json"->'TrainingBaselines', 'Id')) collection(kind, payload, identity_key),
                    LATERAL jsonb_each(COALESCE(collection.payload, '{}'::jsonb)) entry
                    WHERE runtime."Id"=1 AND (jsonb_typeof(entry.value) IS DISTINCT FROM 'object'
                        OR entry.key::uuid IS DISTINCT FROM (entry.value->>collection.identity_key)::uuid))
                THEN RAISE EXCEPTION 'Learning record and dictionary identities differ; reconcile before migration'; END IF;
                IF EXISTS (
                    SELECT 1 FROM "RuntimeDocuments" runtime,
                    LATERAL jsonb_each(COALESCE(runtime."Json"->'TrainingBaselineReviews', '{}'::jsonb)) entry
                    WHERE runtime."Id"=1 AND jsonb_typeof(entry.value) IS DISTINCT FROM 'array')
                THEN RAISE EXCEPTION 'Baseline review membership must be an array'; END IF;
                IF EXISTS (SELECT 1 FROM "RuntimeDocuments" runtime,
                    LATERAL jsonb_each(COALESCE(runtime."Json"->'TrainingManualSequences','{}'::jsonb)) entry
                    WHERE runtime."Id"=1 AND jsonb_typeof(entry.value) IS DISTINCT FROM 'number')
                THEN RAISE EXCEPTION 'Manual review sequence must be an integer'; END IF;
                IF EXISTS (SELECT 1 FROM "RuntimeDocuments" WHERE "Id"=1 AND "Json" ? 'NextTrainingSequence'
                    AND jsonb_typeof("Json"->'NextTrainingSequence') IS DISTINCT FROM 'number')
                THEN RAISE EXCEPTION 'Training sequence must be an integer'; END IF;
                END $$;

                INSERT INTO "RuntimeLearningRows" ("Kind", "Id", "Json", "DueAt")
                SELECT collection.kind, entry.key::uuid, entry.value,
                    CASE WHEN collection.kind='Learning' THEN (entry.value->>'DueAt')::timestamptz ELSE NULL END
                FROM "RuntimeDocuments" runtime,
                LATERAL (VALUES ('Learning', runtime."Json"->'Learning'), ('Reviews', runtime."Json"->'Reviews'),
                    ('TrainingBaselines', runtime."Json"->'TrainingBaselines'),
                    ('TrainingBaselineReviews', runtime."Json"->'TrainingBaselineReviews'),
                    ('TrainingManualSequences', runtime."Json"->'TrainingManualSequences')) collection(kind, payload),
                LATERAL jsonb_each(COALESCE(collection.payload, '{}'::jsonb)) entry WHERE runtime."Id"=1;

                -- Validate the remaining scalar/UUID payloads using the same underlying types.
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "RuntimeLearningRows" row,
                        LATERAL jsonb_array_elements(CASE WHEN row."Kind"='TrainingBaselineReviews' THEN row."Json" ELSE '[]'::jsonb END) member(value)
                        WHERE jsonb_typeof(member.value) IS DISTINCT FROM 'string')
                    THEN RAISE EXCEPTION 'Baseline review membership must contain UUID strings'; END IF;
                    PERFORM member.value::uuid FROM "RuntimeLearningRows" row,
                        LATERAL jsonb_array_elements_text(CASE WHEN row."Kind"='TrainingBaselineReviews' THEN row."Json" ELSE '[]'::jsonb END) member(value);
                    PERFORM ("Json"#>>'{}')::bigint FROM "RuntimeLearningRows" WHERE "Kind"='TrainingManualSequences';
                END $$;

                UPDATE "RuntimeLearningState" learning
                SET "NextTrainingSequence"=COALESCE((runtime."Json"->>'NextTrainingSequence')::bigint, 1)
                FROM "RuntimeDocuments" runtime WHERE runtime."Id"=1 AND learning."Id"=1;

                UPDATE "RuntimeDocuments" SET "Json"="Json" - ARRAY['Learning','Reviews','TrainingBaselines',
                    'TrainingBaselineReviews','TrainingManualSequences','NextTrainingSequence'], "Fingerprint"=''
                WHERE "Id"=1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Stop writers and reconstruct their current state, not only the original backfill.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM "RuntimeDocuments" WHERE "Id"=1)
                    THEN RAISE EXCEPTION 'Runtime document is missing; refusing destructive learning downgrade'; END IF;
                    IF NOT EXISTS (SELECT 1 FROM "RuntimeLearningState" WHERE "Id"=1)
                    THEN RAISE EXCEPTION 'Learning sequence state is missing; refusing destructive downgrade'; END IF;
                    IF EXISTS (SELECT 1 FROM "RuntimeLearningRows" WHERE "Kind" NOT IN
                        ('Learning','Reviews','TrainingBaselines','TrainingBaselineReviews','TrainingManualSequences'))
                    THEN RAISE EXCEPTION 'Unknown learning collection; refusing destructive downgrade'; END IF;
                END $$;
                UPDATE "RuntimeDocuments" runtime SET "Json"=runtime."Json" || jsonb_build_object(
                    'Learning', COALESCE((SELECT jsonb_object_agg("Id"::text,"Json") FROM "RuntimeLearningRows" WHERE "Kind"='Learning'),'{}'::jsonb),
                    'Reviews', COALESCE((SELECT jsonb_object_agg("Id"::text,"Json") FROM "RuntimeLearningRows" WHERE "Kind"='Reviews'),'{}'::jsonb),
                    'TrainingBaselines', COALESCE((SELECT jsonb_object_agg("Id"::text,"Json") FROM "RuntimeLearningRows" WHERE "Kind"='TrainingBaselines'),'{}'::jsonb),
                    'TrainingBaselineReviews', COALESCE((SELECT jsonb_object_agg("Id"::text,"Json") FROM "RuntimeLearningRows" WHERE "Kind"='TrainingBaselineReviews'),'{}'::jsonb),
                    'TrainingManualSequences', COALESCE((SELECT jsonb_object_agg("Id"::text,"Json") FROM "RuntimeLearningRows" WHERE "Kind"='TrainingManualSequences'),'{}'::jsonb),
                    'NextTrainingSequence', learning."NextTrainingSequence"), "Fingerprint"=''
                FROM "RuntimeLearningState" learning WHERE runtime."Id"=1 AND learning."Id"=1;
                """);
            migrationBuilder.DropTable(
                name: "RuntimeLearningRows");

            migrationBuilder.DropTable(
                name: "RuntimeLearningState");
        }
    }
}
