# Known v1 behaviour

## Wake arbitration is first arrival

Core grants a wake response to the first eligible device request that reaches arbitration. It does not infer which room or device the user intended, compare wake confidence, or wait for other devices to report. When devices wake at nearly the same time, network and scheduling latency can decide the winner; a losing device may briefly begin preparation before its ticket is rejected by the shared audio-ownership fence.

This is accepted for v1. The global audio lease remains the authority that prevents two devices from holding the live microphone response at once. Android wake remains opt-in and device arbitration is not presented as room-aware targeting.

### Possible later improvement

If real use shows that first-arrival selection is confusing, gather candidates for one wake event over a short window (about 300 ms), keyed by a shared event window, then choose using a bounded device-reported confidence/level and an owner-configured device priority. Claim or prepare the session only after choosing the winner. Before implementing this, define replay-resistant event identity, confidence calibration, privacy-safe telemetry, and timeout/fallback behavior; retain the single-owner audio lease as the final fence.
