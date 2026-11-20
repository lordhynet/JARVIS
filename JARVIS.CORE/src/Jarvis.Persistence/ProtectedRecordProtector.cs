using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Persistence;

// Private configuration adapter supplies this snapshot. No default key, key generation or recording-key fallback.
public sealed class ProtectedRecordKeyOptions
{
    public string? ActiveKeyId { get; init; }
    public IReadOnlyDictionary<string, string>? Keys { get; init; }
}

public sealed class ProtectedRecordProtector(Func<ProtectedRecordKeyOptions> options, ArrayPool<byte>? plaintextPool = null) : IProtectedRecordProtector
{
    private readonly ArrayPool<byte> plaintextBuffers = plaintextPool ?? ArrayPool<byte>.Shared;
    public const int FormatVersion = 1;
    public const int MaximumPlaintextBytes = IProtectedRecordProtector.MaximumPlaintextBytes;
    private const string Unavailable = "Protected data key is not configured or available.";
    private const string InvalidEnvelope = "Protected record could not be opened.";
    private static bool Label(string? value, int maximum = 64) => value is { Length: > 0 } && value.Length <= maximum &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '@');
    private static void Validate(ProtectedRecordContext context, int bytes)
    {
        Guard.Require(context is not null && Label(context.Purpose) && Label(context.OwnerScope, 80) &&
            context.ScopeId != Guid.Empty && context.RecordId != Guid.Empty && context.Epoch >= 0 && context.Revision > 0,
            400, "Protected record identity is invalid.");
        Guard.Require(bytes <= MaximumPlaintextBytes, 400, "Protected payload exceeds its size limit.");
    }
    private static byte[] Aad(ProtectedRecordContext context, string keyId) => JsonSerializer.SerializeToUtf8Bytes(new object[] {
        "jarvis-protected-record", FormatVersion, keyId, context.Purpose, context.OwnerScope,
        context.ScopeId.ToString("N"), context.RecordId.ToString("N"), context.Epoch, context.Revision
    });
    private static byte[] Key(ProtectedRecordKeyOptions value, string? keyId)
    {
        var encoded = Label(keyId) && value.Keys is not null && value.Keys.TryGetValue(keyId!, out var found) ? found : null;
        Guard.Require(encoded is { Length: 44 }, 503, Unavailable);
        byte[]? key = null;
        try {
            key = Convert.FromBase64String(encoded);
            if (key.Length == 32) return key;
        } catch (FormatException) { }
        if (key is not null) CryptographicOperations.ZeroMemory(key);
        throw new DomainException(503, Unavailable);
    }
    private ProtectedRecordKeyOptions Options() => options() ?? throw new DomainException(503, Unavailable);
    private (string Id, byte[] Root) ActiveKey()
    {
        var value = Options(); return (value.ActiveKeyId!, Key(value, value.ActiveKeyId));
    }
    private static byte[] Derive(byte[] root, string purpose) => HKDF.DeriveKey(HashAlgorithmName.SHA256, root, 32,
        info: Encoding.UTF8.GetBytes("jarvis:protected-records:v1:" + purpose));
    public bool Ready {
        get {
            try { var (_, root) = ActiveKey(); CryptographicOperations.ZeroMemory(root); return true; }
            catch (DomainException) { return false; }
        }
    }
    public ProtectedRecordEnvelope Protect(ProtectedRecordContext context, ReadOnlySpan<byte> plaintext)
    {
        Validate(context, plaintext.Length);
        var (id, root) = ActiveKey(); byte[]? key = null;
        try {
            key = Derive(root, "encryption");
            var nonce = RandomNumberGenerator.GetBytes(12); var ciphertext = new byte[plaintext.Length]; var tag = new byte[16];
            using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad(context, id));
            return new(FormatVersion, id, nonce, ciphertext, tag);
        } finally { CryptographicOperations.ZeroMemory(root); if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
    private static void ValidateEnvelope(ProtectedRecordContext context, ProtectedRecordEnvelope envelope)
    {
        Validate(context, 0);
        Guard.Require(envelope is not null && envelope.Version == FormatVersion && Label(envelope.KeyId) &&
            envelope.Nonce is { Length: 12 } && envelope.Tag is { Length: 16 } && envelope.Ciphertext is { Length: <= MaximumPlaintextBytes },
            409, InvalidEnvelope);
    }
    private void DecryptValidated(ProtectedRecordContext context, ProtectedRecordEnvelope envelope, Span<byte> plaintext)
    {
        var root = Key(Options(), envelope.KeyId); byte[]? key = null;
        try {
            key = Derive(root, "encryption");
            using var aes = new AesGcm(key, 16); aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, Aad(context, envelope.KeyId));
        } catch (CryptographicException) {
            throw new DomainException(409, InvalidEnvelope);
        } finally { CryptographicOperations.ZeroMemory(root); if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
    public byte[] Unprotect(ProtectedRecordContext context, ProtectedRecordEnvelope envelope)
    {
        ValidateEnvelope(context, envelope); var plaintext = new byte[envelope.Ciphertext.Length];
        try { DecryptValidated(context, envelope, plaintext); return plaintext; }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
    }
    public T Unprotect<T>(ProtectedRecordContext context, ProtectedRecordEnvelope envelope, Func<ReadOnlySpan<byte>, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        ValidateEnvelope(context, envelope);
        var buffer = plaintextBuffers.Rent(Math.Max(1, envelope.Ciphertext.Length));
        try {
            var plaintext = buffer.AsSpan(0, envelope.Ciphertext.Length);
            DecryptValidated(context, envelope, plaintext);
            return read(plaintext);
        } finally {
            // Clear the whole rental, including unused pool capacity, before any reuse.
            CryptographicOperations.ZeroMemory(buffer);
            plaintextBuffers.Return(buffer);
        }
    }
    private static byte[] Mac(ProtectedRecordContext context, string id, byte[] root, ReadOnlySpan<byte> request)
    {
        var key = Derive(root, "retry-comparison");
        try {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            hmac.AppendData(Aad(context, id)); hmac.AppendData(request); return hmac.GetHashAndReset();
        } finally { CryptographicOperations.ZeroMemory(key); }
    }
    public ProtectedRetryProof CreateRetryProof(ProtectedRecordContext context, ReadOnlySpan<byte> canonicalRequest)
    {
        Validate(context, canonicalRequest.Length); var (id, root) = ActiveKey();
        try { return new(FormatVersion, id, Mac(context, id, root, canonicalRequest)); }
        finally { CryptographicOperations.ZeroMemory(root); }
    }
    public bool MatchesRetryProof(ProtectedRecordContext context, ProtectedRetryProof proof, ReadOnlySpan<byte> canonicalRequest)
    {
        Validate(context, canonicalRequest.Length);
        if (proof is null || proof.Version != FormatVersion || !Label(proof.KeyId) || proof.Mac is not { Length: 32 }) return false;
        var root = Key(Options(), proof.KeyId); byte[]? actual = null;
        try {
            actual = Mac(context, proof.KeyId, root, canonicalRequest);
            return CryptographicOperations.FixedTimeEquals(actual, proof.Mac);
        } finally { CryptographicOperations.ZeroMemory(root); if (actual is not null) CryptographicOperations.ZeroMemory(actual); }
    }
}
