using System.Security.Cryptography;
using System.Text;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Domains;

namespace Jarvis.Orchestrator.Workflows;

public sealed class WorkflowService(IWorkflowStore store, TimeProvider clock, IWorkflowCatalog? catalog = null, IRuntimeStore? runtime = null)
{
    private readonly IWorkflowCatalog workflows = catalog ?? new WorkflowCatalog();
    public const int MaximumAttempts = 3;
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    public async Task<WorkflowRun> CreateBriefAsync(CreateInterviewBriefRequest request, CancellationToken ct = default)
    {
        Guard.Require(request.RequestId != Guid.Empty, 400, "RequestId is required.");
        var descriptor = workflows.GetLegacy("interview-brief");
        Guard.Require(request.AllowCloudProcessing, 400, "Explicit cloud processing consent is required for an AI brief.");
        const string fingerprint = "interview-brief:1:cloud-approved";
        var now = clock.GetUtcNow();
        var candidate = new WorkflowRun(request.RequestId, descriptor.Key, descriptor.Version, fingerprint,
            WorkflowStatus.Queued, 0, 1, now, now, now.AddMinutes(5), now, AllowCloudProcessing: true);
        return SameRequest(await store.AddAsync(candidate, ct), fingerprint);
    }

    public async Task<WorkflowRun> CompleteBriefAsync(Guid id, Guid token, InterviewBrief brief, CancellationToken ct = default) =>
        Required(await store.UpdateAsync(id, run =>
        {
            RequireClaim(run, token);
            Guard.Require(run.Kind == "interview-brief" && run.AllowCloudProcessing, 409, "Workflow does not permit cloud processing.");
            return Finish(run, WorkflowStatus.Succeeded, clock.GetUtcNow(), null) with { Brief = brief };
        }, ct));

