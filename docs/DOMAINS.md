# Domain Registry

Installed workflows and activity IDs remain compatible. Configuration domains use `domain-<key>-practice` (dots in keys become hyphens); that namespace is reserved. An approved configuration domain appears in `/api/agents/catalog` without a deployment. Runs retain an immutable version/template snapshot. Schedules, goals and triggers resolve the active version at dispatch. Disabled domains, disabled parents, missing installed dependencies and obsolete approval policies fail closed before execution.

`GET /api/domains` lists drafts and dependency previews. `POST /api/domains/drafts` accepts `{ requestId, expectedRevision, manifest }`. First drafts omit `expectedRevision`; edits supply the latest revision. Schema 1 accepts only the fields in `DomainManifest`: key, displayName, description, aliases, roles, capabilities, practicePrompts, executionMode, usesPersonalContext, configuration and parentKey (plus schemaVersion). Unknown fields and executable/permission configuration keys are rejected. Capability IDs/version constraints describe dependencies; they do not install tools or grant permissions.

`POST /api/domains/{key}/activate` requires the separate owner header and `{ requestId, expectedRevision, versionId, manifestHash, reason }`. A fresh explicit approval binds the exact version and hash under the current policy. `/rollback` uses the same confirmation for an older version; `/disable` requires request ID and current revision. Reusing an operation ID with different input returns 409. Draft creation never activates a domain. Aliases are reserved across current and latest versions, including disabled domains; equivalent Unicode names cannot create duplicates.

Template execution delivers the authored practice prompts, with no AI score or tool dispatch. Assisted execution requires cloud consent and existing reviewed provider admission. Finance, Law and Health are read-only and cannot use shared personal coaching context. Law/Health require trusted source capabilities that are currently unavailable, so these definitions remain drafts. Requested features that have no installed capability remain dependency gaps.

`GET/PUT /api/domains/{key}/state` stores owner preferences and a canonical progress reference. This remains a single-owner system. Background access requires the matching trusted domain execution scope. Shared stories/transcripts are not copied into per-domain storage, and personal coaching context is excluded unless explicitly approved in the manifest.

PostgreSQL uses six relational tables with an additive migration. Administrative catalogue mutations use a separate advisory transaction lock, optimistic revisions and unique key/version constraints; they do not write the runtime JSONB row. Runtime reads query active pointers and current approvals. Export includes the registry; erasure clears definitions, versions, bindings, approvals, preferences and audit content. Privacy epochs fence stale requests and invalidate installed seeding. In-memory mode shares the existing privacy boundary.

The application targets .NET 11 and currently uses EF Core 10.0.13. Generate migrations with the matching local EF 10 tool, not the installed global EF 9 tool. Do not apply a migration to the running owner database as a test.

## Natural-language proposals

The dashboard accepts a description plus explicit cloud consent at `/api/domains/proposals`. Generation uses the existing provider-neutral Agent route and durable budget admission, with a 1200-token output bound. There are no live-provider smoke tests. Configure/review provider rates through the existing owner process before use.

Core validates the returned strict manifest, rejects generated personal-context opt-in and permission/code fields, resolves existing aliases, and saves an inactive draft plus generation ID, request fingerprint and provider/model provenance in the relational audit. A repeated successful proposal reuses its persisted version without another model request. The preview displays exact manifest/hash, data scope, cost mode and unmet dependencies. Activation is a separate manual owner action.

`core_domain_propose` reads an explicit domain-creation request from the current captured user utterance. It cannot accept generated replacement speech, activate domains, or grant permissions. Its response directs the owner to the dashboard preview. Android remains an audio transport. Uninstalled features stay gaps; authored templates do not provide quizzes, retrieval, professional advice or calibrated scores.
