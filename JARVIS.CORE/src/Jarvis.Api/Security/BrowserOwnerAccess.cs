using System.Security.Cryptography;
using System.Collections.Frozen;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Api.Security;

public sealed record OwnerElevationRequest(IReadOnlyList<string>? Scopes);
public sealed record IssuedOwnerElevation(string Token, DateTimeOffset ExpiresAt, IReadOnlyList<string> Scopes);

// Authentication extends the current enrolled-device credential and privacy epoch.
// A browser tag or native device kind alone never confers owner authority.
public sealed class BrowserOwnerAccess(IRuntimeStore store, IPrivacyReader privacy, TimeProvider clock, IConfiguration config)
{
    public const string Prefix = "joe1_";
    public const string ElevationItem = "Jarvis.OwnerElevation";
    public const string ElevationHeader = "X-Jarvis-Owner-Elevation";
    public const string EnrollmentRoute = "/api/browser/enrollment";
    public const string ElevationRoute = "/api/browser/elevation";
    public const string StatusRoute = "/api/browser/status";
    public static bool RequiresEnrollment(IConfiguration config)
    {
        var value = config[ConfigKeys.RequireEnrolledBrowser];
        Guard.Require(string.IsNullOrWhiteSpace(value) || bool.TryParse(value, out _), 503, "Browser authentication migration flag must be true or false.");
        return bool.TryParse(value, out var enabled) && enabled;
    }
    public static IReadOnlySet<string> ActiveFingerprints(RuntimeState state, IConfiguration config)
    {
        var current = state.DeviceCredentials.Values.Where(c => !c.Revoked && c.Purpose == DeviceCredentials.OwnerBrowserPurpose)
            .Select(c => c.SecretHash).ToHashSet(StringComparer.Ordinal);
        if (!RequiresEnrollment(config) && !string.IsNullOrWhiteSpace(config[ConfigKeys.CoreApiKey])) current.Add(PrivacyData.Hash(config[ConfigKeys.CoreApiKey]!));
        return current;
    }
    public static readonly IReadOnlySet<string> AllowedScopes = new[] {
        "privacy.manage", "device.manage", "device.verify", "budget.manage", "domain.manage",
        DeviceGrantService.ManageScope, DeviceCommandService.ConfirmScope, ToolService.ConfirmScope,
        AgentToolGateway.ManageScope, IntelligenceEvaluation.ManageScope
    }.ToFrozenSet(StringComparer.Ordinal);
    public async Task<IssuedOwnerElevation> IssueAsync(CoreActor actor, OwnerElevationRequest request, CancellationToken ct)
    {
        Guard.Require(actor.IsOwnerBrowser && actor.DeviceId is not null && actor.CredentialHash is not null && CoreInvocationContext.Origin == InvocationOrigin.ManualClient,
            403, "Use this browser's enrolled credential to request owner access.");
        Guard.Require(request.Scopes is { Count: > 0 and <= 10 }, 400, "Choose up to ten installed owner access scopes.");
        var scopes = request.Scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Guard.Require(scopes is { Length: > 0 and <= 10 } && scopes.All(AllowedScopes.Contains), 400, "Choose installed owner access scopes.");
        var status = await privacy.StatusAsync(ct); using var epoch = PrivacyContext.Enter(status.Epoch);
        var secret = Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); var hash = PrivacyData.Hash(secret);
        var expires = clock.GetUtcNow().AddMinutes(5);
        return await store.UpdateAsync(state => {
            DeviceCredentialContext.RequireCurrent(state, actor.DeviceId!);
            Guard.Require(state.DeviceCredentials.TryGetValue(actor.DeviceId!, out var credential) && !credential.Revoked &&
                credential.Purpose == DeviceCredentials.OwnerBrowserPurpose && credential.SecretHash == actor.CredentialHash, 401, "The enrolled browser identity changed.");
            state.DeviceCredentials[actor.DeviceId!] = credential with { Elevation = new(hash, status.Epoch, expires, scopes!, PrivacyData.Hash(config[ConfigKeys.OwnerKey]!)) };
            return new IssuedOwnerElevation(secret, expires, scopes!);
        }, ct);
    }
    public async Task<OwnerElevationRecord?> ValidateAsync(string? token, CoreActor actor, CancellationToken ct)
    {
        if (!actor.IsOwnerBrowser || token is not { Length: 69 } || !token.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var status = await privacy.StatusAsync(ct); var hash = PrivacyData.Hash(token);
        return await store.ReadAsync(state => actor.DeviceId is not null && state.DeviceCredentials.TryGetValue(actor.DeviceId, out var credential) &&
            !credential.Revoked && credential.Purpose == DeviceCredentials.OwnerBrowserPurpose && credential.SecretHash == actor.CredentialHash &&
            credential.Elevation is { } elevation && elevation.TokenHash == hash && elevation.Epoch == status.Epoch && elevation.ExpiresAt > clock.GetUtcNow() &&
            elevation.Scopes is { Count: > 0 and <= 10 } && elevation.Scopes.All(AllowedScopes.Contains) &&
            elevation.OwnerCredentialHash == PrivacyData.Hash(config[ConfigKeys.OwnerKey] ?? "")
            ? elevation : null, ct);
    }
    public Task<bool> RevokeAsync(CoreActor actor, CancellationToken ct) => store.UpdateAsync(state => {
        if (actor.DeviceId is not null && state.DeviceCredentials.TryGetValue(actor.DeviceId, out var credential) && credential.SecretHash == actor.CredentialHash)
            state.DeviceCredentials[actor.DeviceId] = credential with { Elevation = null };
        return true;
    }, ct);
}
