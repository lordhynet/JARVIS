using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Agents;
using Jarvis.Orchestrator.Cqrs.Devices;
using Jarvis.Orchestrator.Cqrs.Learning;
using Jarvis.Orchestrator.Cqrs.Sessions;
using Jarvis.Orchestrator.Cqrs.Tools;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class CqrsArchitectureTests
{
    private static bool Implements(Type type, Type definition) => type.GetInterfaces()
        .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == definition);

    [Fact]
    public void Every_request_has_a_registered_handler_and_reads_and_writes_are_separate()
    {
        using var factory = new ScenarioFactory();
        var types = typeof(ICommand<>).Assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract).ToList();
        var requests = types.Where(t => Implements(t, typeof(ICommand<>)) || Implements(t, typeof(IQuery<>))).ToList();
        Assert.NotEmpty(requests);
        foreach (var request in requests)
        {
            var command = Implements(request, typeof(ICommand<>));
            Assert.NotEqual(command, Implements(request, typeof(IQuery<>)));
            var requestContract = Assert.Single(request.GetInterfaces(), i => i.IsGenericType &&
                i.GetGenericTypeDefinition() == (command ? typeof(ICommand<>) : typeof(IQuery<>)));
            var handlerContract = (command ? typeof(ICommandHandler<,>) : typeof(IQueryHandler<,>))
                .MakeGenericType(request, requestContract.GetGenericArguments()[0]);
            var handler = factory.Services.GetRequiredService(handlerContract);
            Assert.False(Implements(handler.GetType(), command ? typeof(IQueryHandler<,>) : typeof(ICommandHandler<,>)));
        }
    }

    [Fact]
    public void Query_handlers_do_not_depend_on_write_stores_or_business_command_services()
    {
        var handlers = typeof(IQuery<>).Assembly.GetTypes().Where(t => t.IsClass && Implements(t, typeof(IQueryHandler<,>))).ToList();
        Assert.NotEmpty(handlers);
        var forbidden = new[] { typeof(ISessionStore), typeof(IRuntimeStore), typeof(SessionService), typeof(DeviceService),
            typeof(LearningService), typeof(ToolService), typeof(SessionLifecycleService), typeof(ConversationService), typeof(IHomeControl),
            typeof(IWorkflowStore), typeof(WorkflowService), typeof(Jarvis.Orchestrator.Training.TrainingService),
            typeof(Jarvis.Orchestrator.Coaching.CoachingService), typeof(Jarvis.Orchestrator.Coaching.ProgrammeService) };
        foreach (var handler in handlers)
            foreach (var dependency in handler.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
            {
                Assert.DoesNotContain(dependency, forbidden);
                Assert.False(dependency.IsGenericType && dependency.GetGenericTypeDefinition() == typeof(ICommandHandler<,>));
            }
        Assert.DoesNotContain(typeof(IQuery<>).Assembly.GetReferencedAssemblies(), a =>
            a.Name!.Contains("EntityFrameworkCore", StringComparison.Ordinal) || a.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Commands_are_immediately_visible_to_queries_and_query_reads_do_not_mutate_state()
    {
        using var factory = new ScenarioFactory(); var services = factory.Services;
        Assert.Same(services.GetRequiredService<ISessionStore>(), services.GetRequiredService<ISessionReader>());
        Assert.Same(services.GetRequiredService<IRuntimeStore>(), services.GetRequiredService<IRuntimeReader>());
        var session = await services.GetRequiredService<ICommandHandler<CreateSession, ConversationSession>>()
            .HandleAsync(new CreateSession(new(ActivityType.French, "CQRS read after write", null)));
        var result = await services.GetRequiredService<IQueryHandler<GetSession, ConversationSession?>>()
            .HandleAsync(new GetSession(session.Id));
        Assert.Equal(session, result);
        await services.GetRequiredService<ICommandHandler<CreateLearningItem, LearningItem>>()
            .HandleAsync(new CreateLearningItem(new(LearningDomain.French, "Hello", "Bonjour")));
        var reader = services.GetRequiredService<IRuntimeReader>();
        var before = await reader.ReadAsync(s => JsonSerializer.Serialize(s));
        await services.GetRequiredService<IQueryHandler<ListDevices, IReadOnlyList<DeviceView>>>().HandleAsync(new ListDevices());
        await services.GetRequiredService<IQueryHandler<ListLearningItems, IReadOnlyList<LearningItem>>>().HandleAsync(new ListLearningItems(true));
        await services.GetRequiredService<IQueryHandler<GetReviewHistory, IReadOnlyList<ReviewEvent>>>().HandleAsync(new GetReviewHistory(Guid.NewGuid()));
        await services.GetRequiredService<IQueryHandler<ListToolActions, IReadOnlyList<AuditedTool>>>().HandleAsync(new ListToolActions());
        Assert.Equal(before, await reader.ReadAsync(s => JsonSerializer.Serialize(s)));
    }

    [Fact]
    public async Task Session_read_endpoint_dispatches_the_query_handler()
    {
        var expected = new ConversationSession(Guid.NewGuid(), ActivityType.General, "From query handler", SessionStatus.Active,
            null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        using var original = new ScenarioFactory();
        using var factory = original.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddSingleton<IQueryHandler<ListSessions, IReadOnlyList<ConversationSession>>>(new StubSessionQuery(expected))));
        using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        var sessions = await client.GetFromJsonAsync<List<ConversationSession>>("/api/sessions", LifeScenarioTests.Json);
        Assert.Equal(expected.Id, Assert.Single(sessions!).Id);
        Assert.Empty(await factory.Services.GetRequiredService<ISessionStore>().ListAsync());
    }

    [Fact]
    public async Task Agent_goal_write_endpoint_dispatches_the_command_handler()
    {
        var expected = new PersonalGoal(Guid.NewGuid(), 1, "Fixture", "Handler-owned", "practice-reminder", false,
            DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(2), "Active", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        using var original = new ScenarioFactory();
        using var factory = original.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddSingleton<ICommandHandler<SaveAgentGoal, PersonalGoal>>(new StubSaveAgentGoal(expected))));
        using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        var request = new SaveGoalRequest(Guid.NewGuid(), null, "Fixture", "Handler-owned", "practice-reminder", false,
            DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(2));
        using var response = await client.PostAsJsonAsync("/api/goals", request, LifeScenarioTests.Json);
        response.EnsureSuccessStatusCode();
        Assert.Equal(expected.Id, (await response.Content.ReadFromJsonAsync<PersonalGoal>(LifeScenarioTests.Json))!.Id);
        Assert.Empty(await factory.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.Goals));
    }

    private sealed class StubSessionQuery(ConversationSession session) : IQueryHandler<ListSessions, IReadOnlyList<ConversationSession>>
    {
        public Task<IReadOnlyList<ConversationSession>> HandleAsync(ListSessions query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConversationSession>>([session]);
    }

    private sealed class StubSaveAgentGoal(PersonalGoal goal) : ICommandHandler<SaveAgentGoal, PersonalGoal>
    {
        public Task<PersonalGoal> HandleAsync(SaveAgentGoal command, CancellationToken ct = default) => Task.FromResult(goal);
    }
}
