# Runtime performance baseline and soak runbook

## Synthetic baseline

Run `scripts/measure-runtime.ps1` only against a local PostgreSQL instance. The test-only generator creates a randomly named database, applies migrations, loads synthetic data, records the metrics below, then drops that database. It requires an explicit `JARVIS_PERF_TEST_POSTGRES` connection string targeting `localhost`/loopback and the `postgres` database. The script refuses a remote host or a different database; it does not read `appsettings.json`, call an AI provider, or touch production data.

The generated load contains 200 sessions × 500 messages, 2,000 learning cards, 500 completed agent runs and a runtime document with 700 synthetic voice turns (each text is capped below the application’s transcript limit). The test writes a JSON report to the system temporary directory and prints it. Record the generated date and report values in the baseline table below after each intentional runtime/persistence change.

### Latest measured baseline

| Measurement | Result |
|---|---:|
| Runtime document size | 6,063,976 bytes (5.78 MiB) |
| `UpdateAsync` `RuntimeLockWait` p50 / p95 | 950.858 / 1,651.895 ms (12 samples) |
| Runtime bytes histogram p50 / p95 | 6,063,976 / 6,063,977 bytes (12 samples) |
| `AudioOwnership.CurrentAsync` p50 / p95 | 756.372 / 1,035.513 ms (25 samples) |
| Companion prepare p50 / p95 | 8,489.454 / 9,818.578 ms (10 samples) |
| Pending voice repair / idle repair | 3,939.162 / 500.870 ms |
| Idle worker database calls per minute | 714.94 (60-second observation) |

Dataset and report date: 16 November 2026, disposable PostgreSQL 17 on loopback, 200 sessions × 500 messages, 2,000 learning cards, 500 completed agent runs, 700 synthetic voice turns. Harness passed 1/1. JSON report: `%TEMP%/jarvis-runtime-measurement-34960.json` on the measurement workstation. The run took about 4m22s. No production system or paid provider was used.

### Extraction threshold

The decision sentinel is either (a) `RuntimeLockWait` p95 above 50 ms or (b) serialized runtime JSON above 10 MiB. This run crosses the latency sentinel (1,651.895 ms p95) while staying below the size sentinel (5.78 MiB). The current instrument records elapsed time through context creation, privacy-gate acquisition and runtime row-lock acquisition, so it is not a pure PostgreSQL row-lock wait measurement. Decision: do not extract tables from this single synthetic run. First profile those phases separately and repeat on the intended isolated deployment-class host with representative concurrency. If p95 still exceeds 50 ms there, a targeted relational extraction is justified; compare before/after using this harness. No extraction was performed in Item 2.

## Seven-day owner soak

This is a runbook for the owner. Do not run it against production or leave Core exposed beyond the LAN for the soak.

1. Create an isolated local PostgreSQL database and a dedicated non-production Core configuration. Keep all real provider keys absent; use the existing fake provider/test mode for automated requests.
2. Run the synthetic harness once and save its JSON output with the date and source commit. Confirm the isolation safeguards and database cleanup succeeded.
3. Start the isolated Core build and exercise only local, non-paid flows: create sessions, append ordinary messages, query `CurrentAsync`, prepare a companion session, and run voice-repair idle sweeps. Avoid provider dispatch and do not enter real personal data.
4. Keep the service and database on the same test machine for seven days. Record daily uptime, runtime document size, database storage, error count, and any measured lock wait. Restart Core once during the window and confirm the isolated data remains readable.
5. At the end, rerun the synthetic harness against a fresh isolated database, compare the JSON metrics with the starting report, and note any drift.
6. Stop the test services, remove the isolated database and test secrets, and record the owner-run dates, commit, and results here. Do not claim the soak complete until all seven days are observed.

Owner soak status: not started. No soak was run as part of the code change.
