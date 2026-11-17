using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs.Learning;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Integrations;

public sealed record ListIntegrations : IQuery<IReadOnlyList<IntegrationStatus>>;
public sealed record GetTransit(string? Lines) : IQuery<IReadOnlyList<TransitLine>>;
public sealed record GetCalendar(string Provider, DateTimeOffset Start, DateTimeOffset End) : IQuery<IReadOnlyList<CalendarEntry>>;
public sealed record GetDailyBriefing : IQuery<DailyBriefing>;
public sealed record GetStorageReadiness : IQuery<StorageReadiness>;

public sealed class IntegrationQueryHandlers(IIntegrationReader reader) :
    IQueryHandler<ListIntegrations, IReadOnlyList<IntegrationStatus>>,
    IQueryHandler<GetTransit, IReadOnlyList<TransitLine>>,
    IQueryHandler<GetCalendar, IReadOnlyList<CalendarEntry>>
{
    public Task<IReadOnlyList<IntegrationStatus>> HandleAsync(ListIntegrations query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(reader.Status());
    }
    public Task<IReadOnlyList<TransitLine>> HandleAsync(GetTransit query, CancellationToken ct = default) => reader.TransitAsync(query.Lines, ct);
    public Task<IReadOnlyList<CalendarEntry>> HandleAsync(GetCalendar query, CancellationToken ct = default) => reader.CalendarAsync(query.Provider, query.Start, query.End, ct);
}

public sealed class DailyBriefingQueryHandler(
    IQueryHandler<GetTransit, IReadOnlyList<TransitLine>> transit,
    IQueryHandler<GetCalendar, IReadOnlyList<CalendarEntry>> calendars,
    IQueryHandler<ListLearningItems, IReadOnlyList<LearningItem>> learning,
    TimeProvider clock) : IQueryHandler<GetDailyBriefing, DailyBriefing>
{
    public async Task<DailyBriefing> HandleAsync(GetDailyBriefing query, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        async Task<BriefingSection> Section(string name, Func<Task<object>> read)
        {
            try { return new(name, true, await read(), null); }
            catch (DomainException ex) { return new(name, false, null, ex.Message); }
            catch (Exception ex) when ((ex is HttpRequestException or JsonException or OperationCanceledException) && !ct.IsCancellationRequested)
            { return new(name, false, null, "Provider unavailable or returned an invalid response."); }
        }
        var sections = await Task.WhenAll(
            Section("TfL", async () => await transit.HandleAsync(new GetTransit(null), ct)),
            Section("Google Calendar", async () => await calendars.HandleAsync(new GetCalendar("google", now, now.AddDays(1)), ct)),
            Section("Microsoft Calendar", async () => await calendars.HandleAsync(new GetCalendar("microsoft", now, now.AddDays(1)), ct)),
            Section("Learning due", async () => await learning.HandleAsync(new ListLearningItems(true), ct)));
        return new(now, sections);
    }
}

public sealed class StorageReadinessQueryHandler(IStorageReadiness readiness) : IQueryHandler<GetStorageReadiness, StorageReadiness>
{
    public Task<StorageReadiness> HandleAsync(GetStorageReadiness query, CancellationToken ct = default) => readiness.CheckAsync(ct);
}
