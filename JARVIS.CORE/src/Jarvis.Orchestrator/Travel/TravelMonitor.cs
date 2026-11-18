using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Orchestrator.Travel;

public sealed class TravelMonitor(ITravelStore store, ITravelSource source, TimeProvider clock, ITravelDeliveryPolicy policy)
{
    public async Task<bool> PollAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        if (!await store.ReadAsync(s => Due(s, now), ct)) return false;
        var claim = await store.UpdateAsync((s, _) => {
            var at = clock.GetUtcNow(); if (!Due(s, at)) return (Token: (Guid?)null, Lines: Array.Empty<TravelLine>());
            var token = Guid.NewGuid(); var lines = s.Routes.Values.Where(r => TravelPolicy.PollEligible(r, at)).SelectMany(r => r.Lines).DistinctBy(l => l.Id).ToArray();
            s.Monitor = s.Monitor with { Claim = token, LeaseUntil = at.AddSeconds(90), NextPollAt = at.AddMinutes(2) }; return (Token: (Guid?)token, Lines: lines);
        }, ct);
        if (claim.Token is not { } token) return false;
        try {
            var statuses = await source.StatusAsync(claim.Lines, ct);
            await CompleteAsync(token, statuses, ct); return true;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or DomainException or System.Text.Json.JsonException) {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await store.UpdateAsync((s, _) => {
                if (s.Monitor.Claim != token) return false;
                var failures = Math.Min(s.Monitor.Failures + 1, 10); var minutes = Math.Min(15, Math.Pow(2, failures));
                var delay = Math.Min(15, minutes * (1 + Random.Shared.NextDouble() * .1));
                s.Monitor = s.Monitor with { Claim = null, LeaseUntil = null, Failures = failures, NextPollAt = clock.GetUtcNow().AddMinutes(delay), LastError = "TfL status unavailable; retry is backed off." };
                foreach (var line in claim.Lines) if (s.Lines.TryGetValue(line.Id, out var state)) s.Lines[line.Id] = state with { Candidate = null, CandidateHash = null, CandidateCount = 0 };
                return true;
            }, cleanup.Token);
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested(); return false;
        }
    }
    private static bool Due(TravelState state, DateTimeOffset now) => !(state.Monitor.NextPollAt > now || state.Monitor.LeaseUntil > now) &&
        state.Routes.Values.Any(r => TravelPolicy.PollEligible(r, now));
    private Task<bool> CompleteAsync(Guid token, IReadOnlyList<TravelStatus> statuses, CancellationToken ct) => store.UpdateAsync((s, runtime) => {
        if (s.Monitor.Claim != token || s.Monitor.LeaseUntil <= clock.GetUtcNow()) return false;
        Observe(s, runtime, statuses, clock.GetUtcNow(), policy.NotifyWhenAgentsPaused);
        s.Monitor = s.Monitor with { Claim = null, LeaseUntil = null, Failures = 0, NextPollAt = clock.GetUtcNow().AddMinutes(2), LastError = null }; return true;
    }, ct);
    // Simulation uses this same pure transition inside the store transaction, never a second outbox path.
    public static void Observe(TravelState state, RuntimeState runtime, IReadOnlyList<TravelStatus> statuses, DateTimeOffset now, bool notifyWhenPaused)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TravelPolicy.Zone).DateTime);
        if (state.Monitor.NoticeDay != day) state.Monitor = state.Monitor with { NoticeDay = day, DisruptionNotices = 0 };
        var eligible = state.Routes.Values.Where(r => TravelPolicy.PollEligible(r, now)).SelectMany(r => r.Lines).Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var lineId in eligible) {
            var status = statuses.SingleOrDefault(s => s.LineId.Equals(lineId, StringComparison.OrdinalIgnoreCase));
            var old = state.Lines.GetValueOrDefault(lineId) ?? new TravelLineState(lineId);
            if (status is null) { state.Lines[lineId] = old with { Candidate = null, CandidateHash = null, CandidateCount = 0 }; continue; }
            var fingerprint = TravelPolicy.Fingerprint(status); var updated = old with { LastSeenAt = now };
            if (old.Confirmed is null || TravelPolicy.Fingerprint(old.Confirmed) != fingerprint) {
                var count = old.CandidateHash == fingerprint && old.LastSeenAt >= now.AddMinutes(-5) ? old.CandidateCount + 1 : 1;
                updated = updated with { Candidate = status, CandidateHash = fingerprint, CandidateCount = Math.Min(2, count) };
                if (count >= 2) updated = updated with { Confirmed = status, ReasonHash = TravelPolicy.ReasonHash(status), Candidate = null, CandidateHash = null, CandidateCount = 0 };
            }
            else updated = updated with { Confirmed = status, Candidate = null, CandidateHash = null, CandidateCount = 0 };
            state.Lines[lineId] = updated;
            if (updated.Confirmed is not { } confirmed || TravelPolicy.Fingerprint(confirmed) != fingerprint) continue;
            var active = state.Routes.Values.Where(r => r.Lines.Any(l => l.Id.Equals(lineId, StringComparison.OrdinalIgnoreCase)) && TravelPolicy.Active(r, now)).OrderBy(r => r.Id).ToArray();
            if (active.Length == 0 || runtime.AgentControls.EffectiveAgentsPaused && !notifyWhenPaused || updated.LastNoticeAt > now.AddMinutes(-15)) continue;
            var keys = active.SelectMany(r => TravelPolicy.Occurrences(r, now)).ToArray();
            var last = updated.LastNotified; var recovery = TravelPolicy.Good(confirmed) && last is not null && !TravelPolicy.Good(last);
            var headsUp = !TravelPolicy.Good(confirmed) && keys.Except(updated.WindowKeys ?? []).Any();
            var worsening = last is not null && TravelPolicy.Impact(confirmed) is { } impact && TravelPolicy.Impact(last) is { } previous && impact > previous;
            var reasonChanged = !TravelPolicy.Good(confirmed) && last is not null && TravelPolicy.ReasonHash(confirmed) != TravelPolicy.ReasonHash(last);
            if (!(recovery || !TravelPolicy.Good(confirmed) && last is null || headsUp || worsening || reasonChanged)) continue;
            if (!recovery && state.Monitor.DisruptionNotices >= 6) continue;
            var route = active[0]; var expires = TravelPolicy.ActiveUntil(route, now); if (expires > now.AddMinutes(30)) expires = now.AddMinutes(30);
            var summary = TravelPolicy.ClipUtf8($"{confirmed.LineName}: {confirmed.Description}" + (recovery ? ". Service recovery reported by TfL." : confirmed.Reason.Length > 0 ? ". " + confirmed.Reason : "."), 240);
            var id = AgentRuntime.StableId($"travel-v1:{lineId}:{fingerprint}:{now.UtcTicks}");
            runtime.AgentOutbox.TryAdd(id, new(id, Guid.Empty, summary, now, now, expires, Delivered: true, Category: NotificationCategories.Travel,
                PublicSummary: summary, TravelAuthority: new(route.Id, route.Revision, lineId, TravelPolicy.Occurrences(route, now)[0])));
            state.Lines[lineId] = updated with { LastNoticeAt = now, LastNotified = confirmed, WindowKeys = keys };
            if (!recovery) state.Monitor = state.Monitor with { DisruptionNotices = state.Monitor.DisruptionNotices + 1 };
        }
    }
}
