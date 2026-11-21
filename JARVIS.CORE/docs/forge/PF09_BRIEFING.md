# PF-09: Forge in the spoken briefing

`ForgeBriefingProjection.Build` (pure) turns the programme calendar, PF-08 candidates and optionally a day plan into a small `forge` block on the existing `core_briefing` result (`VoiceBriefingService`).

- **Off means unchanged.** The block is present only when `Forge.Enabled` is true **and** a programme exists. Otherwise the field is null and omitted from JSON, so the legacy briefing is byte-identical.
- **Content:** status (`not-started` with days to go, `active`, `paused`, `graduated`), day N of 261, phase title, due-today review count, and up to three items with label, minutes and a short reason.
- **Honest scheduling.** Without a capacity-backed `ForgeDayPlan`, the items are **suggestions** (`scheduled: false`) in the allocator's selection order with requested minutes; capacity is never assumed. With a real plan they're the earliest placed items with the minutes actually placed (`scheduled: true`). Before the start, while paused or after graduation there are no items.
- **Privacy:** goals in protected divisions (WEALTH, HEALTH, GLOBAL, RELATIONSHIPS) are spoken as "WEALTH goal", never their titles. Other goal titles are control-stripped and cut to 40 characters, like calendar titles, and stay under the existing "treat as data" note. Locked devices still never reach this tool.
- **Size:** the existing 1,200-byte bound wins. Calendar entries are dropped first, then Forge items one at a time, then the whole Forge block, before the briefing would ever fail.
- **No paid AI:** cards come from the existing indexed learning read; goals from runtime state. No provider call, no write.

Not in this slice: the PF-08 caller that produces a capacity-backed plan (so today it always speaks suggestions), the evening and weekly review projections, and the browser Today view.
