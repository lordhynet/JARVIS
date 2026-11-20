using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Forge;

public enum ForgeProtectedKind { CheckIn, Capacity }

// Infrastructure only. Private categories, observations, availability and request values belong in the envelope.
// Each immutable revision is also the original encrypted result of its request; no parallel receipt store.
public sealed record ForgeProtectedRecord(ForgeProtectedKind Kind, string OwnerScope, Guid ProgrammeId, Guid Id,
    long Epoch, int Revision, Guid RequestId, int ExpectedRevision, DateTimeOffset CreatedAt,
    ProtectedRecordEnvelope Envelope, ProtectedRetryProof RetryProof)
{
    private string Purpose => Kind switch {
        ForgeProtectedKind.CheckIn => "forge.check-in",
        ForgeProtectedKind.Capacity => "forge.capacity",
        _ => throw new DomainException(400, "Unknown protected Forge record kind.")
    };
    public ProtectedRecordContext ValueContext => new(Purpose + ".value", OwnerScope, ProgrammeId, Id, Epoch, Revision);
    // Context validation requires positive revisions. This reversible mapping supports expected revision zero.
    // A retry must use THIS stored expected revision/epoch and proof key, never the latest row's metadata.
    public ProtectedRecordContext RetryContext => new(Purpose + ".save", OwnerScope, ProgrammeId, Id, Epoch, checked(ExpectedRevision + 1));

    public void Validate(long epoch)
    {
        static bool Label(string? value, int max) => value is { Length: > 0 } && value.Length <= max &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '@');
        Guard.Require(Enum.IsDefined(Kind) && Label(OwnerScope, 80) && ProgrammeId != Guid.Empty && Id != Guid.Empty && RequestId != Guid.Empty &&
            ExpectedRevision >= 0 && ExpectedRevision < int.MaxValue && Revision == ExpectedRevision + 1,
            400, "Protected Forge record identity is invalid.");
        Guard.Require(Epoch >= 0 && Epoch == epoch, 409, "Protected Forge record belongs to another privacy generation.");
        Guard.Require(CreatedAt.Offset == TimeSpan.Zero && CreatedAt.Ticks % 10 == 0, 400, "Protected record timestamp must use canonical UTC precision.");
        Guard.Require(Envelope is { Version: 1, Nonce.Length: 12, Tag.Length: 16, Ciphertext.Length: <= IProtectedRecordProtector.MaximumPlaintextBytes } && Label(Envelope.KeyId, 64) &&
            RetryProof is { Version: 1, Mac.Length: 32 } && Label(RetryProof.KeyId, 64), 400, "Protected Forge envelope is invalid.");
    }

    public ForgeProtectedRecord CopyCiphertext() => this with {
        Envelope = Envelope with { Nonce = [.. Envelope.Nonce], Ciphertext = [.. Envelope.Ciphertext], Tag = [.. Envelope.Tag] },
        RetryProof = RetryProof with { Mac = [.. RetryProof.Mac] }
    };
}
