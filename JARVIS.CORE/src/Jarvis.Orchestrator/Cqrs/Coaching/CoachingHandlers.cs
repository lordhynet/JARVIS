using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Orchestrator.Cqrs.Coaching;

public sealed record SaveCoaching(SaveCoachingAnswerRequest Request) : ICommand<CoachingAttemptView>;
public sealed record CorrectCoaching(Guid Id, CorrectCoachingAnswerRequest Request) : ICommand<CoachingAttemptView>;
public sealed record DeleteCoaching(Guid Id, int Revision) : ICommand<bool>;
public sealed record StartProgramme(StartProgrammeRequest Request) : ICommand<ImprovementProgramme>;
public sealed record ProgrammeCheckIn(Guid Id, ProgrammeCheckInRequest Request) : ICommand<ImprovementProgramme>;
public sealed record DeleteProgramme(Guid Id, int Revision) : ICommand<bool>;
public sealed class CoachingCommandHandlers(CoachingService service, ProgrammeService programmes) :
    ICommandHandler<SaveCoaching, CoachingAttemptView>, ICommandHandler<CorrectCoaching, CoachingAttemptView>,
    ICommandHandler<DeleteCoaching, bool>, ICommandHandler<StartProgramme, ImprovementProgramme>,
    ICommandHandler<ProgrammeCheckIn, ImprovementProgramme>, ICommandHandler<DeleteProgramme, bool>
{
    public Task<CoachingAttemptView> HandleAsync(SaveCoaching c, CancellationToken ct = default) => service.SaveAsync(c.Request, ct);
    public Task<CoachingAttemptView> HandleAsync(CorrectCoaching c, CancellationToken ct = default) => service.CorrectAsync(c.Id, c.Request, ct);
    public Task<bool> HandleAsync(DeleteCoaching c, CancellationToken ct = default) => service.DeleteAsync(c.Id, c.Revision, ct);
    public Task<ImprovementProgramme> HandleAsync(StartProgramme c, CancellationToken ct = default) => programmes.StartAsync(c.Request, ct);
    public Task<ImprovementProgramme> HandleAsync(ProgrammeCheckIn c, CancellationToken ct = default) => programmes.CheckInAsync(c.Id, c.Request, ct);
    public Task<bool> HandleAsync(DeleteProgramme c, CancellationToken ct = default) => programmes.DeleteAsync(c.Id, c.Revision, ct);
}
public sealed record GetCoachingPrompt(string ExerciseId, Guid? StoryId, int? StoryVersion, int? ClaimIndex) : IQuery<CoachingPrompt>;
public sealed record GetCoaching(Guid Id) : IQuery<CoachingAttemptView>;
public sealed record ListCoaching(Guid SessionId) : IQuery<IReadOnlyList<CoachingAttemptSummary>>;
public sealed record GetProgramme : IQuery<ProgrammeView>;
public sealed record GetSessionReview(Guid SessionId) : IQuery<SessionReview>;
public sealed class CoachingQueryHandlers(IRuntimeReader reader, ISessionReader sessions, TimeProvider clock) :
    IQueryHandler<GetCoachingPrompt, CoachingPrompt>, IQueryHandler<GetCoaching, CoachingAttemptView>,
    IQueryHandler<ListCoaching, IReadOnlyList<CoachingAttemptSummary>>, IQueryHandler<GetProgramme, ProgrammeView>,
    IQueryHandler<GetSessionReview, SessionReview>
{
    public Task<CoachingPrompt> HandleAsync(GetCoachingPrompt q, CancellationToken ct = default) => reader.ReadAsync(s => CoachingService.Prompt(s, q.ExerciseId, q.StoryId, q.StoryVersion, q.ClaimIndex), ct);
    public Task<CoachingAttemptView> HandleAsync(GetCoaching q, CancellationToken ct = default) => reader.ReadAsync(s =>
    {
        Guard.Require(s.CoachingAttempts.TryGetValue(q.Id, out var attempt) && attempt.DeletedAt is null, 404, "Coaching attempt not found."); return CoachingService.View(s, attempt);
    }, ct);
    public Task<IReadOnlyList<CoachingAttemptSummary>> HandleAsync(ListCoaching q, CancellationToken ct = default) => reader.ReadAsync<IReadOnlyList<CoachingAttemptSummary>>(s =>
        s.CoachingAttempts.Values.Where(x => x.SessionId == q.SessionId && x.DeletedAt is null).OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(50)
            .Select(x => new CoachingAttemptSummary(x.Id, x.SessionId, x.ExerciseId, CoachingCatalog.Get(x.ExerciseId).Title, x.Revision, x.CreatedAt,
                TextCoach.Analyse(x.Answers[^1].Text, null).WordCount, CoachingService.SourceCurrent(s, x))).ToList(), ct);
    public Task<ProgrammeView> HandleAsync(GetProgramme q, CancellationToken ct = default) => reader.ReadAsync(s =>
    {
        var programme = s.ActiveProgrammeId is Guid id ? s.Programmes.GetValueOrDefault(id) : null;
        return new ProgrammeView(programme, programme is null ? 0 : ProgrammeService.Week(programme, clock.GetUtcNow()), CoachingCatalog.Weeks,
            "Weekly check-ins are user reports, not proof of practice or a competence score.");
    }, ct);
    public async Task<SessionReview> HandleAsync(GetSessionReview q, CancellationToken ct = default)
    {
        var session = await sessions.GetAsync(q.SessionId, ct);
        Guard.Require(session is not null, 404, "Session not found.");
        return await reader.ReadAsync(s =>
        {
            var technical = s.TrainingAttempts.Values.Where(x => x.SessionId == q.SessionId && x.DeletedAt is null).OrderBy(x => x.Sequence).ToList();
            var coaching = s.CoachingAttempts.Values.Where(x => x.SessionId == q.SessionId && x.DeletedAt is null).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToList();
            var analyses = coaching.Select(x => CoachingService.View(s, x).Analysis).ToList();
            var rated = technical.Where(x => !x.Skipped && x.CanonicalAssessment?.ContentScore is not null)
                .GroupBy(x => x.QuestionId).Select(g => g.Last()).ToList();
            var weaknesses = rated.GroupBy(x => QuestionBank.Get(x.QuestionId).Domain)
                .Select(g => new WeakDomain(g.Key, g.Average(x => x.CanonicalAssessment!.ContentScore!.Value), g.Count(), string.Join(", ", g.Select(x => x.CanonicalAssessment!.Source).Distinct()))).OrderBy(x => x.MeanScore).ToList();
            var next = analyses.SelectMany(x => x.NextDrills).Distinct().Take(4).ToList();
            if (weaknesses.FirstOrDefault() is { } weakest) next.Insert(0, $"Revisit {weakest.Domain}: current session self-rating {weakest.MeanScore:0.0}/4.");
            if (next.Count == 0) next.Add("Save an answer in Technical Practice or Coaching to begin a review.");
            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { session.Id, session.UpdatedAt,
                Technical = technical.Select(x => new { x.Id, x.Revision }), Coaching = coaching.Select(x => new { x.Id, x.Revision, Current = CoachingService.SourceCurrent(s, x) }) })));
            return new SessionReview(session.Id, session.Title, fingerprint, technical.Count(x => !x.Skipped), technical.Count(x => x.Skipped), coaching.Count,
                session.Messages?.Count(x => x.Role == ChatRoles.User) ?? 0,
                technical.Select(x => QuestionBank.Get(x.QuestionId).Prompt).Concat(coaching.Select(x => CoachingCatalog.Get(x.ExerciseId).Prompt)).Distinct().Take(50).ToList(),
                weaknesses, analyses.SelectMany(x => x.Observations).GroupBy(x => x.Code).Select(g => new TextObservation(g.Key, g.First().Detail,
                    g.SelectMany(x => x.Evidence).Distinct().Take(6).ToList())).Take(8).ToList(), next.Take(5).ToList(), "Self ratings and deterministic text observations; no automatic competence or speech score.");
        }, ct);
    }
}
