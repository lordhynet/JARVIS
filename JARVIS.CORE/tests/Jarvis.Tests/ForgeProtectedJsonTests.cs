using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;

namespace Jarvis.Tests;

public sealed class ForgeProtectedJsonTests
{
    private static readonly ProtectedRecordContext Context = new("forge.check-in.value", "personal-owner", Guid.NewGuid(), Guid.NewGuid(), 0, 1);
    private static ProtectedRecordProtector Protector(ArrayPool<byte>? pool = null, int seed = 0) => new(() => new() {
        ActiveKeyId = "synthetic", Keys = new Dictionary<string, string> { ["synthetic"] = Convert.ToBase64String(Enumerable.Range(seed, 32).Select(x => (byte)x).ToArray()) }
    }, pool);
    private sealed record Payload(string Observation, DateTimeOffset At, decimal? OptionalTotal);
    private sealed class Cycle { public Cycle Self => this; }
    private sealed class PlaintextPoolProbe : ArrayPool<byte>
    {
        // Deliberate unused rental capacity must be cleared too, not only the written JSON.
        private readonly byte[] buffer = new byte[ForgeProtectedJson.MaximumSerializationBufferBytes + 64];
        public int Returns { get; private set; }
        public bool Cleared { get; private set; } = true;
        public override byte[] Rent(int minimumLength) { Assert.True(minimumLength <= buffer.Length); Array.Fill(buffer, (byte)0xcc); return buffer; }
        public override void Return(byte[] array, bool clearArray = false) {
            Assert.Same(buffer, array); Cleared &= array.All(x => x == 0); Returns++;
        }
    }

    [Fact]
    public void ForgeProtected_json_optional_values_share_a_bucket_and_roundtrip_exact_provenance()
    {
        var writerPool = new PlaintextPoolProbe(); var readerPool = new PlaintextPoolProbe(); var protector = Protector(readerPool);
        var at = new DateTimeOffset(2026, 11, 20, 11, 0, 0, TimeSpan.FromHours(5.5)).AddTicks(7);
        var missing = new Payload("Synthetic français 日本語", at, null); var known = missing with { OptionalTotal = 123.45m };
        var first = ForgeProtectedJson.Protect(protector, Context, missing, writerPool);
        var second = ForgeProtectedJson.Protect(protector, Context, known, writerPool);
        Assert.Equal(512, first.Ciphertext.Length); Assert.Equal(first.Ciphertext.Length, second.Ciphertext.Length);
        Assert.NotEqual(first.Nonce, second.Nonce);
        var restored = ForgeProtectedJson.Read<Payload>(protector, Context, first);
        Assert.Equal(missing, restored); Assert.Equal(at.Offset, restored.At.Offset); Assert.Equal(at.Ticks, restored.At.Ticks);
        Assert.Equal(known, ForgeProtectedJson.Read<Payload>(protector, Context, second));
        Assert.True(writerPool.Cleared); Assert.Equal(2, writerPool.Returns); Assert.True(readerPool.Cleared); Assert.Equal(2, readerPool.Returns);
    }

    [Theory]
    [InlineData(510, 512)]
    [InlineData(511, 1024)]
    [InlineData(65_534, 65_536)]
    public void ForgeProtected_json_bucket_edges_and_full_capacity_are_supported(int characters, int bytes)
    {
        var pool = new PlaintextPoolProbe(); var protector = Protector(); var payload = new string('a', characters);
        var envelope = ForgeProtectedJson.Protect(protector, Context, payload, pool);
        Assert.Equal(bytes, envelope.Ciphertext.Length); Assert.Equal(payload, ForgeProtectedJson.Read<string>(protector, Context, envelope));
        Assert.True(pool.Cleared); Assert.Equal(1, pool.Returns);
    }

    [Fact]
    public void ForgeProtected_json_oversize_and_serialization_failure_clear_the_entire_rental()
    {
        var pool = new PlaintextPoolProbe(); var protector = Protector();
        Assert.Equal(400, Assert.Throws<DomainException>(() => ForgeProtectedJson.Protect(protector, Context, new string('a', 65_535), pool)).StatusCode);
        Assert.True(pool.Cleared); Assert.Equal(1, pool.Returns);
        var error = Assert.Throws<DomainException>(() => ForgeProtectedJson.Protect(protector, Context, new Cycle(), pool));
        Assert.Equal(400, error.StatusCode); Assert.Null(error.InnerException); Assert.True(pool.Cleared); Assert.Equal(2, pool.Returns);
    }

    [Fact]
    public void ForgeProtected_json_key_failure_clears_the_entire_rental_without_fallback()
    {
        var pool = new PlaintextPoolProbe(); var unavailable = new ProtectedRecordProtector(() => new());
        Assert.Equal(503, Assert.Throws<DomainException>(() => ForgeProtectedJson.Protect(unavailable, Context, "Synthetic", pool)).StatusCode);
        Assert.True(pool.Cleared); Assert.Equal(1, pool.Returns);
    }

    [Fact]
    public void ForgeProtected_json_reads_legacy_unpadded_records_without_rewriting_them()
    {
        var protector = Protector(); var payload = new Payload("Legacy synthetic", DateTimeOffset.UnixEpoch.AddTicks(7), null);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        try {
            var envelope = protector.Protect(Context, bytes); Assert.NotEqual(0, envelope.Ciphertext.Length % 512);
            var nonce = Convert.ToHexString(envelope.Nonce); Assert.Equal(payload, ForgeProtectedJson.Read<Payload>(protector, Context, envelope));
            Assert.Equal(nonce, Convert.ToHexString(envelope.Nonce)); Assert.Equal(bytes.Length, envelope.Ciphertext.Length);
        } finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [Fact]
    public void ForgeProtected_json_authenticates_padding_identity_and_key_and_sanitizes_invalid_json()
    {
        var pool = new PlaintextPoolProbe(); var protector = Protector(pool); var envelope = ForgeProtectedJson.Protect(protector, Context, "Synthetic");
        var changed = envelope with { Ciphertext = [.. envelope.Ciphertext] }; changed.Ciphertext[^1] ^= 1;
        Assert.Equal(409, Assert.Throws<DomainException>(() => ForgeProtectedJson.Read<string>(protector, Context, changed)).StatusCode);
        Assert.Equal(409, Assert.Throws<DomainException>(() => ForgeProtectedJson.Read<string>(protector, Context with { Revision = 2 }, envelope)).StatusCode);
        Assert.Equal(409, Assert.Throws<DomainException>(() => ForgeProtectedJson.Read<string>(Protector(seed: 1), Context, envelope)).StatusCode);
        var invalid = protector.Protect(Context, "not-valid-json"u8);
        var error = Assert.Throws<DomainException>(() => ForgeProtectedJson.Read<Payload>(protector, Context, invalid));
        Assert.Equal(409, error.StatusCode); Assert.Null(error.InnerException); Assert.DoesNotContain("not-valid-json", error.Message);
        Assert.True(pool.Cleared); Assert.Equal(3, pool.Returns);
    }
}
