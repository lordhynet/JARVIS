using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CanonicalTrainingRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimeTrainingRows",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeTrainingRows", x => new { x.Kind, x.Id });
                });
            migrationBuilder.Sql(ValidateCold);
            migrationBuilder.Sql(ValidateEntries(ColdEntries));
            migrationBuilder.Sql($"""
                INSERT INTO "RuntimeTrainingRows" ("Kind","Id","Json") SELECT kind,id,payload FROM ({ColdEntries}) entries;
                UPDATE "RuntimeDocuments" SET "Json"="Json"-'TrainingAttempts'-'TrainingOperations', "Fingerprint"='' WHERE "Id"=1;
                CREATE INDEX "IX_RuntimeTrainingRows_Attempt_QuestionId" ON "RuntimeTrainingRows" ((("Json"->>'QuestionId')::uuid)) WHERE "Kind"='TrainingAttempts';
                CREATE INDEX "IX_RuntimeTrainingRows_Operation_AttemptId" ON "RuntimeTrainingRows" ((("Json"->>'AttemptId')::uuid)) WHERE "Kind"='TrainingOperations';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ValidateEntries("SELECT \"Kind\" AS kind, \"Id\" AS id, \"Json\" AS payload FROM \"RuntimeTrainingRows\""));
            migrationBuilder.Sql(ValidateCold);
            migrationBuilder.Sql("""
                DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM "RuntimeTrainingRows") AND NOT EXISTS (SELECT 1 FROM "RuntimeDocuments" WHERE "Id"=1)
                  THEN RAISE EXCEPTION 'Training rows have no restore root'; END IF;
                IF EXISTS (
                  SELECT 1 FROM "RuntimeDocuments" root,
                  LATERAL (VALUES ('TrainingAttempts'),('TrainingOperations')) collection(kind)
                  WHERE root."Id"=1 AND COALESCE(root."Json"->collection.kind,'{}'::jsonb)<>'{}'::jsonb
                  AND root."Json"->collection.kind IS DISTINCT FROM COALESCE((SELECT jsonb_object_agg("Id"::text,"Json") FROM "RuntimeTrainingRows" WHERE "Kind"=collection.kind),'{}'::jsonb))
                  THEN RAISE EXCEPTION 'Conflicting cold training copy; reconcile before downgrade'; END IF;
                END $$;
                UPDATE "RuntimeDocuments" SET "Json"="Json"||jsonb_build_object(
                  'TrainingAttempts',COALESCE((SELECT jsonb_object_agg("Id"::text,"Json") FROM "RuntimeTrainingRows" WHERE "Kind"='TrainingAttempts'),'{}'::jsonb),
                  'TrainingOperations',COALESCE((SELECT jsonb_object_agg("Id"::text,"Json") FROM "RuntimeTrainingRows" WHERE "Kind"='TrainingOperations'),'{}'::jsonb)),
                  "Fingerprint"='' WHERE "Id"=1;
                """);
            migrationBuilder.DropTable(
                name: "RuntimeTrainingRows");
        }

        private const string ColdEntries = """
            SELECT collection.kind,entry.key::uuid AS id,entry.value AS payload
            FROM "RuntimeDocuments" root,
            LATERAL (VALUES ('TrainingAttempts',root."Json"->'TrainingAttempts'),('TrainingOperations',root."Json"->'TrainingOperations')) collection(kind,payload),
            LATERAL jsonb_each(COALESCE(collection.payload,'{}'::jsonb)) entry WHERE root."Id"=1
            """;
        private const string ValidateCold = """
            DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM "RuntimeDocuments" WHERE "Id"=1 AND jsonb_typeof("Json") IS DISTINCT FROM 'object')
              THEN RAISE EXCEPTION 'Runtime root must be an object'; END IF;
            IF EXISTS (SELECT 1 FROM "RuntimeDocuments" root,
              LATERAL (VALUES ('TrainingAttempts'),('TrainingOperations')) collection(kind)
              WHERE root."Id"=1 AND root."Json" ? collection.kind AND jsonb_typeof(root."Json"->collection.kind) IS DISTINCT FROM 'object')
              THEN RAISE EXCEPTION 'Training source collections must be objects'; END IF;
            END $$;
            """;
        private static string ValidateEntries(string source) => $$"""
            DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM ({{source}}) entry WHERE
              kind NOT IN ('TrainingAttempts','TrainingOperations') OR id='00000000-0000-0000-0000-000000000000'::uuid
              OR jsonb_typeof(payload) IS DISTINCT FROM 'object'
              OR (kind='TrainingAttempts' AND (
                id IS DISTINCT FROM (payload->>'Id')::uuid OR (payload->>'SessionId')::uuid IS NULL OR (payload->>'QuestionId')::uuid IS NULL
                OR (payload->>'SessionId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
                OR (payload->>'QuestionId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
                OR jsonb_typeof(payload->'Mode') IS DISTINCT FROM 'number' OR (payload->>'Mode')::int NOT BETWEEN 0 AND 8
                OR jsonb_typeof(payload->'Answers') IS DISTINCT FROM 'array' OR jsonb_typeof(payload->'Assessments') IS DISTINCT FROM 'array'
                OR jsonb_typeof(payload->'SubmittedAt') IS DISTINCT FROM 'string' OR (payload->>'SubmittedAt')::timestamptz IS NULL
                OR EXISTS (SELECT 1 FROM jsonb_each(payload) field WHERE
                  (field.key IN ('QuestionVersion','Revision') AND (jsonb_typeof(field.value)<>'number' OR (field.value#>>'{}')::int IS NULL))
                  OR (field.key='Sequence' AND (jsonb_typeof(field.value)<>'number' OR (field.value#>>'{}')::bigint IS NULL))
                  OR (field.key='Skipped' AND jsonb_typeof(field.value)<>'boolean'))
                OR EXISTS (SELECT 1 FROM jsonb_array_elements(CASE WHEN jsonb_typeof(payload->'Answers')='array' THEN payload->'Answers' ELSE '[]'::jsonb END) answer
                  WHERE jsonb_typeof(answer)<>'object' OR jsonb_typeof(answer->'At') IS DISTINCT FROM 'string' OR (answer->>'At')::timestamptz IS NULL)
                OR EXISTS (SELECT 1 FROM jsonb_array_elements(CASE WHEN jsonb_typeof(payload->'Assessments')='array' THEN payload->'Assessments' ELSE '[]'::jsonb END) assessment
                  WHERE jsonb_typeof(assessment)<>'object' OR jsonb_typeof(assessment->'Scores') IS DISTINCT FROM 'array'
                  OR jsonb_typeof(assessment->'At') IS DISTINCT FROM 'string' OR (assessment->>'At')::timestamptz IS NULL
                  OR EXISTS (SELECT 1 FROM jsonb_array_elements(CASE WHEN jsonb_typeof(assessment->'Scores')='array' THEN assessment->'Scores' ELSE '[]'::jsonb END) score WHERE jsonb_typeof(score)<>'object'))))
              OR (kind='TrainingOperations' AND (
                (payload->>'AttemptId')::uuid IS NULL OR (payload->>'AttemptId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
                OR jsonb_typeof(payload->'Kind') IS DISTINCT FROM 'string' OR btrim(payload->>'Kind')=''
                OR jsonb_typeof(payload->'Fingerprint') NOT IN ('string','null'))))
              THEN RAISE EXCEPTION 'Invalid canonical training identity or payload'; END IF;
            IF EXISTS (SELECT 1 FROM ({{source}}) entry GROUP BY kind,id HAVING count(*)>1)
              THEN RAISE EXCEPTION 'Duplicate canonical training UUID'; END IF;
            EXCEPTION WHEN invalid_text_representation OR invalid_datetime_format OR datetime_field_overflow OR numeric_value_out_of_range
              THEN RAISE EXCEPTION 'Invalid canonical training UUID or value';
            END $$;
            """;
    }
}
