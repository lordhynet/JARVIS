using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Devices;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jarvis.Tests;

public sealed class DeviceCommandTests
{
    internal const string Arguments = """{ "entityId": "light.kitchen", "action": "turn_on" }""";
    private const string DeviceId = "command-fixture";
    private const string Proposal = "/api/devices/" + DeviceId + "/commands";
    private static readonly BudgetActor Owner = new("fixture-owner", [DeviceCommandService.ConfirmScope]);

    [Fact]
    public async Task Device_command_routes_require_owner_access_and_confirmation_binds_exact_arguments()
    {
        using var host = new JarvisApiFactory();
        using var owner = host.CreateAuthorisedClient();
        var (grantId, _) = await SetupAsync(host.Services, DeviceId);
        var (_, otherSecret) = await SetupAsync(host.Services, "other-command-fixture");
        using var otherDevice = host.CreateClient();
        otherDevice.DefaultRequestHeaders.Add("X-Jarvis-Key", otherSecret);
        TestOwnerManagement.Authorize(otherDevice);
        using var ordinaryOwner = host.CreateClient();
        ordinaryOwner.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        var request = Request(grantId, DateTimeOffset.UtcNow);
        var proposal = await PostAsync<DeviceCommandView>(ordinaryOwner, Proposal, request);
        Assert.Equal(request.RequestId, proposal.Id);
        Assert.Equal(Arguments, proposal.ArgumentsJson);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Arguments))), proposal.ArgumentHash);
        Assert.Equal(DeviceCommandStatus.AwaitingConfirmation, proposal.Status);
        Assert.Equal(nameof(InvocationOrigin.ManualClient), proposal.Origin);
        var path = $"/api/device-commands/{proposal.Id}";
        var confirm = path + "/confirm";
        var decision = new ConfirmDeviceCommandRequest(proposal.ArgumentHash, true);

        using var forbiddenRead = await otherDevice.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRead.StatusCode);
        await ExpectAsync(otherDevice, Proposal, request, HttpStatusCode.Forbidden);
        await ExpectAsync(otherDevice, confirm, decision, HttpStatusCode.Forbidden);
        await ExpectAsync(ordinaryOwner, confirm, decision, HttpStatusCode.Forbidden);
        await ExpectAsync(owner, confirm, new { proposal.ArgumentHash }, HttpStatusCode.BadRequest);
        await ExpectAsync(owner, confirm, decision with { ArgumentHash = PrivacyData.Hash(Arguments.Trim() + " ") }, HttpStatusCode.Conflict);
        var approved = await PostAsync<DeviceCommandView>(owner, confirm, decision);
        Assert.Equal(DeviceCommandStatus.Pending, approved.Status);
        Assert.Equal("fixture-owner", approved.ConfirmedBy);
        Assert.NotNull(approved.ConfirmedAt);
        Assert.Equal(approved, await PostAsync<DeviceCommandView>(owner, confirm, decision));
        Assert.Equal(approved, await PostAsync<DeviceCommandView>(owner, Proposal, request));
        await ExpectAsync(owner, Proposal, request with { ArgumentsJson = Arguments + " " }, HttpStatusCode.Conflict);
        var json = await owner.GetStringAsync(path);
        Assert.DoesNotContain("credentialHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(approved, System.Text.Json.JsonSerializer.Deserialize<DeviceCommandView>(json, LifeScenarioTests.Json));

        var cancellation = decision with { Approve = false };
        var cancelled = await PostAsync<DeviceCommandView>(owner, confirm, cancellation);
        Assert.Equal(DeviceCommandStatus.Cancelled, cancelled.Status);
        Assert.Equal("fixture-owner", cancelled.CancelledBy);
        Assert.NotNull(cancelled.CancelledAt);
        Assert.Equal(approved.ConfirmedAt, cancelled.ConfirmedAt);
        Assert.Equal(cancelled, await PostAsync<DeviceCommandView>(owner, confirm, cancellation));
        await ExpectAsync(owner, confirm, decision, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Device_command_rejects_missing_expired_revoked_and_wrong_device_grants_and_outside_arguments()
    {
        using var host = new JarvisApiFactory();
        using var owner = host.CreateAuthorisedClient();
        var (grantId, secret) = await SetupAsync(host.Services, DeviceId);
        var (otherGrant, _) = await SetupAsync(host.Services, "other-device");
        var now = DateTimeOffset.UtcNow;
        var request = Request(grantId, now);
        await ExpectAsync(owner, Proposal, request with { GrantId = Guid.NewGuid() }, HttpStatusCode.Forbidden);
        await ExpectAsync(owner, Proposal, request with { GrantId = otherGrant }, HttpStatusCode.Forbidden);
        await ExpectAsync(owner, Proposal, request with { ArgumentsJson = Arguments.Replace("light.kitchen", "light.other") }, HttpStatusCode.Forbidden);
        await ExpectAsync(owner, Proposal, request with { ArgumentsJson = "{\"entityId\":\"light.kitchen\",\"entityId\":\"light.other\"}" }, HttpStatusCode.BadRequest);
        var grants = host.Services.GetRequiredService<IDeviceGrantStore>();
        var original = (await grants.GetAsync(grantId))!;
        var expired = await grants.AddAsync(original with { Id = Guid.NewGuid(), GrantedAt = now.AddDays(-1), ExpiresAt = now.AddMinutes(-1) });
        var unaudited = await grants.AddAsync(original with { Id = Guid.NewGuid(), GrantedBy = null, Reason = null });
        var brief = await grants.AddAsync(original with { Id = Guid.NewGuid(), ExpiresAt = now.AddSeconds(30) });
        Assert.Equal(PrivacyData.Hash(secret), expired.CredentialHash);
        foreach (var grant in new[] { expired, unaudited, brief })
            await ExpectAsync(owner, Proposal, request with { GrantId = grant.Id }, HttpStatusCode.Forbidden);
        await ExpectAsync(owner, Proposal, request with { ExpiresAt = now.AddMinutes(6) }, HttpStatusCode.BadRequest);

        var first = await PostAsync<DeviceCommandView>(owner, Proposal, request);
        Assert.True(await grants.RevokeAsync(grantId, now));
        await ExpectAsync(owner, $"/api/device-commands/{first.Id}/confirm", new ConfirmDeviceCommandRequest(first.ArgumentHash, true), HttpStatusCode.Forbidden);
        await ExpectAsync(owner, Proposal, request with { RequestId = Guid.NewGuid() }, HttpStatusCode.Forbidden);
        Assert.Equal(first, await PostAsync<DeviceCommandView>(owner, Proposal, request));

        var replacement = await GrantAsync(host.Services, DeviceId);
        var second = await PostAsync<DeviceCommandView>(owner, Proposal, Request(replacement, now));
        await host.Services.GetRequiredService<DeviceCredentials>().IssueAsync(DeviceId, default);
        await ExpectAsync(owner, $"/api/device-commands/{second.Id}/confirm", new ConfirmDeviceCommandRequest(second.ArgumentHash, true), HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(InvocationOrigin.Agent)]
    [InlineData(InvocationOrigin.Voice)]
    [InlineData(InvocationOrigin.ExternalContent)]
    [InlineData(InvocationOrigin.Unknown)]
    public async Task Device_command_nonmanual_origins_can_propose_and_only_manual_owner_can_decide(InvocationOrigin origin)
    {
        using var host = new JarvisApiFactory();
        var (grantId, _) = await SetupAsync(host.Services, DeviceId);
        var descriptor = Assert.Single(BuiltInDeviceCapabilities.All, x => x.Id == BuiltInDeviceCapabilities.HomeAssistantTurn) with
        {
            Risk = DeviceCapabilityRisk.ReadOnly, ConfirmationPolicy = DeviceConfirmationPolicy.None
        };
        var service = new DeviceCommandService(new DeviceCapabilityCatalog([descriptor]),
            host.Services.GetRequiredService<ICoreTransaction>(), TimeProvider.System);
        var request = Request(grantId, DateTimeOffset.UtcNow);
        DeviceCommandView proposal;
        using (CoreInvocationContext.Enter(origin))
        {
            proposal = await service.ProposeAsync(DeviceId, request);
            Assert.Equal(origin.ToString(), proposal.Origin);
            Assert.Equal(DeviceCommandStatus.AwaitingConfirmation, proposal.Status);
            var error = await Assert.ThrowsAsync<DomainException>(() => service.ConfirmAsync(proposal.Id, new(proposal.ArgumentHash, true), Owner));
            Assert.Equal(403, error.StatusCode);
        }
        using (CoreInvocationContext.Enter(InvocationOrigin.ManualClient))
        {
            var replay = await Assert.ThrowsAsync<DomainException>(() => service.ProposeAsync(DeviceId, request));
            Assert.Equal(409, replay.StatusCode);
            var error = await Assert.ThrowsAsync<DomainException>(() => service.ConfirmAsync(proposal.Id, new(proposal.ArgumentHash, true), Owner with { Scopes = [] }));
            Assert.Equal(403, error.StatusCode);
            Assert.Equal(DeviceCommandStatus.Pending, (await service.ConfirmAsync(proposal.Id, new(proposal.ArgumentHash, true), Owner)).Status);
            var manual = await service.ProposeAsync(DeviceId, request with { RequestId = Guid.NewGuid() });
            Assert.Equal(DeviceCommandStatus.Pending, manual.Status);
            Assert.Null(manual.ConfirmedAt);
        }
    }

    [Theory]
    [InlineData(InvocationOrigin.Agent, DeviceCommandStatus.AwaitingConfirmation)]
    [InlineData(InvocationOrigin.Voice, DeviceCommandStatus.Pending)]
    public async Task Pre_approved_reversible_grant_skips_confirmation_only_for_voice(
        InvocationOrigin origin, DeviceCommandStatus expectedStatus)
    {
        using var host = new JarvisApiFactory();
        var (grantId, _) = await SetupAsync(host.Services, DeviceId, preApproved: true);
        var service = host.Services.GetRequiredService<DeviceCommandService>();
        var request = Request(grantId, DateTimeOffset.UtcNow);

        using var invocation = CoreInvocationContext.Enter(origin);
        var proposal = await service.ProposeAsync(DeviceId, request);

        Assert.Equal(expectedStatus, proposal.Status);
        Assert.Null(proposal.ConfirmedAt);
    }

    [Fact]
    public async Task Device_command_expires_without_reopening_and_retention_removes_aged_arguments()
    {
        var clock = new ScenarioClock();
        using var baseHost = new JarvisApiFactory();
        using var host = baseHost.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); }));
        var (grantId, _) = await SetupAsync(host.Services, DeviceId);
        var service = host.Services.GetRequiredService<DeviceCommandService>();
        var request = Request(grantId, clock.GetUtcNow());
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var proposal = await service.ProposeAsync(DeviceId, request);
        clock.Advance(TimeSpan.FromMinutes(3));
        var query = host.Services.GetRequiredService<IQueryHandler<GetDeviceCommand, DeviceCommandView>>();
        Assert.Equal(DeviceCommandStatus.Expired, (await query.HandleAsync(new(proposal.Id))).Status);
        Assert.Equal(DeviceCommandStatus.Expired, (await service.ConfirmAsync(proposal.Id, new(proposal.ArgumentHash, true), Owner)).Status);
        Assert.Equal(DeviceCommandStatus.Expired, (await service.ProposeAsync(DeviceId, request)).Status);

        var privacy = host.Services.GetRequiredService<IPrivacyStore>();
        var policy = new RetentionPolicy(ArtifactDays: 1);
        Assert.Equal(0, (await privacy.PreviewAsync(policy, clock.GetUtcNow())).Eligible.DeviceCommands);
        clock.Advance(TimeSpan.FromDays(2));
        var asOf = clock.GetUtcNow();
        var preview = await privacy.PreviewAsync(policy, asOf);
        Assert.Equal(1, preview.Eligible.DeviceCommands);
        var job = await privacy.RetainAsync(new(Guid.NewGuid(), asOf, preview.PolicyFingerprint, PrivacyData.RetentionPhrase), policy, asOf);
        Assert.Equal(1, job.Removed.DeviceCommands);
        Assert.Null(await host.Services.GetRequiredService<IDeviceCommandReader>().GetAsync(proposal.Id));
    }

    internal static ProposeDeviceCommandRequest Request(Guid grantId, DateTimeOffset now) =>
        new(Guid.NewGuid(), grantId, Arguments, now.AddMinutes(2));

    internal static async Task<(Guid GrantId, string Secret)> SetupAsync(
        IServiceProvider services, string deviceId, bool preApproved = false)
    {
        await services.GetRequiredService<DeviceService>().RegisterAsync(new(deviceId, "Command fixture", DeviceKind.AndroidCompanion,
            new(true, true, false, false, [])));
        var credential = await services.GetRequiredService<DeviceCredentials>().IssueAsync(deviceId, default);
        return (await GrantAsync(services, deviceId, preApproved), credential.Secret);
    }

    private static async Task<Guid> GrantAsync(IServiceProvider services, string deviceId, bool preApproved = false)
    {
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var grant = await services.GetRequiredService<DeviceGrantService>().GrantAsync(deviceId,
            DeviceGrantTests.GrantRequest() with { ExpiresAt = now.AddDays(4), PreApproved = preApproved },
            new("fixture-owner", [DeviceGrantService.ManageScope]));
        return grant.Id;
    }

    private static async Task ExpectAsync(HttpClient client, string path, object body, HttpStatusCode expected)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        Assert.Equal(expected, response.StatusCode);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(LifeScenarioTests.Json))!;
    }
}
