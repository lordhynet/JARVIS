# Android offline practice — 0.5.0

## What is stored

Android keeps an encrypted snapshot of its current practice screen, cached sessions/questions/exercises/stories, editor drafts, and pending unpaid practice writes. The cache is scoped to the normalized Core address and exact Core credential. AES-GCM authenticates the ciphertext and its Core identity; the encryption key stays in Android Keystore. Files live in the app's no-backup directory and are written atomically. The APK contains no provider keys.

Draft edits are saved locally after a 300 ms debounce. Explicit practice saves persist their complete request and stable operation ID before network dispatch. Technical/coaching answer submission, their versioned corrections, technical self-assessments, and (from 0.6.0) unpaid guided-drill creation can enter the queue. Guided stage payloads retain their drill ID and index. Chat, AI assessments, agents, paid workflows, voice, session creation/deletion and external actions are excluded.

The cache holds at most about 3.9 MB of JSON and 50 pending operations. Hitting a limit reports an error; pending unknown outcomes are not silently evicted. An unreadable or unauthenticated cache is not overwritten automatically.

## Use it

1. Connect to Core once and open a saved session while online so its questions and exercises are cached.
2. If the connection is lost, the app marks cached results as potentially stale. Continue editing, or choose another cached question/exercise in an existing saved session. Creating a new session requires Core.
3. Save an answer or correction. In offline mode it is encrypted locally without dispatch. The app does not claim it was saved on Core.
4. Open **Offline saves → Review queued saves**. When Core is reachable, choose **Retry same save** for an operation. There is no automatic background replay.
5. If the server rejects a stale revision, deleted source or invalid request, the operation is marked **Conflict**. Its original payload remains available for comparison. Choose **Load latest server versions; keep drafts**, reconcile your text, then explicitly discard the obsolete operation and save the reconciled answer.

Retries reuse the operation ID and payload even after process restart or a lost acknowledgement. A successful retry updates the current canonical answer where its context matches; it does not replace editor text. A discard removes only the local operation and does not undo any server write that may already have succeeded.

## Erase and recovery

From Android 0.7.0, snapshots include Core's verified privacy epoch. Before restoring a
snapshot online, Android checks `/api/privacy/status`. An erasure, changed epoch or HTTP
401 invalidates the connection, clears that identity's local practice and saved credentials,
and stops capture. A mistyped key for a new attempt does not erase separately saved data.
Requests carry `X-Jarvis-Data-Epoch`; Core rejects mismatched generations before endpoint
dispatch. Late responses are rejected by the invalidated client. Legacy snapshots without
an epoch migrate only after an online check verifies epoch zero (no prior Core erasure).
Unknown legacy caches are unavailable offline. Verified caches remain available during a
genuine outage; remote erasure cannot reach a fully offline phone until reconnection.
If local file cleanup fails, private screen state and credentials are cleared and the app
reports the cleanup failure for an explicit retry. Per-record retention propagation remains
outside this increment. Install the matching Core header support before Android 0.7.0.

Signing out asks before erasing local drafts, cache, queued operations and saved connection credentials. It leaves Core history unchanged. In-flight cache writes are fenced so they cannot recreate data after erasure. Rotating a Core key or changing the Core address creates a different cache identity; migrating pending work between identities is not implemented.

Automated JVM tests cover queue restoration, lost-acknowledgement identity, edits/revisions, conflict preservation, capacity, route exclusions, ciphertext randomization and rejection of tampering/wrong keys/wrong Core identities. Android release compilation and lint cover platform API compatibility. Physical process-death, airplane-mode, Keystore-loss and power-loss acceptance still need device testing; these checks have not been represented as completed.
