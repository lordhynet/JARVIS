using System.Security.Claims;
using System.Text.Encodings.Web;
using Jarvis.Orchestrator.Runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Jarvis.Api.Security;

public static class CorePolicies
{
    public const string Scheme = "JarvisCore";
    public const string Owner = "CoreOwner";
    public const string Companion = "CoreCompanion";
    public const string OwnerOrCompanion = "CoreOwnerOrCompanion";
    public const string PrivacyManage = "CoreOwnerStepUp:privacy.manage";
    public const string DeviceManage = "CoreOwnerStepUp:device.manage";
    public const string DeviceVerify = "CoreOwnerStepUp:device.verify";
    public const string DeviceGrants = "CoreOwnerStepUp:" + DeviceGrantService.ManageScope;
    public const string DeviceCommandConfirm = "CoreOwnerStepUp:" + DeviceCommandService.ConfirmScope;
    public const string ToolConfirm = "CoreOwnerStepUp:" + ToolService.ConfirmScope;
    public const string DomainManage = "CoreOwnerStepUp:domain.manage";
    public const string IntelligenceEvaluate = "CoreOwnerStepUp:" + Jarvis.Orchestrator.Intelligence.IntelligenceEvaluation.ManageScope;
    public const string BudgetManage = "CoreOwnerStepUp:budget.manage";
}

/// <summary>Turns the identity validated by ApiKeyMiddleware into the principal used by endpoint policies.</summary>
public sealed class JarvisItemAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.Items[ApiKeyMiddleware.ActorItem] is not CoreActor actor)
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, actor.SubjectId),
            new("jarvis_actor", actor.DeviceId is null ? "owner" : "companion")
        };
        if (actor.DeviceId is not null) claims.Add(new("jarvis_device_id", actor.DeviceId));
        if (Context.Items[ApiKeyMiddleware.FingerprintItem] is string fingerprint)
            claims.Add(new("jarvis_credential_fingerprint", fingerprint));
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

public sealed record JarvisActorRequirement(string Actor) : IAuthorizationRequirement;
public sealed record OwnerStepUpRequirement(string Scope) : IAuthorizationRequirement;
public sealed class UndeclaredEndpointRequirement : IAuthorizationRequirement;

public sealed class JarvisActorRequirementHandler : AuthorizationHandler<JarvisActorRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, JarvisActorRequirement requirement)
    {
        if (context.User.HasClaim("jarvis_actor", requirement.Actor)) context.Succeed(requirement);
        return Task.CompletedTask;
    }

}

public sealed class OwnerStepUpHandler(OwnerAuthorization owner, IHttpContextAccessor accessor) : AuthorizationHandler<OwnerStepUpRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OwnerStepUpRequirement requirement)
    {
        var http = context.Resource as HttpContext ?? accessor.HttpContext;
        if (http is not null)
        {
            _ = owner.Require(http, requirement.Scope);
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}

public sealed class CoreStepUpPolicyProvider(IOptions<AuthorizationOptions> options) : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);
    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();
    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        const string prefix = "CoreOwnerStepUp:";
        if (!policyName.StartsWith(prefix, StringComparison.Ordinal) || policyName.Length == prefix.Length)
            return _fallback.GetPolicyAsync(policyName);
        var scope = policyName[prefix.Length..];
        return Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder(CorePolicies.Scheme)
            .RequireAuthenticatedUser().AddRequirements(new JarvisActorRequirement("owner"), new OwnerStepUpRequirement(scope)).Build());
    }
}

public static class CoreAuthorizationRegistration
{
    public static IServiceCollection AddCoreAuthorization(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddAuthentication(CorePolicies.Scheme).AddScheme<AuthenticationSchemeOptions, JarvisItemAuthenticationHandler>(CorePolicies.Scheme, _ => { });
        // Core authenticates with keys and tickets. Keep framework protection in process memory and skip disk key-ring startup.
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var keyRingStartup = services.SingleOrDefault(descriptor => descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType?.FullName == "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService");
        if (keyRingStartup is not null) services.Remove(keyRingStartup);
        services.AddAuthorization(options =>
        {
            options.AddPolicy(CorePolicies.Owner, p => p.RequireAuthenticatedUser().AddRequirements(new JarvisActorRequirement("owner")));
            options.AddPolicy(CorePolicies.Companion, p => p.RequireAuthenticatedUser().AddRequirements(new JarvisActorRequirement("companion")));
            options.AddPolicy(CorePolicies.OwnerOrCompanion, p => p.RequireAuthenticatedUser());
            options.FallbackPolicy = new AuthorizationPolicyBuilder(CorePolicies.Scheme).RequireAuthenticatedUser()
                .AddRequirements(new UndeclaredEndpointRequirement()).Build();
        });
        services.AddSingleton<IAuthorizationPolicyProvider, CoreStepUpPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, JarvisActorRequirementHandler>();
        services.AddSingleton<IAuthorizationHandler, OwnerStepUpHandler>();
        services.AddSingleton<IAuthorizationHandler, UndeclaredEndpointHandler>();
        return services;
    }
}

public sealed class UndeclaredEndpointHandler : AuthorizationHandler<UndeclaredEndpointRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, UndeclaredEndpointRequirement requirement) => Task.CompletedTask;
}
