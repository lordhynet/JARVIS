-- Isolated verification project only. No provider HTTP or private configuration is required.
INSERT INTO "RuntimeVoiceTurns" ("Id","SessionId","Role","Text","At","AtText","Restricted")
SELECT '00000000-0000-4000-8000-000000000303',"Id",'user','Isolated restore voice fixture',
    '2026-11-18T08:10:00Z'::timestamptz,'2026-11-18T08:10:00.0000000+00:00',false FROM "Sessions" LIMIT 1;
INSERT INTO "TravelRoutes" ("Id","NameKey","Json") VALUES ('00000000-0000-4000-8000-000000000304','RESTORE FIXTURE',
'{"Id":"00000000-0000-4000-8000-000000000304","Name":"Restore fixture","Lines":[{"Id":"jubilee","Name":"Jubilee","Mode":"tube"}],"Stops":[],"Windows":[{"Days":[1],"Start":"08:00","End":"09:00"}],"TimeZone":"Europe/London","Enabled":false,"Revision":1,"CreatedAt":"2026-11-18T08:10:00Z","UpdatedAt":"2026-11-18T08:10:00Z"}'::jsonb);
INSERT INTO "TravelLineStates" ("LineId","Json") VALUES ('jubilee','{"LineId":"jubilee","CandidateCount":1}'::jsonb);
