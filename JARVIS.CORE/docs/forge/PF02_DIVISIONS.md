# PF-02: Forge divisions and goal tags

The sixteen Addendum V2 §3 divisions are code-owned **Domain Registry templates** (`Jarvis.Orchestrator.Forge.ForgeDivisions`), keyed `forge.<code>` (for example `forge.tech`). There is no second domain system, table or migration.

## Installing (owner-only, Forge enabled)

`POST /api/forge/divisions/drafts` with `{ "includeOptional": false }` saves each division through the existing registry draft path (`IDomainRegistry.SaveDraftAsync`) with a deterministic request ID, so repeats and concurrent installs replay instead of duplicating. It never activates anything. Existing definitions, including any the owner edited, disabled or activated, are reported as `existing` and left untouched. Activation stays the owner's existing per-domain approval step. With Forge disabled the route is 404.

- **Template mode** with the installed `core_template_practice` capability only: no paid AI, no external action, `UsesPersonalContext = false` for every division.
- **Protected privacy classes:** WEALTH, HEALTH, GLOBAL, RELATIONSHIPS. Their practice prompts never ask for private values (amounts, symptoms, partners). Private values belong in the PF-03 encrypted check-ins, not in domain text.
- **RELATIONSHIPS is optional** and skipped unless `includeOptional` is true.
- **Existing risk policy applies unchanged.** WEALTH is classified Finance (read-only capability, no shared context). HEALTH is classified Health and **stays a draft** until trusted medical sources are installed; PF-02 deliberately doesn't weaken that.

## Goal division tag

`PersonalGoal` and `SaveGoalRequest` gain an optional `divisionKey`, omitted from JSON when null (legacy shape unchanged). It must name a code-owned division (`400` otherwise). It's inert planning metadata: no workflow, capability, consent or Forge activation is implied, and Forge needn't be enabled. Idempotent goal retries compare the tag (a different tag under the same ID is `409`). An edit without the tag clears it, like any other field. Deleting a goal clears the tag in its content-free tombstone.

## Not in this slice

Per-division capacity weights, the PF-08 caller that turns goals and due work into planner candidates, metric/division membership for check-ins (Codex's H2), and any registry change to resolve tags against active domains.
