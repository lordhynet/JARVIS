SELECT json_build_object(
    'AudioSingleton', (SELECT count(*) FROM "RuntimeAudioState" WHERE "Id"=1),
    'VoiceTurns', (SELECT count(*) FROM "RuntimeVoiceTurns"),
    'VoiceTickets', (SELECT count(*) FROM "RuntimeVoiceTickets"),
    'TravelRoutes', (SELECT count(*) FROM "TravelRoutes"),
    'TravelLineStates', (SELECT count(*) FROM "TravelLineStates"),
    'TravelMonitorSingleton', (SELECT count(*) FROM "TravelMonitorState" WHERE "Id"=1));
