# Story Bank readiness checklist

`GET /api/stories/{id}/readiness` and `GET /api/stories/readiness` (owner, least-ready first) return a deterministic checklist for each story; the Story Bank page shows the score, level, next follow-up question and an expandable checklist.

- **Nine checks** from the story's own fields: context/problem, personal ownership ("I" vs team-only), technical decisions, trade-offs (an alternative or cost), result, measurement, failure modes, reflection, and source evidence (attached CV or source quotes).
- **Score:** ready = 1, weak = 0.5, missing = 0; levels are draft (<50), usable (50–84) and interview-ready (≥85). Up to six targeted follow-up questions.
- **Honesty rule:** following the Story Bank's "blank metrics are better than invented ones", an explicit "not measured" or "unknown" counts as ready, and the advice never asks for invented numbers or facts. It checks shape, **not truth**.
- No AI, no storage, no writes. It's recomputed on read, so it always matches the current story version (the browser ignores a result for an older version).
