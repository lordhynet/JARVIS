using System.Globalization;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Forge;

public interface IForgeAvailability { bool Enabled { get; } }
public interface IForgeProgrammeReader
{
    Task<ForgeProgramme?> GetAsync(string ownerScope, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ForgeProgramme>> ListAsync(string ownerScope, CancellationToken ct = default);
}

public static class ForgeProgrammePolicy
{
    // Current JARVIS is a personal Core. This is server-owned scope, never a request-body owner ID.
    public const string OwnerScope = "personal-owner";
    // PostgreSQL timestamps have microsecond precision. Return exactly what will survive restart.
    public static DateTimeOffset Timestamp(DateTimeOffset now) => new(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
    public static void RequireEnabled(IForgeAvailability feature) => Guard.Require(feature.Enabled, 404, "Forge is not available.");
    public static ForgeProgrammeView View(ForgeProgramme programme, DateTimeOffset now) =>
        new(programme, ForgeCalendar.Evaluate(now, TimeZoneInfo.FindSystemTimeZoneById(programme.TimeZone),
            programme.Start, programme.Graduation, ForgeCalendar.DefaultPhases));
}

public sealed class ForgeProgrammeService(ICoreTransaction transaction, IForgeAvailability feature, TimeProvider clock)
{
    public Task<ForgeProgramme> CreateAsync(CreateForgeProgrammeRequest request, CancellationToken ct = default)
    {
        ForgeProgrammePolicy.RequireEnabled(feature);
        Guard.Require(request.RequestId != Guid.Empty, 400, "RequestId is required.");
        Guard.Require(DateOnly.TryParseExact(request.LocalStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) &&
            DateOnly.TryParseExact(request.LocalGraduationDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _), 400, "Use ISO programme dates.");
        var graduation = DateOnly.ParseExact(request.LocalGraduationDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return transaction.ExecuteAsync(async writes => {
            ForgeProgrammePolicy.RequireEnabled(feature);
            var previous = await writes.ReadForgeProgrammeAsync(ForgeProgrammePolicy.OwnerScope, request.RequestId);
            if (previous is not null) {
                Guard.Require(previous.DeletedAt is null && previous.Start == start && previous.Graduation == graduation && previous.TimeZone == request.TimeZone,
                    409, "Programme request ID belongs to different or deleted configuration.");
                return previous;
            }
            Guard.Require(start == ForgeCalendar.DefaultStart && graduation == ForgeCalendar.DefaultGraduation && request.TimeZone == ForgeCalendar.DefaultTimeZone,
                400, "This configuration supports Forge from 1 December 2026 to 18 August 2027 in Europe/London.");
            Guard.Require(await writes.CountForgeProgrammesAsync(ForgeProgrammePolicy.OwnerScope) < 10, 409, "Ten active Forge programmes already exist.");
            var now = ForgeProgrammePolicy.Timestamp(clock.GetUtcNow());
            var programme = new ForgeProgramme(request.RequestId, ForgeProgrammePolicy.OwnerScope, 1, 1, start, graduation,
                request.TimeZone, false, now, now);
            await writes.SaveForgeProgrammeAsync(programme);
            return programme;
        }, ct);
    }

    public Task<ForgeProgramme> PauseAsync(Guid id, PauseForgeProgrammeRequest request, CancellationToken ct = default)
    {
        ForgeProgrammePolicy.RequireEnabled(feature);
        return transaction.ExecuteAsync(async writes => {
            ForgeProgrammePolicy.RequireEnabled(feature);
            var current = await writes.ReadForgeProgrammeAsync(ForgeProgrammePolicy.OwnerScope, id);
            Guard.Require(current is { DeletedAt: null }, 404, "Forge programme not found.");
            Guard.Require(request.ExpectedRevision > 0 && current.Revision == request.ExpectedRevision, 409, "Programme changed. Reload before editing.");
            if (current.Paused == request.Paused) return current;
            var updated = current with { Paused = request.Paused, Revision = checked(current.Revision + 1), UpdatedAt = ForgeProgrammePolicy.Timestamp(clock.GetUtcNow()) };
            await writes.SaveForgeProgrammeAsync(updated);
            return updated;
        }, ct);
    }

    public Task<bool> DeleteAsync(Guid id, int revision, CancellationToken ct = default)
    {
        ForgeProgrammePolicy.RequireEnabled(feature);
        return transaction.ExecuteAsync(async writes => {
            ForgeProgrammePolicy.RequireEnabled(feature);
            var current = await writes.ReadForgeProgrammeAsync(ForgeProgrammePolicy.OwnerScope, id);
            if (current is null || current.DeletedAt is not null) return false;
            Guard.Require(revision > 0 && current.Revision == revision, 409, "Programme changed. Reload before deleting.");
            var now = ForgeProgrammePolicy.Timestamp(clock.GetUtcNow());
            await writes.SaveForgeProgrammeAsync(current with { DeletedAt = now, UpdatedAt = now, Paused = true, Revision = checked(current.Revision + 1) });
            return true;
        }, ct);
    }
}

public sealed class MemoryForgeProgrammeReader(MemoryPrivacyBoundary boundary) : IForgeProgrammeReader
{
    public Task<ForgeProgramme?> GetAsync(string ownerScope, Guid id, CancellationToken ct = default) => boundary.Run(() => {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(boundary.ForgeProgrammes.GetValueOrDefault((ownerScope, id)));
    });
    public Task<IReadOnlyList<ForgeProgramme>> ListAsync(string ownerScope, CancellationToken ct = default) => boundary.Run(() => {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ForgeProgramme>>(boundary.ForgeProgrammes.Values.Where(x => x.OwnerScope == ownerScope && x.DeletedAt is null)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).ToArray());
    });
}
