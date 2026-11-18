using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Text;

namespace Jarvis.Tests;

public sealed class AgentToolGatewayTests
{
    private const string Capability = "fixture.action";
    private const string Schema = """{"type":"object","properties":{"target":{"type":"string","maxLength":20}},"required":["target"],"additionalProperties":false}""";
    private static readonly BudgetActor Owner = new("fixture-owner", [AgentToolGateway.ManageScope]);

    [Fact]
    public async Task Tool_outside_specialist_descriptor_is_refused_before_preparation()
    {
        var f = await Fixture.Create(allowed: false);
        using var origin = CoreInvocationContext.Enter(InvocationOrigin.Agent);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => f.Gateway.InvokeAsync(f.Call))).StatusCode);
        Assert.Equal(0, f.Handler.Prepared); Assert.Equal(0, f.Handler.Executed);
        Assert.Empty(await f.Store.ReadAsync(s => s.AgentToolInvocations));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"target\":\"one\",\"extra\":true}")]
    [InlineData("{\"target\":\"one\",\"target\":\"two\"}")]
    [InlineData("{\"target\":null}")]
    public async Task Invalid_parameters_are_refused_before_budget_or_execution(string args)
    {
        var f = await Fixture.Create(paid: true);
        using var origin = CoreInvocationContext.Enter(InvocationOrigin.Agent);
        Assert.Equal(400, (await Assert.ThrowsAsync<DomainException>(() => f.Gateway.InvokeAsync(f.Call with { ArgumentsJson = args }))).StatusCode);
        Assert.Equal(0, f.Handler.Prepared); Assert.Empty(await f.Store.ReadAsync(s => s.UsageReservations));
    }

    [Fact]
    public async Task Irreversible_action_needs_exact_owner_approval_and_cannot_replay()
    {
        var f = await Fixture.Create(irreversible: true);
        using (CoreInvocationContext.Enter(InvocationOrigin.Agent)) {
            Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Gateway.InvokeAsync(f.Call))).StatusCode);
        }
        Assert.Equal(0, f.Handler.Prepared); Assert.Equal(0, f.Handler.Executed);
        var pending = await f.Store.ReadAsync(s => s.AgentToolInvocations[f.Call.Id]);
        using (CoreInvocationContext.Enter(InvocationOrigin.ManualClient)) {
            await Assert.ThrowsAsync<DomainException>(() => f.Gateway.ConfirmAsync(f.Call.Id, new string('0', 64), Owner));
            Assert.True(await f.Gateway.ConfirmAsync(f.Call.Id, pending.ArgumentsHash, Owner));
        }
        using (CoreInvocationContext.Enter(InvocationOrigin.Agent)) {
            await Assert.ThrowsAsync<DomainException>(() => f.Gateway.InvokeAsync(f.Call with { ArgumentsJson = "{\"target\":\"other\"}" }));
            Assert.Equal(0, f.Handler.Executed);
            Assert.Equal("{\"done\":true}", await f.Gateway.InvokeAsync(f.Call));
            await Assert.ThrowsAsync<DomainException>(() => f.Gateway.InvokeAsync(f.Call));
        }
        Assert.Equal(1, f.Handler.Executed);
        Assert.Equal(AgentToolStatus.Succeeded, (await f.Store.ReadAsync(s => s.AgentToolInvocations[f.Call.Id])).Status);
    }

    [Fact]
    public async Task Reservation_exists_before_handler_executes_and_uses_existing_GBP_ledger()
    {
        var f = await Fixture.Create(paid: true);
        f.Handler.OnExecute = async () => {
            var charge = await f.Store.ReadAsync(s => s.UsageReservations[f.Call.Id]);
            Assert.Equal(f.Task.Id, charge.RunId); Assert.Equal(.01m, charge.ReservedUsd);
            Assert.NotNull(await f.Store.ReadAsync(s => s.GbpCommitments.GetValueOrDefault(f.Call.Id)));
            Assert.Equal(AgentToolStatus.Executing, (await f.Store.ReadAsync(s => s.AgentToolInvocations[f.Call.Id])).Status);
        };
        using var origin = CoreInvocationContext.Enter(InvocationOrigin.Agent);
        await f.Gateway.InvokeAsync(f.Call); Assert.Equal(1, f.Handler.Executed);
    }

    [Fact]
    public async Task Missing_budget_plan_and_oversized_reservation_never_execute()
    {
        var f = await Fixture.Create(paid: true); f.Handler.NoCharge = true;
        using var origin = CoreInvocationContext.Enter(InvocationOrigin.Agent);
        await Assert.ThrowsAsync<DomainException>(() => f.Gateway.InvokeAsync(f.Call)); Assert.Equal(0, f.Handler.Executed);
        f.Handler.NoCharge = false; f.Handler.Cost = 3;
        await Assert.ThrowsAsync<DomainException>(() => f.Gateway.InvokeAsync(f.Call with { Id = Guid.NewGuid() }));
        Assert.Equal(0, f.Handler.Executed); Assert.Empty(await f.Store.ReadAsync(s => s.UsageReservations));
    }

    [Fact]
    public async Task Unknown_outcome_keeps_reservation_and_blocks_automatic_replay()
    {
        var f = await Fixture.Create(paid: true); f.Handler.OnExecute = () => throw new IOException("Synthetic lost response");
        using var origin = CoreInvocationContext.Enter(InvocationOrigin.Agent);
        await Assert.ThrowsAsync<IOException>(() => f.Gateway.InvokeAsync(f.Call));
        Assert.Null((await f.Store.ReadAsync(s => s.UsageReservations[f.Call.Id])).ActualUsd);
        Assert.Equal(AgentToolStatus.Uncertain, (await f.Store.ReadAsync(s => s.AgentToolInvocations[f.Call.Id])).Status);
        await Assert.ThrowsAsync<DomainException>(() => f.Gateway.InvokeAsync(f.Call)); Assert.Equal(1, f.Handler.Executed);
    }

    [Fact]
    public async Task Revocation_during_preparation_is_rechecked_before_dispatch()
    {
        var f = await Fixture.Create(paid: true);
        f.Handler.OnPrepare = async () => {
            using var owner = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
            await f.Gateway.RevokeAsync(f.Call.GrantId, Owner);
        };
        using var origin = CoreInvocationContext.Enter(InvocationOrigin.Agent);
        await Assert.ThrowsAsync<DomainException>(() => f.Gateway.InvokeAsync(f.Call)); Assert.Equal(0, f.Handler.Executed);
    }

    [Fact]
    public async Task Changed_goal_revision_and_stale_task_claim_cannot_use_the_grant()
    {
        var f = await Fixture.Create(goal: true);
        await f.Store.UpdateAsync(s => { var goal = s.Goals.Values.Single(); s.Goals[goal.Id] = goal with { Revision = goal.Revision + 1 }; return true; });
        using var origin = CoreInvocationContext.Enter(InvocationOrigin.Agent);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => f.Gateway.InvokeAsync(f.Call))).StatusCode);
        await Assert.ThrowsAsync<DomainException>(() => f.Gateway.InvokeAsync(f.Call with { ClaimToken = Guid.NewGuid() }));
        Assert.Equal(0, f.Handler.Prepared);
    }

    [Fact]
    public async Task Cancellation_and_deletion_remove_authority_and_do_not_erase_money()
    {
        var f = await Fixture.Create(paid: true);
        using (CoreInvocationContext.Enter(InvocationOrigin.Agent)) await f.Gateway.InvokeAsync(f.Call);
        await f.Runtime.CancelAsync(f.Task.RunId, true, default);
        Assert.Empty(await f.Store.ReadAsync(s => s.AgentToolGrants)); Assert.Empty(await f.Store.ReadAsync(s => s.AgentToolInvocations));
        Assert.Single(await f.Store.ReadAsync(s => s.UsageReservations));
        using var origin = CoreInvocationContext.Enter(InvocationOrigin.Agent);
        await Assert.ThrowsAsync<DomainException>(() => f.Gateway.InvokeAsync(f.Call with { Id = Guid.NewGuid() })); Assert.Equal(1, f.Handler.Executed);
    }

    [Fact]
    public async Task Installed_registry_reuses_catalog_and_registers_voice_home_device_metadata_without_agent_privileges()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call."));
        var registry = host.Services.GetRequiredService<ICapabilityRegistry>();
        Assert.Same(host.Services.GetRequiredService<ICapabilityCatalog>(), registry);
        Assert.NotNull(registry.Get("core_home").InputSchemaJson);
        foreach (var device in BuiltInDeviceCapabilities.All) {
            Assert.Equal(device.ArgumentSchemaJson, registry.Get(device.Id).InputSchemaJson);
            Assert.Equal(device.Risk, registry.Get(device.Id).ActionRisk);
        }
        var specialists = host.Services.GetRequiredService<AgentSpecialistCatalog>();
        Assert.Equal(Enum.GetValues<AgentRole>().Length, specialists.Installed.Count);
        Assert.All(specialists.Installed, descriptor => Assert.DoesNotContain("core_home", descriptor.AllowedCapabilities));
        Assert.True(registry.Accepts("core_coaching_prompt", "1", """{"exercise_id":"point-first","story_id":null,"story_version":null,"claim_index":null}"""));
        Assert.False(registry.Accepts("core_coaching_prompt", "1", """{"exercise_id":"point-first","story_id":null,"story_version":null,"claim_index":null,"extra":1}"""));
    }

    [Fact]
    public async Task Agent_reasoning_cannot_bypass_gateway_and_prepared_dispatch_cannot_bypass_reservation()
    {
        var calls = 0; using var host = new ProviderFactory(_ => { calls++; throw new InvalidOperationException("No dispatch expected."); });
        var service = host.Services.GetRequiredService<TextReasoningService>();
        var task = new TextReasoningTask(Guid.NewGuid(), Guid.NewGuid(), "Chat", TextCapability.Conversation, ReasoningDataClass.Personal,
            true, "Fixture instructions", [new("user", "Hello")]);
        using (CoreInvocationContext.Enter(InvocationOrigin.Agent)) await Assert.ThrowsAsync<DomainException>(() => service.ReasonAsync(task, default));
        var prepared = await service.PrepareAsync(task, default);
        await Assert.ThrowsAsync<DomainException>(() => prepared.ExecuteAsync(default)); Assert.Equal(0, calls);
        // The application agent executor has no reasoning-provider, HTTP or handler dependency.
        var parameters = typeof(AgentExecutor).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Contains(typeof(AgentToolGateway), parameters);
        Assert.DoesNotContain(typeof(Jarvis.Orchestrator.Intelligence.ITextReasoningProvider), parameters);
        Assert.DoesNotContain(typeof(IAgentToolHandler), parameters); Assert.DoesNotContain(typeof(HttpClient), parameters);
        Assert.All(parameters, type => Assert.Contains(type, new[] { typeof(AgentToolGateway), typeof(IRuntimeStore), typeof(TimeProvider), typeof(IWorkflowCatalog), typeof(IDomainRegistry), typeof(AgentActionService) }));
        Assert.Null(typeof(AgentToolExecutionScope).GetMethod("Enter", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
    }

    [Theory]
    [InlineData("career-plan", "Agent", false)]
    [InlineData("research-review", "Research", true)]
    public async Task Real_agent_executor_enters_gateway_reserves_once_and_reuses_existing_cache(string workflow, string profile, bool research)
    {
        IRuntimeStore? store = null; var calls = 0;
        using var root = new ProviderFactory(async request => {
            calls++;
            var charge = Assert.Single(await store!.ReadAsync(s => s.UsageReservations.Values.ToArray()));
            var invocation = Assert.Single(await store.ReadAsync(s => s.AgentToolInvocations.Values.ToArray()));
            Assert.Equal(AgentToolStatus.Executing, invocation.Status); Assert.Equal(charge.Id, invocation.ReservationId);
            Assert.Null(charge.ActualUsd);
            var text = research ? "Public fixture source" : JsonSerializer.Serialize(new { schemaVersion = 1, summary = "Scoped fixture artifact", drills = new[] { "One", "Two", "Three" }, sourceIds = Array.Empty<Guid>() });
            var json = JsonSerializer.Serialize(new { status = "completed", usage = new { input_tokens = 10, output_tokens = 10 }, output = new[] {
                new { type = "message", content = new[] { new { type = "output_text", text, annotations = research
                    ? new[] { new { type = "url_citation", url = "https://example.org/fixture", title = "Fixture", start_index = 0, end_index = 6 } }
                    : [] } } }
            } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        using var host = root.WithWebHostBuilder(builder => {
            builder.UseSetting("Jarvis:WorkflowsEnabled", "false");
            builder.UseSetting($"AI:{profile}:Provider", "openai"); builder.UseSetting($"AI:{profile}:Model", "fixture-agent");
            builder.UseSetting($"AI:{profile}:RateCardModel", "fixture-agent"); builder.UseSetting($"AI:{profile}:InputUsdPerMillion", ".4");
            builder.UseSetting($"AI:{profile}:OutputUsdPerMillion", "1.6"); builder.UseSetting($"AI:{profile}:AllowedDataClasses:0", research ? "Public" : "Personal");
        });
        store = host.Services.GetRequiredService<IRuntimeStore>(); var runtime = host.Services.GetRequiredService<AgentRuntime>();
        await runtime.StartAsync(new(Guid.NewGuid(), workflow, true, "Fixture goal"), default); var task = (await runtime.ClaimAsync(default))!;
        var executor = host.Services.GetRequiredService<IAgentExecutor>();
        var first = await executor.ExecuteAsync(task, default); var replay = await executor.ExecuteAsync(task, default);
        Assert.Contains(research ? "provider-grounded-search" : "Scoped fixture artifact", first);
        Assert.Contains(research ? "provider-grounded-search" : "Scoped fixture artifact", replay);
        Assert.Equal(1, calls); Assert.Single(await store.ReadAsync(s => s.UsageReservations));
        Assert.Equal(AgentToolStatus.Succeeded, Assert.Single(await store.ReadAsync(s => s.AgentToolInvocations.Values.ToArray())).Status);
        Assert.Equal(ProviderAttemptOutcome.Cached, Assert.Single(await store.ReadAsync(s => s.ProviderAttempts.Values.ToArray())).Outcome);
    }

    [Fact]
    public async Task Agent_context_cannot_self_grant_or_approve_and_erasure_drops_operational_authority()
    {
        var f = await Fixture.Create();
        using (CoreInvocationContext.Enter(InvocationOrigin.Agent)) {
            await Assert.ThrowsAsync<DomainException>(() => f.Gateway.GrantAsync(Guid.NewGuid(), f.Task.RunId, Capability, 2, 4, true, Owner));
            await f.Gateway.InvokeAsync(f.Call);
        }
        var state = await f.Store.ReadAsync(s => s); var export = PrivacyData.Export(new(1, 1, false, null, null), state, [], [], [], [], DateTimeOffset.UtcNow);
        Assert.Single(export.AgentToolGrants!); Assert.Single(export.AgentToolInvocations!);
        Assert.DoesNotContain("\"target\"", JsonSerializer.Serialize(export.AgentToolInvocations));
        var erased = PrivacyData.EmptyAfterErase(state); Assert.Empty(erased.AgentToolGrants); Assert.Empty(erased.AgentToolInvocations);
    }

    [Fact]
    public async Task Direct_template_handler_cannot_execute_outside_the_gateway()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call."));
        var runtime = host.Services.GetRequiredService<AgentRuntime>(); await runtime.StartAsync(new(Guid.NewGuid(), "daily-coaching", false), default);
        var task = (await runtime.ClaimAsync(default))!;
        var run = await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.AgentRuns[task.RunId]);
        var handler = host.Services.GetServices<IAgentToolHandler>().Single(x => x.CapabilityId == AgentCapabilityIds.Template);
        var call = new AgentToolCall(Guid.NewGuid(), task.Id, task.ClaimToken!.Value, Guid.NewGuid(), AgentCapabilityIds.Template, "1",
            JsonSerializer.Serialize(new { artifact = "{}" }), ReasoningDataClass.Personal);
        var prepared = await handler.PrepareAsync(new(call, task, run, host.Services.GetRequiredService<AgentSpecialistCatalog>().Get(task.Role)), default);
        await Assert.ThrowsAsync<DomainException>(() => prepared.ExecuteAsync(default));
    }

    private sealed record Fixture(InMemoryRuntimeStore Store, AgentRuntime Runtime, AgentTask Task, AgentToolGateway Gateway, FakeHandler Handler, AgentToolCall Call)
    {
        public static async Task<Fixture> Create(bool allowed = true, bool irreversible = false, bool paid = false, bool goal = false)
        {
            var clock = new BudgetClock(); var store = new InMemoryRuntimeStore(); var runtime = new AgentRuntime(store, clock);
            var run = await runtime.StartAsync(new(Guid.NewGuid(), "daily-coaching", true), default); var task = (await runtime.ClaimAsync(default))!;
            if (goal) await store.UpdateAsync(s => { var id = Guid.NewGuid(); s.Goals[id] = new(id, 3, "Goal", "Brief", "daily-coaching", true,
                clock.Now, clock.Now.AddDays(1), GoalStatus.Active, clock.Now, clock.Now, task.RunId); return true; });
            var registry = new CapabilityCatalog([new(Capability, "Synthetic test handler", irreversible ? CapabilityRisk.ExternalAction : CapabilityRisk.ReadOnly,
                InputSchemaJson: Schema, DataClasses: [ReasoningDataClass.Public], RequiresOwnerApproval: irreversible, Irreversible: irreversible, RequiresBudget: paid)]);
            var specialists = new AgentSpecialistCatalog(new AgentSpecialistCatalog().Installed.Select(s => s with {
                AllowedCapabilities = allowed ? [..s.AllowedCapabilities, Capability] : s.AllowedCapabilities }));
            var handler = new FakeHandler(paid); var budget = new UsageBudget(store, clock, new TestBudgetSource(clock));
            var gateway = new AgentToolGateway(store, clock, registry, specialists, [handler], budget);
            AgentToolGrant grant;
            using (CoreInvocationContext.Enter(InvocationOrigin.ManualClient)) grant = await gateway.GrantAsync(Guid.NewGuid(), run.Run.Id, Capability, 2m, 10, !irreversible, Owner);
            var call = new AgentToolCall(Guid.NewGuid(), task.Id, task.ClaimToken!.Value, grant.Id, Capability, "1", "{\"target\":\"one\"}", ReasoningDataClass.Public);
            return new(store, runtime, task, gateway, handler, call);
        }
    }
    private sealed class FakeHandler(bool paid) : IAgentToolHandler
    {
        private bool Paid => paid;
        public string CapabilityId => Capability;
        public int Prepared, Executed; public bool NoCharge; public decimal Cost = .01m;
        public Func<Task>? OnPrepare, OnExecute;
        public async Task<IPreparedAgentTool> PrepareAsync(AgentToolContext context, CancellationToken ct) {
            Prepared++; if (OnPrepare is not null) await OnPrepare(); return new Execution(this, context);
        }
        private sealed class Execution(FakeHandler handler, AgentToolContext context) : IPreparedAgentTool {
            public AgentToolCharge? Charge => handler.Paid && !handler.NoCharge ? new(context.Call.Id, context.Task.Id, "openai", "fixture", handler.Cost, new(10, 10, 10), UsageLane.Background) : null;
            public Guid? CachedReservationId => null;
            public async Task<string> ExecuteAsync(CancellationToken ct) { handler.Executed++; if (handler.OnExecute is not null) await handler.OnExecute(); return "{\"done\":true}"; }
        }
    }
}

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Agent_tool_grants_and_audit_survive_restart_and_delete_with_the_run()
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new BudgetClock(); Guid runId;
        using (var first = new PostgresApiFactory(database.ConnectionString, clock)) {
            var runtime = first.Services.GetRequiredService<AgentRuntime>();
            var run = await runtime.StartAsync(new(Guid.NewGuid(), "daily-coaching", false), default); runId = run.Run.Id;
            var task = (await runtime.ClaimAsync(default))!;
            await first.Services.GetRequiredService<IAgentExecutor>().ExecuteAsync(task, default);
        }
        using var restarted = new PostgresApiFactory(database.ConnectionString, clock);
        var store = restarted.Services.GetRequiredService<IRuntimeStore>();
        var grant = Assert.Single(await store.ReadAsync(s => s.AgentToolGrants.Values.ToArray())); Assert.Equal(runId, grant.RunId);
        var audit = Assert.Single(await store.ReadAsync(s => s.AgentToolInvocations.Values.ToArray())); Assert.Equal(AgentToolStatus.Succeeded, audit.Status);
        Assert.Null(audit.ReservationId); Assert.NotNull(audit.OutputHash);
        await restarted.Services.GetRequiredService<AgentRuntime>().CancelAsync(runId, true, default);
        Assert.Empty(await store.ReadAsync(s => s.AgentToolGrants)); Assert.Empty(await store.ReadAsync(s => s.AgentToolInvocations));
    }
}
