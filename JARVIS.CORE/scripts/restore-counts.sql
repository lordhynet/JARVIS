SELECT json_build_object(
    'Migrations', (SELECT count(*) FROM "__EFMigrationsHistory"),
    'LearningCards', (SELECT count(*) FROM "RuntimeLearningRows" WHERE "Kind"='Learning'),
    'LearningReviews', (SELECT count(*) FROM "RuntimeLearningRows" WHERE "Kind"='Reviews'),
    'LearningRows', (SELECT count(*) FROM "RuntimeLearningRows"),
    'LearningControl', (SELECT count(*) FROM "RuntimeLearningState" WHERE "Id"=1),
    'NextTrainingSequence', (SELECT "NextTrainingSequence" FROM "RuntimeLearningState" WHERE "Id"=1),
    'AudioSingleton', (SELECT count(*) FROM "RuntimeAudioState" WHERE "Id"=1),
    'VoiceTurns', (SELECT count(*) FROM "RuntimeVoiceTurns"),
    'VoiceTickets', (SELECT count(*) FROM "RuntimeVoiceTickets"),
    'TravelRoutes', (SELECT count(*) FROM "TravelRoutes"),
    'TravelLineStates', (SELECT count(*) FROM "TravelLineStates"),
    'TravelMonitorSingleton', (SELECT count(*) FROM "TravelMonitorState" WHERE "Id"=1));
