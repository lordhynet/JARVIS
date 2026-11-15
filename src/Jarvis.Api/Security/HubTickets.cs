using System.Security.Cryptography;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Security;

public sealed class HubTickets(IRuntimeStore store, IPrivacyReader privacy, IConfiguration config, TimeProvider clock)
{
    public const string Prefix = "jht1_";
    public async Task<object> IssueAsync(CancellationToken ct)
    {
        var status = await privacy.StatusAsync(ct);
        var secret = Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); var hash = PrivacyData.Hash(secret);
        var expires = clock.GetUtcNow().AddSeconds(30);
        return await store.UpdateAsync<object>(s => {
            foreach (var key in s.HubTickets.Where(x => x.Value.ExpiresAt <= clock.GetUtcNow()).Select(x => x.Key).ToList()) s.HubTickets.Remove(key);
            Guard.Require(s.HubTickets.Count < 100, 429, "Too many outstanding hub tickets.");
            s.HubTickets[hash] = new(PrivacyData.Hash(config[ApiKeyMiddleware.ConfigKey]!), status.Epoch, expires);
            return new { ticket = secret, expiresAt = expires, transport = "WebSockets", skipNegotiation = true };
        }, ct);
    }
    public async Task<string> ConsumeAsync(string secret, CancellationToken ct)
    {
        Guard.Require(secret.StartsWith(Prefix, StringComparison.Ordinal) && secret.Length == 69, 401, "Invalid hub ticket.");
        var status = await privacy.StatusAsync(ct);
        return await store.UpdateAsync(s => {
            Guard.Require(s.HubTickets.Remove(PrivacyData.Hash(secret), out var ticket) && ticket.ExpiresAt > clock.GetUtcNow() &&
                ticket.Epoch == status.Epoch && ticket.KeyFingerprint == PrivacyData.Hash(config[ApiKeyMiddleware.ConfigKey]!), 401, "Hub ticket expired, was consumed, or belongs to a previous Core identity.");
            return ticket.KeyFingerprint;
        }, ct);
    }
}
