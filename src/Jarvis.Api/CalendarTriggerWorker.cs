using System.Globalization;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs.Integrations;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Api;

public sealed class CalendarTriggerSync(IIntegrationReader calendar, IRuntimeReader reader, AgentRuntime runtime,
    IConfiguration config, TimeProvider clock)
{
    public async Task<int> SyncAsync(CancellationToken ct)
    {
        Guard.Require(config.GetValue("Automation:Calendar:Enabled", false), 409, "Calendar automation is disabled in appsettings.json.");
        var provider = config["Automation:Calendar:Provider"] ?? "google";
        Guard.Require(provider is "google" or "microsoft", 400, "Calendar automation provider must be google or microsoft.");
        var hours = config.GetValue("Automation:Calendar:LeadHours", 24);
        var days = config.GetValue("Automation:Calendar:LookAheadDays", 14);
        var match = Guard.Text(config["Automation:Calendar:TitleContains"] ?? "interview", "Calendar title filter", 100);
        Guard.Require(hours is >= 1 and <= 168 && days is >= 1 and <= 30, 400, "Calendar lead/lookahead limits are invalid.");
        var now = clock.GetUtcNow(); var source = "calendar:" + provider;
        var entries = await calendar.CalendarAsync(provider, now, now.AddDays(days), ct);
        var prior = await reader.ReadAsync(s => s.AgentTriggers.Values.Where(x => x.Source == source).ToList(), ct);
        var seen = new HashSet<string>(); var count = 0;
        foreach (var entry in entries.Where(x => !x.AllDay && x.Title.Contains(match, StringComparison.OrdinalIgnoreCase)))
        {
            Guard.Require(DateTimeOffset.TryParse(entry.Start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start), 502, "Calendar timestamp is invalid.");
            if (start <= now) continue;
            var occurrence = PrivacyData.Hash(entry.Id); seen.Add(occurrence);
            var old = prior.SingleOrDefault(x => x.OccurrenceId == occurrence);
            var due = start.AddHours(-hours); var consent = config.GetValue("Automation:Calendar:AllowCloudProcessing", false);
            var brief = "Prepare for this interview: " + entry.Title;
            if (brief.Length > 2000) brief = brief[..2000];
            if (old is not null && old.DueAt == due && old.ExpiresAt == start && old.Brief == brief && old.AllowCloudProcessing == consent && old.Status != "Cancelled") continue;
            await runtime.IngestTriggerAsync(new(source, occurrence, (old?.Revision ?? 0) + 1, "interview-preparation", due, start,
                brief, consent, occurrence), ct); count++;
        }
        // Missing events are cancelled only after a complete successful provider snapshot.
        foreach (var old in prior.Where(x => !seen.Contains(x.OccurrenceId) && x.Status is "Pending" or "Dispatched"))
        {
            await runtime.IngestTriggerAsync(new(source, old.OccurrenceId, old.Revision + 1, old.Workflow, old.DueAt, old.ExpiresAt,
                "", old.AllowCloudProcessing, old.CorrelationId, true), ct); count++;
        }
        return count;
    }
}

public sealed class CalendarTriggerWorker(CalendarTriggerSync sync, IPrivacyReader privacy, IConfiguration config, ILogger<CalendarTriggerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (config.GetValue("Automation:Calendar:Enabled", false))
                {
                    var status = await privacy.StatusAsync(stoppingToken);
                    if (!status.Erased) { using var scope = PrivacyContext.Enter(status.Epoch); await sync.SyncAsync(stoppingToken); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogWarning("Calendar automation sync failed ({Type}).", e.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
