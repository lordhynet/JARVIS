using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Devices;
using Jarvis.Orchestrator.Cqrs.Integrations;
using Jarvis.Orchestrator.Cqrs.Learning;
using Jarvis.Orchestrator.Cqrs.Sessions;
using Jarvis.Orchestrator.Cqrs.Tools;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Api;

public static class CqrsRegistration
{
    public static IServiceCollection AddJarvisCqrs(this IServiceCollection services)
    {
        // Both ports resolve to the same backing store: no duplicated in-memory state.
        services.AddSingleton<ISessionReader>(s => s.GetRequiredService<ISessionStore>());
        services.AddSingleton<IRuntimeReader>(s => s.GetRequiredService<IRuntimeStore>());
        services.AddSingleton<SessionCommandHandlers>();
        services.AddSingleton<SessionQueryHandlers>();
        services.AddSingleton<DeviceCommandHandlers>();
        services.AddSingleton<DeviceQueryHandler>();
        services.AddSingleton<LearningCommandHandlers>();
        services.AddSingleton<LearningQueryHandlers>();
        services.AddSingleton<ToolCommandHandlers>();
        services.AddSingleton<ToolQueryHandler>();
        services.AddSingleton<IntegrationQueryHandlers>();
        services.AddSingleton<DailyBriefingQueryHandler>();
        services.AddSingleton<StorageReadinessQueryHandler>();
        services.AddSingleton<TranscribeAudioHandler>();

        Command<CreateSession, ConversationSession, SessionCommandHandlers>(services);
        Command<AddCheckpoint, ConversationSession?, SessionCommandHandlers>(services);
        Command<TransitionSession, ConversationSession, SessionCommandHandlers>(services);
        Command<ClaimSession, ConversationSession, SessionCommandHandlers>(services);
        Command<StartHandoff, ConversationSession, SessionCommandHandlers>(services);
        Command<AdvanceHandoff, ConversationSession, SessionCommandHandlers>(services);
        Command<SendMessage, ConversationSession, SessionCommandHandlers>(services);
        Command<ExpireHandoffs, CommandCompleted, SessionCommandHandlers>(services);
        Command<RegisterDevice, RegisteredDevice, DeviceCommandHandlers>(services);
        Command<HeartbeatDevice, RegisteredDevice, DeviceCommandHandlers>(services);
        Command<VerifyDevice, RegisteredDevice, DeviceCommandHandlers>(services);
        Command<CreateLearningItem, LearningItem, LearningCommandHandlers>(services);
        Command<ReviewLearningItem, LearningItem, LearningCommandHandlers>(services);
        Command<RequestHomeAction, AuditedTool, ToolCommandHandlers>(services);
        Command<ConfirmToolAction, AuditedTool, ToolCommandHandlers>(services);
        Command<RecoverInterruptedTools, int, ToolCommandHandlers>(services);
        Command<TranscribeAudio, string, TranscribeAudioHandler>(services);

        Query<GetSession, ConversationSession?, SessionQueryHandlers>(services);
        Query<ListSessions, IReadOnlyList<ConversationSession>, SessionQueryHandlers>(services);
        Query<ListDevices, IReadOnlyList<DeviceView>, DeviceQueryHandler>(services);
        Query<ListLearningItems, IReadOnlyList<LearningItem>, LearningQueryHandlers>(services);
        Query<GetReviewHistory, IReadOnlyList<ReviewEvent>, LearningQueryHandlers>(services);
        Query<ListToolActions, IReadOnlyList<AuditedTool>, ToolQueryHandler>(services);
        Query<ListIntegrations, IReadOnlyList<IntegrationStatus>, IntegrationQueryHandlers>(services);
        Query<GetTransit, IReadOnlyList<TransitLine>, IntegrationQueryHandlers>(services);
        Query<GetCalendar, IReadOnlyList<CalendarEntry>, IntegrationQueryHandlers>(services);
        Query<GetDailyBriefing, DailyBriefing, DailyBriefingQueryHandler>(services);
        Query<GetStorageReadiness, StorageReadiness, StorageReadinessQueryHandler>(services);
        return services;
    }

    private static void Command<TCommand, TResult, THandler>(IServiceCollection services)
        where TCommand : ICommand<TResult> where THandler : class, ICommandHandler<TCommand, TResult> =>
        services.AddSingleton<ICommandHandler<TCommand, TResult>>(s => s.GetRequiredService<THandler>());

    private static void Query<TQuery, TResult, THandler>(IServiceCollection services)
        where TQuery : IQuery<TResult> where THandler : class, IQueryHandler<TQuery, TResult> =>
        services.AddSingleton<IQueryHandler<TQuery, TResult>>(s => s.GetRequiredService<THandler>());
}
