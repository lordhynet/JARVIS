# Paid interview-brief workflow

The Android Progress screen can request a Claude-generated interview-preparation draft
and three drills from up to two saved stories and three due non-French learning prompts.
Learning-card reference answers, provider keys and arbitrary tools are not sent. Selected
story facts are sent only after the per-run cloud-processing opt-in. Source IDs
must exist in the supplied context; output remains an AI-generated draft, not verified fact.
Nothing automatically rewrites a canonical story or records a mastery score.

## Enable after adding your API key

Keep the existing private root `appsettings.json` and its database password. All backend settings live in that file. `appsettings.example.json` is a sanitized template, not an additional runtime configuration source.

Set `OpenAI.ApiKey` to enable consented chat, transcription, AI feedback, live voice, and specialist agents. Set `Anthropic.ApiKey` for the Claude interview-brief workflow. Keys stay on Core and are never included in the APK. The `AI` model profiles, dated rates, per-lane limits, and `PaidAI` daily/monthly/background limits are preconfigured; review the rates against your provider account before paid use.

`OpenAI.Model` must match `AI.Chat.Model`. `OpenAI.TranscriptionModel` controls transcription. `AI.Coach`, `AI.Voice`, `AI.Agent`, and `AI.Research` define their own model and input/output token rates. A rate card older than 90 days blocks dispatch. The Claude adapter also requires `PaidAI.Enabled`, matching `Anthropic.Model` and `PaidAI.RateCardModel`, and positive dated rates and budgets.

Apply deployment changes with:

```powershell
./scripts/start-server.ps1 -NoBuild
```

Each paid UI action requires consent. Research, career, and personal planning require cloud consent before a run or schedule is created. Local practice reminders and deterministic practice plans remain available without cloud processing.

## Budget, durability and cancellation

Before HTTP dispatch, Core atomically reserves a conservative charge in PostgreSQL's
locked runtime document. Reservation identity is the worker claim token. The bound uses
UTF-8 context size plus framing allowance and a 2,048-token output cap. The owner supplies
the dated rates. Competing devices/hosts cannot reserve beyond the configured daily limit
or the smaller monthly/background limit for this ledger.

Known token usage settles the reservation. Missing/ambiguous responses retain the maximum
charge, and recovery does not automatically replay a run with an uncertain prior charge.
Unexpected usage above the estimate is recorded and blocks further paid dispatch until
operator reconciliation. A reconciliation UI is not implemented: inspect provider billing
before resolving uncertain entries; never simply erase reservations to bypass a limit.

The $50 monthly ceiling applies to calls recorded by this new workflow ledger. Legacy
OpenAI chat/transcription, other applications, hosting, taxes and exchange rates are not
metered here. A complete global all-provider spend gateway remains a release requirement.
Provider-account spending controls should also be configured.

Paid runs renew their lease while active. Cancellation revokes the claim and signals the
HTTP operation; an already dispatched request may still be billable. A stale worker cannot
publish. The worker marks uncertain failures `needs_reconciliation` rather than pretending
they were free or automatically retrying them.

## API and privacy

- `GET /api/workflows/paid-ai`: configuration readiness, budgets, committed totals, uncertain count.
- `POST /api/workflows/interview-briefs`: `{requestId,allowCloudProcessing:true}`; 202 with durable run.
- Existing workflow get/events/cancel APIs apply. `brief` contains summary, three drills,
  source IDs, model and generation time.
- `DELETE /api/workflows/{id}`: idempotently remove draft/result content and revoke a live claim.
  Minimal request identity and spending metadata remain, preventing same-ID paid replay.
- Deleted workflow content/events are unavailable through normal get/list/events APIs.
- Briefs referencing a missing source are hidden on API reads. Automatic physical purging
  of derived source content within 24 hours remains pending; delete its workflow result
  to purge that saved draft. Complete account export/deletion/retention jobs remain pending.

These routes retain the existing single-user Core-key boundary. OIDC/user isolation and
the full governed specialist runtime are not complete. This workflow has no calendar trigger,
external-action capability, autonomous notifications or independent specialist memories.

## Evidence

Provider-contract fixtures cover request shape, no tools, bounded context, source validation,
actual/uncertain usage and replay suppression. PostgreSQL tests exercise competing budgets
across hosts. The final backend suite passed 82 tests with PostgreSQL; browser regressions
passed 14 tests. No live Claude call was made because no real key was provided.

Protocol reference: [Claude Messages API](https://platform.claude.com/docs/en/api/messages/create).

## GBP compatibility and rollback

Paid dispatch now also requires the configured central GBP authority. All existing OpenAI/Anthropic text, search, transcription and realtime reservation paths share the same transactionally enforced GBP gate. The ordinary Core key cannot increase spending or reconcile charges away. A configured provider is not authorization to raise the development/production budget.

GBP commitments retain their original quote and explicit billing safety basis; policy/FX updates do not reprice old commitments. Provider usage above a reservation still requires reconciliation. An actual provider bill can exceed an estimate, so budget software cannot retroactively prevent a charge; use conservative accounting and provider-side safeguards where available.

Erasure retains minimal USD/GBP financial commitments and the accounting scope, revokes spending approvals, increments the authority epoch and redacts actor/evidence fields. Reopening does not reset the month's spend or revive old approval requests. Late numeric settlement uses the retained conversion basis without restoring personal data.

Do not mix older writers with an activated GBP authority: they do not enforce the new policy and can discard unknown JSON metadata. Before any rollback, stop every paid path and worker/voice window, preserve post-upgrade accounting and verify a safe target. `PaidAI:Enabled=false` alone does not disable all OpenAI paths. Never restore an old database snapshot that forgets incurred charges. No relational migration is required for the additive runtime JSON fields in this slice.

Owner verification may correct GBP separately from USD through optional `actualGbp` on the existing reconciliation request. This supports bank/invoice FX differences without falsifying a clean USD settlement. Evidence and the separate owner credential remain mandatory, and the original conversion estimate is retained beside the verified amount. Verification records a bill; it does not authorize a higher monthly ceiling. No such real charge or correction was made during implementation tests.
