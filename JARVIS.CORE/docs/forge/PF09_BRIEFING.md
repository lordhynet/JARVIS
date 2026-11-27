# PF-09: Forge in the spoken briefing

`ForgeBriefingProjection.Build` (pure) turns the programme calendar, PF-08 candidates and optionally a day plan into a small `forge` block on the existing `core_briefing` result (`VoiceBriefingService`).

- **Off means unchanged.** The block is present only when `Forge.Enabled` is true **and** a programme exists. Otherwise the field is null and omitted from JSON, so the legacy briefing is byte-identical.
- **Content:** status (`not-started` with days to go, `active`, `paused`, `graduated`), day N of 261, phase title, due-today review count, and up to three items with label, minutes and a short reason.
- **Honest scheduling.** Without a capacity-backed `ForgeDayPlan`, the items are **suggestions** (`scheduled: false`) in the allocator's selection order with requested minutes; capacity is never assumed. With a real plan they're the earliest placed items with the minutes actually placed (`scheduled: true`). Before the start, while paused or after graduation there are no items.
- **Privacy:** goals in protected divisions (WEALTH, HEALTH, GLOBAL, RELATIONSHIPS) are spoken as "WEALTH goal", never their titles. Other goal titles are control-stripped and cut to 40 characters, like calendar titles, and stay under the existing "treat as data" note. Locked devices still never reach this tool.
- **Size:** the existing 1,200-byte bound wins. Calendar entries are dropped first, then Forge items one at a time, then the whole Forge block, before the briefing would ever fail.
- **No paid AI:** cards come from the existing indexed learning read; goals from runtime state. No provider call, no write.

Not in this slice: the PF-08 caller that produces a capacity-backed plan (so today it always speaks suggestions), and full spoken/accountability evening and weekly reviews. The browser's optional recorded-practice summary is described in [J4_WEEKLY_SUMMARY.md](J4_WEEKLY_SUMMARY.md); it is not weighted skill/evidence coverage or a capacity-backed plan.

## Shared Today service and browser endpoint

`ForgeTodayService` uses one projection for both consumers, each at its supplied time. It uses the newest live programme, canonical cards due strictly before the next programme-local midnight, and runtime goals due for attention before that boundary. The inclusive indexed read is exact-filtered using payload ticks/offsets; a card or goal at next midnight belongs to tomorrow. Boundary offsets are evaluated at that local midnight, including DST. The records `ForgeBriefing`/`ForgeBriefingItem` live in `Jarvis.Contracts`; they hold no private capacity and protected goal titles are redacted.

`GET /api/forge/today` is owner-only and Forge-gated (404 when off) and returns 404 until a programme exists. It's read-only: no write, provider call or plan. Android stays audio-only (`AGENTS.md`); this is the browser surface.

The candidate adapter receives explicit `notBefore = now`; confirmed interview time stays unknown/null until a reviewed source port exists. Its order is already the allocator's admission/selection order, which the projection preserves without another ranking policy. Minutes without a plan are duration proposals, not observed capacity. Goal reasons remain coarse reviews rather than publishing private numeric deadline priorities as local-calendar facts.

The optional pure plan projection checks local date, programme/capacity revision, positive budget, unique known matching keys/kinds, whole-minute placements within the local day, no overlaps, requested duration bounds and matching totals before claiming scheduled. Invalid plans fall back to suggestions. This is defensive consistency checking, not protected-source authorization: the future capacity-backed caller must still resolve current programme, privacy epoch, capacity, quiet-hours policy and confirmed interview source under reviewed entry/recovery gates. Today's runtime service always supplies no plan, reads no protected record/key/protector and creates no new history/store/entity/counter/schema. Its independent read snapshots are epoch-fenced proposals; no authoritative capacity-backed plan or frozen schedule is claimed. Existing legacy/off voice behavior and locked-tool gates remain unchanged. Full source review is required before merge/deployment; Forge stays off on live Core.

## Browser Today panel

The existing Daily briefing tab loads the read-only panel independently of Travel. It uses the same Today endpoint, renders labels as text, and keeps no Forge data in browser storage. A 404 shows the calm off/no-programme state; other HTTP/network failures clear old suggestions and offer a reload without displaying private server error text. Unscheduled items are suggestions, with no assertion about whether capacity exists. The programme calendar ending does not claim achieved mastery or completion of its activities.

Overlapping reloads publish only their latest result. Disconnect and erasure clear the panel and invalidate pending loads; the tab handler also checks the connection generation before starting subsequent module work or showing late notices. Synthetic browser tests cover delayed Travel across disconnect/erasure, stale successes/failures, reload ordering, retry, safe text rendering and real HTTP status propagation on desktop and mobile layouts. These checks establish browser behavior, not app installation or physical-device acceptance.

## Evening review

Owner-only/Forge-gated `GET /api/forge/evening` returns the current programme-local day's recorded practice through captured now and next local day's suggestions, with an explicit `TomorrowDate`. It is a base count/proposal view, not full accountability, verified evidence, mastery or completed curriculum. The existing weekly classifier/unknown/minutes semantics apply to the day counts.

One captured programme reference and ONE existing runtime read callback supply both halves. Normal Today keeps its indexed candidate read; all callers reuse the same private pure projection for half-open canonical ticks, local-date/DST boundaries, candidate ordering, unknown interview context and protected title redaction. Tomorrow begins at the next local midnight, not now plus24hours. Runtime always supplies no capacity-backed plan. Programme selection and runtime remain separately epoch-fenced snapshots, not a globally frozen or authoritative plan. The on-demand evening read still hydrates cold/learning units; no performance acceptance.

After Today, the browser starts independent weekly/evening reads together in stable sections. It reuses one activity renderer for week/day counts and shows Tomorrow's date and paused/not-started/calendar-ended/empty/active status. Active items are suggestions. Reload/clear generation fences successes/errors; an old weekly continuation cannot start a new evening request after clear. Optional errors replace only their section with fixed retry text. No raw server error, browser storage, polling, native business UI or new action.

No plan/history/store/entity/schema/provider/capacity/key/protector read is introduced. Full spoken evening/weekly review, weighted scores, spend, actual/planned comparisons and missed-task adaptation remain separate requirements; `core_briefing` and its existing1,200-byte cap are unchanged. Independent entire-source review and existing operational/owner gates still apply before merge or rollout.
