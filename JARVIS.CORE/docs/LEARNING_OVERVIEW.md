# Learning overview

`GET /api/learning/overview` (owner only) returns a deterministic summary of the spaced-repetition cards.
No AI and nothing stored: `LearningOverviewBuilder` projects the current cards and review events on read.

- **Forecast:** seven 24-hour windows from now. Window 0 includes everything overdue.
- **Last 7 days:** review counts by rating, plus the recall rate (the share not rated Again). It is null when there are no reviews.
- **Focus:** the domain with the most Again ratings in the last 7 days. It is null when nothing was forgotten, so JARVIS never invents a weakness.
- **Domains:** only domains that have cards, ordered by most due now, then most Again, then name.
- Reviews of cards that no longer exist are ignored.

Browser: the Learning panel shows the overview above the due cards. It refreshes after every rating and is cleared on erase or disconnect.