    public async Task<WorkflowRun> CreateAsync(CreatePracticePlanRequest request, CancellationToken ct = default)
    {
        var descriptor = workflows.GetLegacy("practice-plan");
        Guard.Require(request.RequestId != Guid.Empty, 400, "RequestId is required.");
        var now = clock.GetUtcNow();
        var due = request.NotBefore?.ToUniversalTime() ?? now;
        // Fingerprint uses the requested value, not the server's changing clock on a retry.
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "practice-plan:1:" + (request.NotBefore?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "immediate"))));
        var existing = await store.GetAsync(request.RequestId, ct);
        if (existing is not null) return SameRequest(existing, fingerprint);
        Guard.Require(due >= now.AddMinutes(-1) && due <= now.AddDays(7), 400,
            "NotBefore must be within the next seven days.");
        var candidate = new WorkflowRun(request.RequestId, descriptor.Key, descriptor.Version, fingerprint,
            WorkflowStatus.Queued, 0, 1, now, now, due.AddMinutes(5), due);
        return SameRequest(await store.AddAsync(candidate, ct), fingerprint);
    }

    public Task<WorkflowRun?> ClaimAsync(string workerId, CancellationToken ct = default)
    {
        var worker = Guard.Text(workerId, "WorkerId", 100);
        var now = clock.GetUtcNow();
        return store.ClaimAsync(now, run =>
        {
            if (run.ExpiresAt <= now) return Finish(run, WorkflowStatus.Expired, now, "deadline_expired");
            if (run.Attempt >= MaximumAttempts) return Finish(run, WorkflowStatus.Failed, now, "lease_expired");
            var expiry = now.Add(LeaseDuration);
            if (expiry > run.ExpiresAt) expiry = run.ExpiresAt;
            return run with { Status = WorkflowStatus.Running, Attempt = run.Attempt + 1,
                Revision = run.Revision + 1, UpdatedAt = now, ClaimToken = Guid.NewGuid(), WorkerId = worker,
                LeaseExpiresAt = expiry, AvailableAt = expiry, ErrorCode = null };
        }, ct);
    }

    public async Task<WorkflowRun> CompleteAsync(Guid id, Guid token, PracticePlan result, CancellationToken ct = default)
    {
        return Required(await store.UpdateAsync(id, run =>
        {
            RequireClaim(run, token);
            return Finish(run, WorkflowStatus.Succeeded, clock.GetUtcNow(), null) with { Result = result };
        }, ct));
    }

    public async Task<WorkflowRun> HeartbeatAsync(Guid id, Guid token, CancellationToken ct = default) =>
        Required(await store.UpdateAsync(id, run =>
        {
            RequireClaim(run, token);
            var now = clock.GetUtcNow();
            var expiry = now.Add(LeaseDuration);
            if (expiry > run.ExpiresAt) expiry = run.ExpiresAt;
            return run with { UpdatedAt = now, Revision = run.Revision + 1, LeaseExpiresAt = expiry, AvailableAt = expiry };
        }, ct));

    public async Task<WorkflowRun> FailAsync(Guid id, Guid token, bool transient, CancellationToken ct = default, string? errorCode = null) =>
        Required(await store.UpdateAsync(id, run =>
        {
            RequireClaim(run, token);
            var now = clock.GetUtcNow();
            if (!transient || run.Attempt >= MaximumAttempts)
                return Finish(run, WorkflowStatus.Failed, now, errorCode ?? (transient ? "retry_limit" : "processing_failed"));
            var due = now.AddSeconds(Math.Pow(2, run.Attempt - 1) + Random.Shared.NextDouble());
            if (due >= run.ExpiresAt) return Finish(run, WorkflowStatus.Expired, now, "deadline_expired");
            return Finish(run, WorkflowStatus.RetryScheduled, now, "transient_failure") with { AvailableAt = due };
        }, ct));

    public async Task<WorkflowRun> CancelAsync(Guid id, CancellationToken ct = default) =>
        Required(await store.UpdateAsync(id, run => Terminal(run.Status) ? run :
            Finish(run, WorkflowStatus.Cancelled, clock.GetUtcNow(), null), ct));

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (await store.GetAsync(id, ct) is null) return;
        if (runtime is not null) await runtime.UpdateAsync(s => {
            ReasoningLifecycle.Retire(s, id);
            return true;
        }, ct);
        await store.UpdateAsync(id, run => run.DeletedAt is not null ? run :
            Finish(run, WorkflowStatus.Cancelled, clock.GetUtcNow(), null) with
            { DeletedAt = clock.GetUtcNow(), Brief = null, Result = null, AllowCloudProcessing = false }, ct);
    }

    public static bool Terminal(WorkflowStatus status) => status is WorkflowStatus.Succeeded or
        WorkflowStatus.Failed or WorkflowStatus.Cancelled or WorkflowStatus.Expired;

    private void RequireClaim(WorkflowRun run, Guid token)
    {
        var now = clock.GetUtcNow();
        Guard.Require(run.Status == WorkflowStatus.Running && token != Guid.Empty && run.ClaimToken == token &&
            run.LeaseExpiresAt > now && run.ExpiresAt > now, 409, "Workflow claim expired or was revoked.");
    }

    private static WorkflowRun Finish(WorkflowRun run, WorkflowStatus status, DateTimeOffset now, string? error) =>
        run with { Status = status, Revision = run.Revision + 1, UpdatedAt = now, AvailableAt = null,
            ClaimToken = null, WorkerId = null, LeaseExpiresAt = null, ErrorCode = error };

    private static WorkflowRun SameRequest(WorkflowRun run, string fingerprint)
    {
        Guard.Require(run.DeletedAt is null, 409, "This workflow was deleted. Use a new RequestId for new work.");
        Guard.Require(run.Fingerprint == fingerprint, 409, "RequestId was used for a different workflow request.");
        return run;
    }

    private static WorkflowRun Required(WorkflowRun? run) => run ?? throw new DomainException(404, "Workflow not found.");
}
