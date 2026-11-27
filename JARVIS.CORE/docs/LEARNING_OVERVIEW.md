# Learning overview

`GET /api/learning/overview` (owner only) returns a deterministic summary of the spaced-repetition cards.
No AI and nothing stored: `LearningOverviewBuilder` projects the current cards and review events on read.

- **Forecast:** seven 24-hour windows from now. Window 0 includes everything overdue.
- **Last 7 days:** review counts by rating, plus the recall rate (the share not rated Again). It is null when there are no reviews.
- **Focus:** the domain with the most Again ratings in the last 7 days. It is null when nothing was forgotten, so JARVIS never invents a weakness.
- **Domains:** only domains that have cards, ordered by most due now, then most Again, then name.
- Reviews of cards that no longer exist are ignored.

Browser: the Learning panel shows the overview above the due cards. It refreshes after every rating and is cleared on erase or disconnect.

The optional summary loads independently of due cards. Unavailable/slow summaries cannot block review; failures display unavailable, never invented zero counts. Current-refresh and connection fences discard stale responses. Forecast labels describe rolling24-hour groups and focus counts Again events, including repeated reviews of a card; these are self-reported ratings, not verified skill grades.

The existing IRuntimeReader has a scoped overview port. Its in-memory default uses canonical state; PostgreSQL reads only card metadata and recent Event projections from RuntimeLearningRows in one repeatable-read/privacy-gated transaction. Prompts/answers, replay Results, unrelated learning kinds and the cold runtime document are not hydrated. Exact .NET timestamp filtering follows a widened SQL candidate window, preserving offsets and submicrosecond boundaries. Invalid selected identities/domain/rating fail closed. No migration, new table, persistent summary, counter or service is added.

This is not a high-volume or50ms performance claim. Card metadata is scanned for global counts, and review time filtering can still scan history; existing kind/card indexes are reused. Profile/explain and consider a reviewed time index before volume rollout. Local PostgreSQL may be unavailable; actual SQL/privacy parity tests must pass in CI before any merge, followed by independent exact-source review.
