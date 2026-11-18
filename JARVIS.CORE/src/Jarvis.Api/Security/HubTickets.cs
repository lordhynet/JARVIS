using System.Security.Cryptography;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Security;

public sealed record HubTicketIdentity(CoreActor Actor, string Fingerprint);
public sealed class HubTickets(IRuntimeStore store, IPrivacyReader privacy, IConfiguration config, TimeProvider clock)
{
    public const string Prefix = "jht1_";
    public async Task<object> IssueAsync(CancellationToken ct)
    {
        var status = await privacy.StatusAsync(ct);
        var secret = Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); var hash = PrivacyData.Hash(secret);
        var expires = clock.GetUtcNow().AddSeconds(30);
        var actor = DeviceCredentialContext.CurrentActor;
        Guard.Require(actor?.DeviceId is null || actor.IsOwnerBrowser, 403, "Only owner browsers can request status hub tickets.");
        return await store.UpdateAsync<object>(s => {
            foreach (var key in s.HubTickets.Where(x => x.Value.ExpiresAt <= clock.GetUtcNow()).Select(x => x.Key).ToList()) s.HubTickets.Remove(key);
            Guard.Require(s.HubTickets.Count < 100, 429, "Too many outstanding hub tickets.");
            if (actor is { IsOwnerBrowser: true }) DeviceCredentialContext.RequireCurrent(s, actor.DeviceId!);
            s.HubTickets[hash] = new(actor is { IsOwnerBrowser: true } ? actor.CredentialHash! : PrivacyData.Hash(config[ConfigKeys.CoreApiKey]!),
                status.Epoch, expires, actor is { IsOwnerBrowser: true } ? actor : null);
            return new { ticket = secret, expiresAt = expires, transport = "WebSockets", skipNegotiation = true };
        }, ct);
    }
    public async Task<string> ConsumeAsync(string secret, CancellationToken ct) => (await ConsumeIdentityAsync(secret, ct)).Fingerprint;
    public async Task<HubTicketIdentity> ConsumeIdentityAsync(string secret, CancellationToken ct)
    {
        Guard.Require(secret.StartsWith(Prefix, StringComparison.Ordinal) && secret.Length == 69, 401, "Invalid hub ticket.");
        var status = await privacy.StatusAsync(ct);
        return await store.UpdateAsync(s => {
            Guard.Require(s.HubTickets.Remove(PrivacyData.Hash(secret), out var ticket) && ticket.ExpiresAt > clock.GetUtcNow() &&
                ticket.Epoch == status.Epoch, 401, "Hub ticket expired, was consumed, or belongs to a previous Core identity.");
            if (ticket.Actor is { IsOwnerBrowser: true, DeviceId: { } id } actor) {
                Guard.Require(s.DeviceCredentials.TryGetValue(id, out var credential) && !credential.Revoked &&
                    credential.Purpose == DeviceCredentials.OwnerBrowserPurpose && credential.SecretHash == actor.CredentialHash &&
                    ticket.KeyFingerprint == credential.SecretHash, 401, "The enrolled browser credential changed or was revoked.");
                return new HubTicketIdentity(actor, ticket.KeyFingerprint);
            }
            Guard.Require(!BrowserOwnerAccess.RequiresEnrollment(config) && ticket.KeyFingerprint == PrivacyData.Hash(config[ConfigKeys.CoreApiKey]!),
                401, "Legacy browser authentication is disabled or its Core identity changed.");
            return new HubTicketIdentity(new CoreActor("hub-ticket", null), ticket.KeyFingerprint);
        }, ct);
    }
}
