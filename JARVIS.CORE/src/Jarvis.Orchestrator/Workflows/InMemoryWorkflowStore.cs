using System.Text.Json;
using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Workflows;

public sealed class InMemoryWorkflowStore : IWorkflowStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, WorkflowRun> _runs = [];
    private readonly List<WorkflowEvent> _events = [];
    public (List<WorkflowRun> Runs, List<WorkflowEvent> Events) PrivacySnapshot()
    { lock (_gate) return (_runs.Values.Select(Copy).ToList(), _events.ToList()); }
    public void ReplacePrivacyData(IReadOnlyList<WorkflowRun> runs, IReadOnlyList<WorkflowEvent> events)
    { lock (_gate) { _runs.Clear(); _events.Clear(); foreach (var run in runs) _runs.Add(run.Id, Copy(run)); _events.AddRange(events); } }
    private static WorkflowRun Copy(WorkflowRun run) => JsonSerializer.Deserialize<WorkflowRun>(JsonSerializer.Serialize(run))!;

    public Task<WorkflowRun> AddAsync(WorkflowRun candidate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_runs.TryGetValue(candidate.Id, out var existing)) return Task.FromResult(Copy(existing));
            Save(candidate);
            return Task.FromResult(Copy(candidate));
        }
    }

    public Task<WorkflowRun?> ClaimAsync(DateTimeOffset now, Func<WorkflowRun, WorkflowRun> claim, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var run = _runs.Values.Where(r => r.AvailableAt <= now).OrderBy(r => r.AvailableAt).ThenBy(r => r.Id).FirstOrDefault();
            if (run is null) return Task.FromResult<WorkflowRun?>(null);
            var updated = claim(Copy(run));
            Save(updated);
            return Task.FromResult<WorkflowRun?>(Copy(updated));
        }
    }

    public Task<WorkflowRun?> UpdateAsync(Guid id, Func<WorkflowRun, WorkflowRun> update, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_runs.TryGetValue(id, out var run)) return Task.FromResult<WorkflowRun?>(null);
            var updated = update(Copy(run));
            if (updated.Revision != run.Revision) Save(updated);
            return Task.FromResult<WorkflowRun?>(Copy(updated));
        }
    }

    public Task<WorkflowRun?> GetAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(_runs.TryGetValue(id, out var run) ? Copy(run) : null);
    }

    public Task<IReadOnlyList<WorkflowRun>> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult<IReadOnlyList<WorkflowRun>>(_runs.Values.Where(r => r.DeletedAt is null).OrderByDescending(r => r.CreatedAt)
            .ThenBy(r => r.Id).Take(25).Select(Copy).ToList());
    }

    public Task<IReadOnlyList<WorkflowEvent>> EventsAsync(Guid id, int afterRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult<IReadOnlyList<WorkflowEvent>>(_events.Where(e => e.RunId == id && e.Revision > afterRevision)
            .OrderBy(e => e.Revision).Take(100).ToList());
    }

    private void Save(WorkflowRun run)
    {
        _runs[run.Id] = Copy(run);
        _events.Add(new(Guid.NewGuid(), run.Id, run.Revision, run.Status, run.UpdatedAt));
    }
}
