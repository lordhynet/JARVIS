using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;
using Jarvis.Orchestrator.Domains;

namespace Jarvis.Orchestrator.Workflows;

// Synchronous aggregate transitions share the PostgreSQL runtime lock. No provider I/O occurs in a callback.
public sealed partial class AgentRuntime(IRuntimeStore store, TimeProvider clock, IWorkflowCatalog? catalog = null)
{
    // Compatibility view for old clients; new modules register descriptors, never edit this engine.
    public static readonly string[] Workflows = WorkflowCatalog.BuiltIns.Select(x => x.Key).ToArray();
    private readonly IWorkflowCatalog workflowCatalog = catalog ?? new WorkflowCatalog();
    private IReadOnlyList<string> WorkflowKeys => workflowCatalog.Keys;
    public static bool IsQuiet(AgentControls controls, DateTimeOffset now)
    {
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TrainingProjection.Zone(controls.TimeZone)).DateTime);
        var start = LocalTime(controls.QuietStart); var end = LocalTime(controls.QuietEnd);
        return start != end && (start < end ? local >= start && local < end : local >= start || local < end);
    }
    public static TimeOnly LocalTime(string value) { Guard.Require(TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time), 400, "Time must be HH:mm."); return time; }
    public async Task<AgentSchedule> SaveScheduleAsync(SaveAgentScheduleRequest request, CancellationToken ct)
    {
        Guard.Require(request.RequestId != Guid.Empty, 400, "Request ID is required.");
        var descriptor = await workflowCatalog.ResolveAsync(request.Workflow, ct);
        RequireConsent(descriptor, request.AllowCloudProcessing);
        var title = Guard.Text(request.Title, "Schedule title", 120); var zone = TrainingProjection.Zone(request.TimeZone).Id; LocalTime(request.LocalTime);
        return await store.UpdateAsync(s =>
        {
            var prior = s.AgentSchedules.GetValueOrDefault(request.RequestId);
            if (prior is not null && request.ExpectedRevision is null)
            {
                Guard.Require(prior.Title == title && prior.Workflow == request.Workflow && prior.TimeZone == zone && prior.LocalTime == request.LocalTime &&
                    prior.Enabled == request.Enabled && prior.AllowCloudProcessing == request.AllowCloudProcessing, 409, "Schedule request ID was reused for different input."); return prior;
            }
            Guard.Require(prior is null ? request.ExpectedRevision is null : request.ExpectedRevision == prior.Revision, 409, "Schedule changed. Reload before editing.");
            Guard.Require(prior is not null || s.AgentSchedules.Count < 30, 409, "Schedule limit reached.");
            var updated = new AgentSchedule(request.RequestId, title, request.Workflow, zone, request.LocalTime, request.Enabled, request.AllowCloudProcessing,
                (prior?.Revision ?? 0) + 1, prior?.CreatedAt ?? clock.GetUtcNow(), prior?.LastOccurrence);
            s.AgentSchedules[updated.Id] = updated; return updated;
        }, ct);
    }
    public async Task<AgentControls> ControlsAsync(AgentControls controls, CancellationToken ct)
    {
        var paused = controls.PausedWorkflows?.Distinct().ToList() ?? [];
        var keys = (await workflowCatalog.ListAsync(ct)).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        Guard.Require(paused.All(keys.Contains), 400, "Unknown workflow pause target."); LocalTime(controls.QuietStart); LocalTime(controls.QuietEnd); TrainingProjection.Zone(controls.TimeZone);
        return await store.UpdateAsync(s =>
        {
            s.AgentControls = controls with { Paused = false, PaidAiPaused = controls.EffectivePaidAiPaused, AgentsPaused = controls.EffectiveAgentsPaused, PausedWorkflows = paused };
            return s.AgentControls;
        }, ct);
    }
    public async Task<AgentRunView> StartAsync(StartAgentRunRequest request, CancellationToken ct)
    {
        Guard.Require(request.RequestId != Guid.Empty, 400, "Request ID is required.");
        var descriptor = await workflowCatalog.ResolveAsync(request.Workflow, ct);
        RequireConsent(descriptor, request.AllowCloudProcessing);
        var brief = string.IsNullOrWhiteSpace(request.Brief) ? "" : Guard.Text(request.Brief, "Goal brief", 2000);
        var occurrence = "manual:" + Hash(brief) + (request.HoldForSetup ? ":setup" : "");
        return await store.UpdateAsync(s =>
        {
            if (s.AgentRuns.TryGetValue(request.RequestId, out var existing))
            {
                Guard.Require(existing.DeletedAt is null && existing.Workflow == request.Workflow && existing.AllowCloudProcessing == request.AllowCloudProcessing &&
                    existing.Occurrence == occurrence, 409, "Agent request ID belongs to different or deleted work."); return View(s, existing);
            }
            var created = Create(s, request.RequestId, null, occurrence, request.Workflow, request.AllowCloudProcessing, brief, clock.GetUtcNow(), descriptor);
            if (request.HoldForSetup) s.AgentRuns[created.Id] = created = created with { Status = AgentRunStatus.AwaitingSetup, Deadline = clock.GetUtcNow().AddDays(1) };
            return View(s, created);
        }, ct);
    }
    private AgentRun Create(RuntimeState s, Guid id, Guid? scheduleId, string occurrence, string workflow, bool consent, string brief, DateTimeOffset now, WorkflowDescriptor descriptor)
    {
        RequireConsent(descriptor, consent);
        Guard.Require(!s.AgentControls.EffectiveAgentsPaused && !(s.AgentControls.PausedWorkflows?.Contains(workflow) ?? false), 409, "Workflow is paused.");
        Guard.Require(s.AgentRuns.Count < 2000, 409, "Agent history capacity reached.");
        var roles = descriptor.Roles;
        var tasks = roles.Select(role => new AgentTask(StableId(id + ":" + role), id, role == AgentRole.Review ? id : null,
            role, 1, brief, AgentTaskStatus.Queued, 0, now)).ToList();
        var run = new AgentRun(id, scheduleId, occurrence, workflow, 1, consent, now, now.AddMinutes(5), AgentRunStatus.Queued, tasks.Select(x => x.Id).ToList(),
            WorkflowPolicy: WorkflowCatalog.Snapshot(descriptor));
        s.AgentRuns.Add(id, run); foreach (var task in tasks) s.AgentTasks.Add(task.Id, task); return run;
    }
    public async Task<int> TickAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var available = (await workflowCatalog.ListAsync(ct)).ToDictionary(x => x.Key, StringComparer.Ordinal);
        var due = await store.ReadAsync(s => AgentEligibility.HasTickWork(s, now, available), ct);
        return due ? await TickCoreAsync(available, ct) : 0;
    }
    private Task<int> TickCoreAsync(IReadOnlyDictionary<string, WorkflowDescriptor> available, CancellationToken ct) => store.UpdateAsync(s =>
    {
        var now = clock.GetUtcNow(); if (!AgentEligibility.HasTickWork(s, now, available)) return 0;
        var count = TickPlanning(s, now, available);
        if (!s.AgentControls.EffectiveAgentsPaused) foreach (var schedule in s.AgentSchedules.Values.Where(x => x.Enabled).ToList())
        {
            if (!available.ContainsKey(schedule.Workflow) || s.AgentControls.PausedWorkflows?.Contains(schedule.Workflow) == true) continue;
            var zone = TrainingProjection.Zone(schedule.TimeZone); var local = TimeZoneInfo.ConvertTime(now, zone);
            var due = local.Date + LocalTime(schedule.LocalTime).ToTimeSpan();
            if (zone.IsInvalidTime(due)) due = due.AddHours(1);
            var offset = zone.IsAmbiguousTime(due) ? zone.GetAmbiguousTimeOffsets(due).Max() : zone.GetUtcOffset(due);
            var at = new DateTimeOffset(due, offset).ToUniversalTime();
            var occurrence = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ":" + schedule.LocalTime;
            // Skip stale daily occurrences rather than replaying a backlog after sleep.
            if (available[schedule.Workflow].RequiresCloud && !schedule.AllowCloudProcessing) continue;
            if (now < at || now - at > TimeSpan.FromMinutes(30) || schedule.LastOccurrence == occurrence) continue;
            var id = StableId(schedule.Id + ":" + occurrence);
            if (!s.AgentRuns.ContainsKey(id)) { Create(s, id, schedule.Id, occurrence, schedule.Workflow, schedule.AllowCloudProcessing, schedule.Title, now, available[schedule.Workflow]); count++; }
            s.AgentSchedules[schedule.Id] = schedule with { LastOccurrence = occurrence };
        }
        foreach (var run in s.AgentRuns.Values.Where(x => x.DeletedAt is null && x.Status is AgentRunStatus.Queued or AgentRunStatus.Running or AgentRunStatus.AwaitingSetup).ToList())
        {
            var tasks = run.TaskIds.Select(id => s.AgentTasks[id]).ToList();
            if (run.Deadline <= now)
            {
                foreach (var task in tasks.Where(x => x.Status is AgentTaskStatus.Queued or AgentTaskStatus.Running or AgentTaskStatus.RetryScheduled or AgentTaskStatus.AwaitingApproval or AgentTaskStatus.AwaitingReceipt))
                    s.AgentTasks[task.Id] = task with { Status = AgentTaskStatus.Failed, ClaimToken = null, ErrorCode = "deadline_expired", CompletedAt = now };
                s.AgentRuns[run.Id] = run with { Status = AgentRunStatus.Expired }; continue;
            }
            if (tasks.Any(x => x.Status is AgentTaskStatus.Failed or AgentTaskStatus.NeedsReconciliation))
            {
                s.AgentRuns[run.Id] = run with { Status = tasks.Any(x => x.Status == AgentTaskStatus.NeedsReconciliation) ? AgentRunStatus.NeedsReconciliation : AgentRunStatus.Failed };
                foreach (var task in tasks.Where(x => x.Status is AgentTaskStatus.Queued or AgentTaskStatus.RetryScheduled)) s.AgentTasks[task.Id] = task with { Status = AgentTaskStatus.Cancelled, CompletedAt = now };
            }
            else if (tasks.All(x => x.Status == AgentTaskStatus.Succeeded))
            {
                s.AgentRuns[run.Id] = run with { Status = AgentRunStatus.Succeeded };
                var noticeId = StableId(run.Id + ":notification-v1");
                s.AgentOutbox.TryAdd(noticeId, new(noticeId, run.Id, run.Workflow + " is ready", now, now, now.AddHours(6)));
            }
        }
        if (!s.AgentControls.EffectiveAgentsPaused)
        {
            var local = TimeZoneInfo.ConvertTime(now, TrainingProjection.Zone(s.AgentControls.TimeZone)); var time = TimeOnly.FromDateTime(local.DateTime);
            var start = LocalTime(s.AgentControls.QuietStart); var end = LocalTime(s.AgentControls.QuietEnd);
            var quiet = start == end ? false : start < end ? time >= start && time < end : time >= start || time < end;
            if (!quiet) foreach (var message in s.AgentOutbox.Values.Where(x => !x.Delivered && x.DeliverAfter <= now && x.ExpiresAt > now).ToList())
                s.AgentOutbox[message.Id] = message with { Delivered = true };
        }
        return count;
    }, ct);
    public async Task<AgentTask?> ClaimAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = await store.ReadAsync(s => AgentEligibility.HasClaimWork(s, now), ct);
        return due ? await ClaimCoreAsync(ct) : null;
    }
    private Task<AgentTask?> ClaimCoreAsync(CancellationToken ct) => store.UpdateAsync(s =>
    {
        var now = clock.GetUtcNow(); if (!AgentEligibility.HasClaimWork(s, now)) return null;
        foreach (var stale in s.AgentTasks.Values.Where(x => x.Status == AgentTaskStatus.Running && x.LeaseExpiresAt <= now).ToList())
        {
            CoreTelemetry.LeaseRecoveries.Add(1);
            var uncertain = s.UsageReservations.Values.Any(x => x.RunId == stale.Id && x.ActualUsd is null);
            s.AgentTasks[stale.Id] = stale with { Status = uncertain ? AgentTaskStatus.NeedsReconciliation : stale.Attempt >= 3 ? AgentTaskStatus.Failed : AgentTaskStatus.RetryScheduled,
                AvailableAt = now.AddSeconds(Math.Pow(2, stale.Attempt) + Random.Shared.NextDouble()), ClaimToken = null, CompletedAt = uncertain ? now : null,
                ErrorCode = uncertain ? "worker_lost_unknown_outcome" : "worker_recovered" };
        }
        if (s.AgentTasks.Values.Count(x => x.Status == AgentTaskStatus.Running && x.LeaseExpiresAt > now) >= 2) return null;
        var task = s.AgentTasks.Values.Where(x => x.Status is AgentTaskStatus.Queued or AgentTaskStatus.RetryScheduled && x.AvailableAt <= now)
            .OrderBy(x => x.AvailableAt).ThenBy(x => x.Id).FirstOrDefault(x =>
            {
                var run = s.AgentRuns[x.RunId];
                return run.DeletedAt is null && run.Status is AgentRunStatus.Queued or AgentRunStatus.Running && run.Deadline > now &&
                    s.AgentControls.PausedWorkflows?.Contains(run.Workflow) != true &&
                    (x.Role != AgentRole.Review || run.TaskIds.Where(id => id != x.Id).All(id => s.AgentTasks[id].Status == AgentTaskStatus.Succeeded));
            });
        if (task is null) return null;
        var claimed = task with { Status = AgentTaskStatus.Running, Attempt = task.Attempt + 1, ClaimToken = Guid.NewGuid(), LeaseExpiresAt = now.AddSeconds(60) };
        s.AgentTasks[task.Id] = claimed; s.AgentRuns[task.RunId] = s.AgentRuns[task.RunId] with { Status = AgentRunStatus.Running }; return claimed;
    }, ct);
    public Task<bool> RenewAsync(Guid id, Guid token, CancellationToken ct) => store.UpdateAsync(s =>
    {
        Guard.Require(s.AgentTasks.TryGetValue(id, out var task) && task.ClaimToken == token && task.Status == AgentTaskStatus.Running && task.LeaseExpiresAt > clock.GetUtcNow(), 409, "Task claim expired.");
        var run = s.AgentRuns[task.RunId];
        Guard.Require(!s.AgentControls.EffectiveAgentsPaused && s.AgentControls.PausedWorkflows?.Contains(run.Workflow) != true && run.DeletedAt is null && run.Status == AgentRunStatus.Running && run.Deadline > clock.GetUtcNow(), 409, "Task is paused, cancelled or expired.");
        s.AgentTasks[id] = task with { LeaseExpiresAt = clock.GetUtcNow().AddSeconds(60) }; return true;
    }, ct);
    public Task<bool> CompleteAsync(Guid id, Guid token, string output, string? error, bool uncertain, CancellationToken ct)
    {
        Guard.Require(output.Length <= 16000, 502, "Agent output is too large.");
        return store.UpdateAsync(s =>
        {
            Guard.Require(s.AgentTasks.TryGetValue(id, out var task) && task.Status == AgentTaskStatus.Running && task.ClaimToken == token && task.LeaseExpiresAt > clock.GetUtcNow(), 409, "Agent task claim expired or changed.");
            var run = s.AgentRuns[task.RunId];
            Guard.Require(run.DeletedAt is null && run.Status == AgentRunStatus.Running && run.Deadline > clock.GetUtcNow() && !s.AgentControls.EffectiveAgentsPaused && s.AgentControls.PausedWorkflows?.Contains(run.Workflow) != true, 409, "Agent run is cancelled, paused or expired.");
            Guard.Require(error is not null || (task.SourceRefs ?? []).All(source => SourceCurrent(s, source, clock.GetUtcNow())), 409, "Agent source changed during processing. Start a fresh run.");
            s.AgentTasks[id] = task with { Status = error is null ? AgentTaskStatus.Succeeded : uncertain ? AgentTaskStatus.NeedsReconciliation : AgentTaskStatus.Failed,
                Output = error is null ? output : null, ErrorCode = error, ClaimToken = null, LeaseExpiresAt = null, CompletedAt = clock.GetUtcNow() }; return true;
        }, ct);
    }
    public Task<bool> CancelAsync(Guid id, bool delete, CancellationToken ct) => store.UpdateAsync(s =>
    {
        Guard.Require(s.AgentRuns.TryGetValue(id, out var run), 404, "Agent run not found.");
        AgentToolLifecycle.Retire(s, id, clock.GetUtcNow(), delete);
        s.AgentRuns[id] = run with { Status = AgentRunStatus.Cancelled, DeletedAt = delete ? clock.GetUtcNow() : run.DeletedAt,
            WorkflowPolicy = delete && run.WorkflowPolicy is { } policy ? policy with { DomainContext = null, PracticeTemplate = null } : run.WorkflowPolicy };
        foreach (var taskId in run.TaskIds)
        {
            var task = s.AgentTasks[taskId]; s.AgentTasks[taskId] = task with { Status = AgentTaskStatus.Cancelled, ClaimToken = null, Input = delete ? "" : task.Input, Output = delete ? null : task.Output };
            if (delete) ReasoningLifecycle.Retire(s, taskId);
        }
        foreach (var note in s.AgentOutbox.Values.Where(x => x.RunId == id).ToList()) s.AgentOutbox.Remove(note.Id);
        return true;
    }, ct);
    public static AgentRunView View(RuntimeState s, AgentRun run) => new(run, run.TaskIds.Select(id => s.AgentTasks[id])
        .Select(x => new AgentTaskView(x.Id, x.RunId, x.Role, x.Status, x.Attempt,
            (x.SourceRefs ?? []).All(source => SourceCurrent(s, source)) ? x.Output : null,
            (x.SourceRefs ?? []).All(source => SourceCurrent(s, source)) ? x.ErrorCode : "source_changed")).ToList());
    public static string LearningVersion(LearningItem item) => Hash(item.Prompt + "\n" + item.Answer);
    public static bool SourceCurrent(RuntimeState s, AgentSourceRef source, DateTimeOffset? at = null) => source.Kind switch
    {
        "story" => s.Stories.TryGetValue(source.Id, out var story) && story.Version.ToString(CultureInfo.InvariantCulture) == source.Version,
        "learning" => s.Learning.TryGetValue(source.Id, out var item) && LearningVersion(item) == source.Version,
        "import" => s.ImportedDocuments.TryGetValue(source.Id, out var document) && document.DeletedAt is null && document.ContentHash == source.Version,
        "voice" => s.VoiceTurns.TryGetValue(source.Id, out var turn) && !turn.Restricted && turn.Role == ChatRoles.User && !s.RetiredSessions.Contains(turn.SessionId) &&
            PrivacyData.Hash(System.Text.Json.JsonSerializer.Serialize(turn)) == source.Version,
        "memory" => s.ContextMemories.TryGetValue(source.Id, out var memory) && Jarvis.Orchestrator.Intelligence.ContextMemoryService.Version(memory) == source.Version &&
            memory.ExpiresAt > (at ?? DateTimeOffset.UtcNow) && Jarvis.Orchestrator.Intelligence.ContextMemoryService.Current(s, memory),
        _ => false
    };
    public static void InvalidateSource(RuntimeState s, string kind, Guid id)
    {
        Jarvis.Orchestrator.Intelligence.ContextMemoryService.DropSource(s, kind, id);
        foreach (var task in s.AgentTasks.Values.Where(x => x.SourceRefs?.Any(r => r.Kind == kind && r.Id == id) == true).ToList())
        {
            AgentToolLifecycle.Retire(s, task.RunId, DateTimeOffset.UtcNow, true);
            s.AgentTasks[task.Id] = task with { Output = null, Input = "", ClaimToken = null, Status = AgentTaskStatus.Cancelled, ErrorCode = "source_deleted" };
            s.AgentRuns[task.RunId] = s.AgentRuns[task.RunId] with { Status = AgentRunStatus.SourceDeleted };
            ReasoningLifecycle.Retire(s, task.Id);
            foreach (var notice in s.AgentOutbox.Values.Where(x => x.RunId == task.RunId).ToList()) s.AgentOutbox.Remove(notice.Id);
        }
    }
    public static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16]);
    private static void RequireConsent(WorkflowDescriptor descriptor, bool consent) => Guard.Require(consent || !descriptor.RequiresCloud,
        400, "This workflow requires explicit cloud-processing consent. Local practice reminders remain available without it.");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
