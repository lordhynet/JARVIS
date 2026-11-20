namespace Jarvis.Orchestrator.Runtime;

// Opaque identity only. Purpose is a fixed application label, never a private category/value.
public sealed record ProtectedRecordContext(string Purpose, string OwnerScope, Guid ScopeId, Guid RecordId, long Epoch, int Revision);
public sealed record ProtectedRecordEnvelope(int Version, string KeyId, byte[] Nonce, byte[] Ciphertext, byte[] Tag);
public sealed record ProtectedRetryProof(int Version, string KeyId, byte[] Mac);

/// <summary>JARVIS-owned local protection. It grants no authorization and performs no storage or provider calls.</summary>
public interface IProtectedRecordProtector
{
    bool Ready { get; }
    ProtectedRecordEnvelope Protect(ProtectedRecordContext context, ReadOnlySpan<byte> plaintext);
    // Caller owns the returned plaintext buffer and must clear it after consuming the typed payload.
    byte[] Unprotect(ProtectedRecordContext context, ProtectedRecordEnvelope envelope);
    ProtectedRetryProof CreateRetryProof(ProtectedRecordContext context, ReadOnlySpan<byte> canonicalRequest);
    bool MatchesRetryProof(ProtectedRecordContext context, ProtectedRetryProof proof, ReadOnlySpan<byte> canonicalRequest);
}
