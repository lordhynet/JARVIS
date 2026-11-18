# Saved London routes and public travel voice

Status: implemented locally; reviewer and owner acceptance pending.

Travel uses dedicated TravelRoutes, TravelLineStates and singleton TravelMonitorState tables. A travel update joins the existing D2 runtime transaction so a confirmed transition and its notification commit together. Candidate states, poll claims, backoff and notification limits survive restart. Route changes expire outstanding notifications in that same transaction. No travel collections are added to the runtime JSON document.

The owner saves at most ten routes, twelve public lines per route and eight London local-time windows. The browser Daily briefing card owns editing; Android remains audio-only. The daily catalogue and saved stop metadata validate every selected public ID. No route name or owner-defined URL is sent upstream. TfL query strings are fully redacted, including the optional app key. Testing refuses real TfL HTTP unless a fixture explicitly installs a fake handler.

One batched status request polls eligible lines every two minutes, only while a route is active or starts within sixty minutes. A new state needs two consecutive matching observations within five minutes. Failed or missing observations reset its candidate. Numeric TfL severity codes are identifiers; application impact uses explicit public description labels. Error retries use jittered 2/4/8-minute delays capped at fifteen minutes. Delivery is restricted to active opted-in windows, with a fifteen-minute per-line limit and six daily disruptions plus recovery notices. Quiet-hour exceptions are confined to those windows; polling ahead never authorizes early delivery.

The code-owned core_travel registry tool is public and read-only. It supports status and the next five arrivals at a saved stop, hides saved route names while locked, and rejects edits and bookings. Stored confirmed public alerts contribute at most three entries and 300 UTF-8 bytes to the next voice window. Existing utterances and prepared provider sockets receive no mid-window update. Public source text is labelled as data, never instructions.

Privacy export includes routes and line state. Full erasure clears both and resets monitor metadata; route deletion prunes unused line state. Routes are owner configuration and survive ordinary retention. Expired travel outbox entries are removed by retention.

Migration: 20261118194620_TravelRoutesAndMonitor follows 20261118184236_RuntimeHotControlState. Stop writers and back up before migration. Down drops the three new tables and therefore loses travel configuration and observation history; export/backup first. Existing runtime data is unchanged by this migration. Restore a matching backup and binary when rolling back a release. Local disposable PostgreSQL rehearsal is separate from production deployment.

No live TfL calls, paid providers, phone installation or hardware acceptance form part of this implementation run.
