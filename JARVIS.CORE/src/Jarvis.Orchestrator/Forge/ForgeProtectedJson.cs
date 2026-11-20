using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Forge;

// Typed-command boundary only: this grants no authorization, recovery readiness or domain validation.
// Whitespace padding hides exact JSON length within a bucket; cadence and bucket size remain observable.
public static class ForgeProtectedJson
{
    public const int BucketBytes = 512;
    // Utf8JsonWriter may request six bytes per UTF-16 character before it knows actual escaping cost.
    // Bound its scratch as well as the final output, while still accepting a full 64KiB ASCII JSON value.
    public const int MaximumSerializationBufferBytes = IProtectedRecordProtector.MaximumPlaintextBytes * 6 + 256;

    public static ProtectedRecordEnvelope Protect<T>(IProtectedRecordProtector protector, ProtectedRecordContext context,
        T payload, ArrayPool<byte>? plaintextPool = null) where T : class
    {
        Guard.Require(payload is not null, 400, "A protected payload is required.");
        var pool = plaintextPool ?? ArrayPool<byte>.Shared;
        var buffer = pool.Rent(MaximumSerializationBufferBytes);
        try {
            var output = new BoundedPlaintextWriter(buffer);
            try {
                using var writer = new Utf8JsonWriter(output);
                JsonSerializer.Serialize(writer, payload);
                writer.Flush();
            }
            catch (Exception error) when (error is JsonException or NotSupportedException) {
                throw new DomainException(400, "Protected payload could not be encoded.");
            }
            var padded = checked((output.Count + BucketBytes - 1) / BucketBytes * BucketBytes);
            Guard.Require(padded <= IProtectedRecordProtector.MaximumPlaintextBytes, 400, "Protected payload exceeds its size limit.");
            buffer.AsSpan(output.Count, padded - output.Count).Fill((byte)' ');
            return protector.Protect(context, buffer.AsSpan(0, padded));
        } finally {
            CryptographicOperations.ZeroMemory(buffer);
            pool.Return(buffer);
        }
    }

    // Existing unpadded JSON remains valid. No raw buffer copy escapes this scoped callback.
    public static T Read<T>(IProtectedRecordProtector protector, ProtectedRecordContext context,
        ProtectedRecordEnvelope envelope) where T : class => protector.Unprotect(context, envelope, plaintext => {
            try { return JsonSerializer.Deserialize<T>(plaintext) ?? throw new DomainException(409, "Protected payload could not be decoded."); }
            catch (Exception error) when (error is JsonException or NotSupportedException) {
                throw new DomainException(409, "Protected payload could not be decoded.");
            }
        });

    private sealed class BoundedPlaintextWriter(byte[] buffer) : IBufferWriter<byte>
    {
        public int Count { get; private set; }
        public void Advance(int count) {
            Guard.Require(count >= 0 && count <= IProtectedRecordProtector.MaximumPlaintextBytes - Count, 400, "Protected payload exceeds its size limit.");
            Count += count;
        }
        public Memory<byte> GetMemory(int sizeHint = 0) {
            Guard.Require(sizeHint >= 0 && sizeHint <= MaximumSerializationBufferBytes - Count, 400, "Protected payload exceeds its size limit.");
            return buffer.AsMemory(Count, MaximumSerializationBufferBytes - Count);
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}
