# ADR 0003: Central GBP budget authority

Accepted for implementation on 14 November 2026 following owner approval to continue building Slices 0 and 1. This does not enable providers, authorize a production budget increase, install dependencies or deploy.

Preserve the existing USD reservation IDs, provider accounting, public routes, privacy generations and transactional runtime store. Add GBP commitments and explicit approvals in the same runtime transaction. Existing USD limits remain additional restrictions during transition; they cannot authorize spending beyond GBP policy.

The budget environment and stable accounting scope come from canonical server configuration. UTC calendar months apply. Development has a £10 monthly ceiling. Production has a £100 ceiling unless a valid owner approval sets a total monthly ceiling above £100 and no higher than £200. No caller, provider adapter or domain manifest supplies an increased ceiling.

GBP conversions retain their immutable FX, tax/safety and policy basis. Missing or expired rates and unclassified legacy charges block paid dispatch. Operators can opt in to an ECB reference quote fallback. Tax and safety multipliers remain explicit settings. Outstanding charges remain committed. Erasure retains minimal financial accounting, invalidates approvals and prevents replay after reopening.

Owner budget management uses a separate configured credential and explicit scope. Legacy Core keys do not confer this privilege. This is a personal-server compatibility foundation, not OIDC or complete device identity. New remote device control remains outside these slices.

No relational migration or new project is required initially: GBP reservations and approvals share the existing RuntimeDocuments lock. Older writers cannot be mixed with the new authority after activation. Rollback requires stopping paid dispatch and preserving all incurred commitments first.

See the root Slice 0/1 change plan for tests, compatibility and rollback. Android remains audio-only; domain registration, Domain Factory, routing adapters, wake detection and native handoff remain later slices.
