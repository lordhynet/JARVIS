namespace Jarvis.Orchestrator.Cqrs;

/// <summary>A use case that changes state or performs an external operation.</summary>
public interface ICommand<TResult>;

/// <summary>A read-only use case; handling it must not persist changes.</summary>
public interface IQuery<TResult>;

public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken ct = default);
}

public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken ct = default);
}

public readonly record struct CommandCompleted;
