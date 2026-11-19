# Core privacy controls

These controls apply to the single-owner Core and browser companion. Android 0.7.0 adds
cache-generation and revoked-access protection as described below.

The browser **Privacy & data** panel downloads a consistent JSON export, previews retention, applies a confirmed preview, and supports confirmed Core erasure. Exports include personal records and derived provider results, but exclude backend configuration, authentication credentials, worker claims and voice ticket secrets.

All backend settings remain in root `appsettings.json`. `Privacy.Retention.Enabled` defaults to false. Session, answer, transcript, cache and artifact ages are independently configurable. Only ended sessions expire; their linked practice content is also purged. Recent answer corrections reset answer age. Minimal replay tombstones and spending records remain.

Erasure requires `ERASE MY CORE DATA`, an operation ID and the current privacy revision. PostgreSQL erases the session, runtime and workflow stores in one transaction. An epoch fence blocks earlier requests and background jobs from reading a reopened generation or publishing old private results. In-memory mode shares an equivalent lock across its stores.

Erasure blocks normal Core operations. Rotate `Jarvis.ApiKey` in root `appsettings.json`, reconnect with the new key, then type `START A NEW CORE`. Paid AI and agents remain paused. Only numeric settlement of existing spending reservations can cross the erasure boundary; this preserves actual charges and overrun freezes without accepting response content or new reservations.

Retention retries reuse the same operation and cutoff. Changed requests cannot reuse a completed operation ID. Content-free job history records removed counts.

These operations remove logical records from the active database. They do not erase backups, PostgreSQL storage remnants/WAL, downloaded exports, provider-held copies or other devices' offline caches. Manage those copies separately. No destructive operation should be run against live data merely to verify these controls.

## Android cache boundary

Android 0.7.0 verifies privacy before restoring cached practice and sends the verified
epoch with API requests. Core rejects stale/malformed epochs before endpoint dispatch.
When the phone observes erasure, an epoch change or rejected authentication, it invalidates
the client, clears that Core identity's practice cache and queued saves, stops capture and
signs out. Saved credentials are removed when they belong to the affected identity; a failed
attempt with a different key/address does not erase a separately saved connection's drafts.
Late responses from an invalidated client cannot restore its content. Cleanup failures
hide private screen state, remove credentials and report the file-removal failure.

Verified offline practice remains usable during an outage; a disconnected phone cannot
receive a remote erasure until reconnecting. Legacy caches without an epoch migrate only
after an online check confirms Core has never been erased. This increment does not propagate
individual-record retention/deletion into every cached view, or manage provider/backup copies.
Cleanup follows the connection being checked. Previously abandoned cache identities for
other addresses/credentials require the explicit local erase action; this is not a device-wide
remote wipe protocol.

Interview capture adds dedicated recording/chunk metadata and AES-GCM audio files.
Audio expires after 30 days, independently of transcript retention. Core export includes
recording metadata only. Recording deletion and full erasure remove owned ciphertext,
including orphaned files; a failed file deletion prevents a success response. Native
recording queues use DPAPI/Android Keystore and retain unacknowledged audio during an
outage or ordinary authorization failure. They remove copies on a confirmed Core erasure
or epoch change for that origin; an offline device learns this on reconnect. This differs
from the older practice-cache cleanup policy described above. No capture starts after a
restart without another explicit owner start. See [recording ADR](adr/0019-interview-recording-storage.md)
for consent, key/volume backup, locking, third-party data boundaries and hardware limits.
