using System.Globalization;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Orchestrator.Coaching;

public sealed class ProgrammeService(IRuntimeStore store, TimeProvider clock)
{
    public static DateOnly StartDate(string text)
    {
        Guard.Require(DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date), 400, "Start date must be yyyy-MM-dd."); return date;
    }
    public static int Week(ImprovementProgramme programme, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TrainingProjection.Zone(programme.TimeZone)).DateTime);
        var elapsed = today.DayNumber - StartDate(programme.LocalStartDate).DayNumber;
        return elapsed < 0 ? 0 : Math.Min(13, elapsed / 7 + 1);
    }
    public Task<ImprovementProgramme> StartAsync(StartProgrammeRequest request, CancellationToken ct)
    {
        Guard.Require(request.RequestId != Guid.Empty, 400, "RequestId is required.");
        var date = StartDate(request.LocalStartDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var zone = TrainingProjection.Zone(request.TimeZone).Id;
        return store.UpdateAsync(s =>
        {
            if (s.Programmes.TryGetValue(request.RequestId, out var prior))
            {
                Guard.Require(prior.DeletedAt is null && prior.LocalStartDate == date && prior.TimeZone == zone, 409, "RequestId belongs to a different or deleted programme."); return prior;
            }
            Guard.Require(s.Programmes.Count < 10, 409, "Programme history is full for this personal test build.");
            var programme = new ImprovementProgramme(request.RequestId, date, zone, 1, clock.GetUtcNow(), []);
            s.Programmes.Add(programme.Id, programme); s.ActiveProgrammeId = programme.Id; return programme;
        }, ct);
    }
    public Task<ImprovementProgramme> CheckInAsync(Guid id, ProgrammeCheckInRequest request, CancellationToken ct)
    {
        Guard.Require(request.RequestId != Guid.Empty && request.Week is >= 1 and <= 12, 400, "RequestId and week 1–12 are required.");
        var note = string.IsNullOrWhiteSpace(request.Note) ? "" : Guard.Text(request.Note, "Check-in note", 1000);
        return store.UpdateAsync(s =>
        {
            Guard.Require(s.Programmes.TryGetValue(id, out var programme) && programme.DeletedAt is null, 404, "Programme not found.");
            var previous = s.Programmes.Values.SelectMany(p => p.CheckIns.Select(c => (ProgrammeId: p.Id, CheckIn: c))).FirstOrDefault(x => x.CheckIn.Id == request.RequestId);
            if (previous.CheckIn is not null)
            {
                Guard.Require(previous.ProgrammeId == id && previous.CheckIn.Week == request.Week && previous.CheckIn.Note == note, 409, "Check-in request ID was reused for different input."); return programme;
            }
            Guard.Require(programme.Revision == request.ExpectedRevision && request.ExpectedRevision > 0, 409, "Programme changed. Reload before checking in.");
            Guard.Require(request.Week <= Week(programme, clock.GetUtcNow()), 400, "A future week cannot be checked in yet.");
            Guard.Require(programme.CheckIns.All(x => x.Week != request.Week), 409, "This week already has a check-in.");
            var updated = programme with { Revision = checked(programme.Revision + 1), CheckIns = [..programme.CheckIns, new(request.RequestId, request.Week, note, clock.GetUtcNow())] };
            s.Programmes[id] = updated; return updated;
        }, ct);
    }
    public Task<bool> DeleteAsync(Guid id, int revision, CancellationToken ct) => store.UpdateAsync(s =>
    {
        if (!s.Programmes.TryGetValue(id, out var programme) || programme.DeletedAt is not null) return false;
        Guard.Require(programme.Revision == revision && revision > 0, 409, "Programme changed. Reload before deleting.");
        s.Programmes[id] = programme with { DeletedAt = clock.GetUtcNow(), CheckIns = [], Revision = checked(programme.Revision + 1) };
        if (s.ActiveProgrammeId == id) s.ActiveProgrammeId = null;
        return true;
    }, ct);
}
