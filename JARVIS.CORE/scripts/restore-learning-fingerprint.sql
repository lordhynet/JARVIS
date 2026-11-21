-- Structural comparison of canonical state before and after a synthetic restore.
-- No plaintext values are emitted; this is not a replacement for backup encryption.
WITH footprint AS (SELECT jsonb_build_object(
    'Migrations', (SELECT jsonb_agg("MigrationId" ORDER BY "MigrationId") FROM "__EFMigrationsHistory"),
    'Root', (SELECT jsonb_build_object('Json', "Json", 'Fingerprint', "Fingerprint") FROM "RuntimeDocuments" WHERE "Id"=1),
    'Learning', (SELECT jsonb_agg(jsonb_build_object('Kind', "Kind", 'Id', "Id", 'Json', "Json", 'DueAt', "DueAt") ORDER BY "Kind", "Id") FROM "RuntimeLearningRows"),
    'Control', (SELECT jsonb_build_object('Id', "Id", 'NextTrainingSequence', "NextTrainingSequence") FROM "RuntimeLearningState" WHERE "Id"=1)
) AS data)
SELECT json_build_object(
    'Canonical', encode(sha256(convert_to(data::text, 'UTF8')), 'hex'),
    'Learning', encode(sha256(convert_to((data - 'Root')::text, 'UTF8')), 'hex')) FROM footprint;
