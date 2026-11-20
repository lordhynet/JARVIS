using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Runtime;

// Original creation response evidence in the existing learning store, not current mastery/history.
public sealed record LearningCreationReceipt(Guid RequestId, long Epoch, string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FingerprintVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Fingerprint,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LearningItem? Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? RetiredAt)
{
    public const int CurrentFingerprintVersion = 1;
    public const string Created = "created", Retired = "retired";
    public static string FingerprintFor(CreateLearningRequest normalized)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Version = CurrentFingerprintVersion, normalized.Domain, normalized.Prompt, normalized.Answer });
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Validate() => Guard.Require(RequestId != Guid.Empty && Epoch >= 0 &&
        (Status == Created && FingerprintVersion == CurrentFingerprintVersion && Fingerprint is { Length: 64 } &&
            Result is { Id: var id } && id != Guid.Empty && RetiredAt is null ||
         Status == Retired && FingerprintVersion is null && Fingerprint is null && Result is null && RetiredAt is not null),
        409, "Persisted learning creation receipt is invalid.");
    public LearningCreationExport Export() {
        Validate(); return new(RequestId, Epoch, Status, Result?.CreatedAt, Result?.Id, Result, RetiredAt);
    }
    public static void Retire(RuntimeState state, IReadOnlySet<Guid> cardIds, DateTimeOffset at)
    {
        foreach (var receipt in state.CreationReceipts.Values.Where(x => x.Result is { } result && cardIds.Contains(result.Id)).ToArray())
            state.CreationReceipts[receipt.RequestId] = new(receipt.RequestId, receipt.Epoch, Retired, null, null, null, at);
    }
}

public sealed record LearningCreationChange(LearningItem Result, LearningCreationReceipt? Receipt);
