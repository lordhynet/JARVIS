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

## Voice hot-path profile

Run `scripts/measure-runtime.ps1 -VoiceProfile` on a quiet host with a separate loopback PostgreSQL instance started with `shared_preload_libraries=pg_stat_statements`. Use the same `JARVIS_PERF_TEST_POSTGRES` connection settings as the baseline. The script creates the extension in the disposable database and takes before/after [statement counter snapshots](https://www.postgresql.org/docs/17/pgstatstatements.html) filtered to that database. Capture CPU and available memory when comparing runs.

The profile uses pooled connections and disables Core background workers. After five warm-up calls per operation, it measures 200 successful lease checks, 200 companion preparations with fresh request IDs and 200 runtime updates. The clock stays fixed so the synthetic lease and device remain eligible throughout the run. Application timings include transfer and materialization. PostgreSQL statement times measure server-side execution. The row-lock query timing includes loading the runtime document.

On 16 November 2026 the profile used a Windows 11 Pro workstation with a Core i7-1185G7 (4 physical cores, 8 logical CPUs), 15.7 GiB of physical memory and a Docker Desktop VM with 7.6 GiB and 8 logical CPUs. The runtime document was about 6.06 MB in both runs. Other workloads on the workstation make elapsed times approximate. The statement counts are taken from the isolated database.

The baseline performed four full runtime reads and one locked runtime read per companion preparation. The four reads took 310.6 ms p50 to load and 46.5 ms p50 to deserialize, measured separately across 800 calls. The session-candidate query averaged 405.2 ms in PostgreSQL. The selected session query also repeated transcript data across the checkpoint join and averaged 379.4 ms in PostgreSQL as the fixture accumulated checkpoints. The runtime-update save phase took 1,172.7 ms p50 for writes, while PostgreSQL's measured update averaged 78.5 ms. These timings include client and transfer costs and cannot isolate database lock contention.

| Measurement | Before | After |
|---|---:|---:|
| Companion preparation p50 / p95 | 3,004.6 / 4,531.4 ms | 1,270.9 / 2,068.8 ms |
| Per-frame lease check p50 / p95 | 30.2 / 42.6 ms | 26.9 / 35.0 ms |
| Runtime update p50 / p95 | 1,477.6 / 2,224.1 ms | 1,486.7 / 2,199.0 ms |
| Full runtime reads during 200 preparations, excluding the locked claim read | 800 | 0 |
| Session-candidate query, PostgreSQL mean | 405.2 ms | 217.8 ms |

The preparation change projects device and lease fields, and counts messages directly from their stored JSON text. Median preparation fell by 57.7% in this synthetic test. Both runs still validated each frame through PostgreSQL, and the frame-check timings show run-to-run variation. The locked claim read and selected-session transcript query remain part of preparation.

### Realtime frame authorization

Each realtime connection caches its authoritative lease for audio forwarding. The monitor starts a refresh every 500 ms. A snapshot expires one second after the refresh began or when the lease expires, whichever comes first. Committed lease revocation, ownership loss and erasure invalidate matching snapshots in the same Core process. Changes made by another Core process are detected by the next refresh or the freshness deadline.

Provider connection and Core tool dispatch still verify the lease with PostgreSQL. The profile retains the authoritative check for comparison and measures 200 batches of 1,000 in-process frame checks. It asserts that the cached batches issue zero PostgreSQL statements. The timings cover lease validation only. Network, provider, and device playback delays require separate measurement.

| Measurement | Result |
|---|---:|
| Authoritative lease check p50 / p95 | 27.777 / 37.434 ms |
| Cached batch of 1,000 checks p50 / p95 | 0.079 / 0.118 ms |
| SQL SELECT statements in 200 authoritative checks | 800 |
| SQL statements in 200,000 cached checks | 0 |
| Companion preparation p50 / p95 | 1,158.1 / 2,055.4 ms |
| Runtime update p50 / p95 | 1,444.7 / 2,126.5 ms |

The 16 November 2026 result uses the same workstation and workload as the previous profile. The cached timing excludes refreshes, which run independently of frame traffic.

## Seven-day owner soak

This is a runbook for the owner. Do not run it against production or leave Core exposed beyond the LAN for the soak.

1. Create an isolated local PostgreSQL database and a dedicated non-production Core configuration. Keep all real provider keys absent; use the existing fake provider/test mode for automated requests.
2. Run the synthetic harness once and save its JSON output with the date and source commit. Confirm the isolation safeguards and database cleanup succeeded.
3. Start the isolated Core build and exercise only local, non-paid flows: create sessions, append ordinary messages, query `CurrentAsync`, prepare a companion session, and run voice-repair idle sweeps. Avoid provider dispatch and do not enter real personal data.
4. Keep the service and database on the same test machine for seven days. Record daily uptime, runtime document size, database storage, error count, and any measured lock wait. Restart Core once during the window and confirm the isolated data remains readable.
5. At the end, rerun the synthetic harness against a fresh isolated database, compare the JSON metrics with the starting report, and note any drift.
6. Stop the test services, remove the isolated database and test secrets, and record the owner-run dates, commit, and results here. Do not claim the soak complete until all seven days are observed.

Owner soak status: not started. No soak was run as part of the code change.
