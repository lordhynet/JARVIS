using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Privacy;

public sealed record GetPrivacyStatus : IQuery<PrivacyStatus>;
public sealed record ExportCore(DateTimeOffset At) : IQuery<CoreDataExport>;
public sealed record PreviewRetention(RetentionPolicy Policy, DateTimeOffset At) : IQuery<RetentionPreview>;
public sealed record ListPrivacyJobs : IQuery<IReadOnlyList<PrivacyJob>>;
public sealed record EraseCore(EraseCoreRequest Request, string KeyFingerprint, DateTimeOffset At) : ICommand<PrivacyStatus>;
public sealed record ReopenCore(ReopenCoreRequest Request, string KeyFingerprint) : ICommand<PrivacyStatus>;
public sealed record ApplyRetention(ApplyRetentionRequest Request, RetentionPolicy Policy, DateTimeOffset At) : ICommand<PrivacyJob>;
public sealed class PrivacyQueries(IPrivacyReader reader) : IQueryHandler<GetPrivacyStatus, PrivacyStatus>, IQueryHandler<ExportCore, CoreDataExport>,
    IQueryHandler<PreviewRetention, RetentionPreview>, IQueryHandler<ListPrivacyJobs, IReadOnlyList<PrivacyJob>>
{
    public Task<PrivacyStatus> HandleAsync(GetPrivacyStatus q, CancellationToken ct = default) => reader.StatusAsync(ct);
    public Task<CoreDataExport> HandleAsync(ExportCore q, CancellationToken ct = default) => reader.ExportAsync(q.At, ct);
    public Task<RetentionPreview> HandleAsync(PreviewRetention q, CancellationToken ct = default) => reader.PreviewAsync(q.Policy, q.At, ct);
    public Task<IReadOnlyList<PrivacyJob>> HandleAsync(ListPrivacyJobs q, CancellationToken ct = default) => reader.JobsAsync(ct);
}
public sealed class PrivacyCommands(IPrivacyStore store) : ICommandHandler<EraseCore, PrivacyStatus>, ICommandHandler<ReopenCore, PrivacyStatus>, ICommandHandler<ApplyRetention, PrivacyJob>
{
    public Task<PrivacyStatus> HandleAsync(EraseCore c, CancellationToken ct = default) => store.EraseAsync(c.Request, c.KeyFingerprint, c.At, ct);
    public Task<PrivacyStatus> HandleAsync(ReopenCore c, CancellationToken ct = default) => store.ReopenAsync(c.Request, c.KeyFingerprint, ct);
    public Task<PrivacyJob> HandleAsync(ApplyRetention c, CancellationToken ct = default) => store.RetainAsync(c.Request, c.Policy, c.At, ct);
}
