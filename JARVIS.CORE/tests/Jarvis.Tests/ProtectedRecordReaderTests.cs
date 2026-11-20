using System.Buffers;
using System.Text.Json;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;

namespace Jarvis.Tests;

public sealed class ProtectedRecordReaderTests
{
    private static readonly ProtectedRecordContext Context = new("forge.checkin", "owner", Guid.NewGuid(), Guid.NewGuid(), 1, 2);
    private static ProtectedRecordKeyOptions Options(byte value = 7) => new() {
        ActiveKeyId = "synthetic-key", Keys = new Dictionary<string, string> { ["synthetic-key"] = Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray()) }
    };
    private sealed class ObservedPool : ArrayPool<byte>
    {
        public int Rentals { get; private set; }
        public int Returns { get; private set; }
        public byte[]? LastRental { get; private set; }
        public byte[]? AtReturn { get; private set; }
        public override byte[] Rent(int minimumLength) {
            Rentals++; return LastRental = Enumerable.Repeat((byte)0xA5, minimumLength + 32).ToArray();
        }
        public override void Return(byte[] array, bool clearArray = false) {
            Assert.Same(LastRental, array); Returns++; AtReturn = array.ToArray();
        }
        public void AssertCleared() {
            Assert.Equal(1, Rentals); Assert.Equal(1, Returns); Assert.NotNull(AtReturn);
            Assert.All(AtReturn!, b => Assert.Equal((byte)0, b));
            Assert.All(LastRental!, b => Assert.Equal((byte)0, b));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(65_536)]
    public void Typed_reader_sees_only_payload_and_entire_rental_is_zero_before_return(int bytes)
    {
        var pool = new ObservedPool(); IProtectedRecordProtector codec = new ProtectedRecordProtector(() => Options(), pool);
        var payload = Enumerable.Repeat((byte)'r', bytes).ToArray(); var envelope = codec.Protect(Context, payload);
        var count = codec.Unprotect(Context, envelope, value => {
            Assert.Equal(bytes, value.Length); Assert.True(value.SequenceEqual(payload)); return value.Length;
        });
        Assert.Equal(bytes, count); pool.AssertCleared();
    }

    [Fact]
    public void Reader_exception_is_preserved_and_private_buffer_is_still_zeroed()
    {
        var pool = new ObservedPool(); var codec = new ProtectedRecordProtector(() => Options(), pool);
        var envelope = codec.Protect(Context, "synthetic-private-value"u8);
        var expected = new IOException("Synthetic reader failure.");
        var error = Assert.Throws<IOException>(() => codec.Unprotect<int>(Context, envelope, bytes => {
            Assert.True(bytes.SequenceEqual("synthetic-private-value"u8)); throw expected;
        }));
        Assert.Same(expected, error); pool.AssertCleared();
    }

    [Theory]
    [InlineData("tamper", 409)]
    [InlineData("wrong-key", 409)]
    [InlineData("missing-key", 503)]
    public void Failed_decryption_never_calls_reader_and_clears_rental(string scenario, int status)
    {
        var pool = new ObservedPool(); var options = Options(); var codec = new ProtectedRecordProtector(() => options, pool);
        var envelope = codec.Protect(Context, "synthetic-private-value"u8);
        if (scenario == "tamper") { var tag = envelope.Tag.ToArray(); tag[0] ^= 1; envelope = envelope with { Tag = tag }; }
        if (scenario == "wrong-key") options = Options(8);
        if (scenario == "missing-key") options = new();
        var called = false;
        var error = Assert.Throws<DomainException>(() => codec.Unprotect(Context, envelope, bytes => { called = true; return bytes.Length; }));
        Assert.Equal(status, error.StatusCode); Assert.False(called); pool.AssertCleared();
    }

    [Fact]
    public void Invalid_input_and_null_reader_are_rejected_before_renting_plaintext()
    {
        var pool = new ObservedPool(); var codec = new ProtectedRecordProtector(() => Options(), pool);
        var envelope = codec.Protect(Context, "synthetic"u8);
        Assert.Throws<ArgumentNullException>(() => codec.Unprotect<int>(Context, envelope, null!));
        Assert.Equal(409, Assert.Throws<DomainException>(() => codec.Unprotect(Context, envelope with { Tag = [] }, bytes => bytes.Length)).StatusCode);
        Assert.Equal(409, Assert.Throws<DomainException>(() => codec.Unprotect(Context, envelope with { Ciphertext = new byte[65_537] }, bytes => bytes.Length)).StatusCode);
        Assert.Equal(0, pool.Rentals); Assert.Equal(0, pool.Returns);
    }

    public sealed record SyntheticObservation(int Value);
    [Fact]
    public void Typed_decode_uses_the_scoped_reader_and_clears_on_decode_failure()
    {
        var pool = new ObservedPool(); IProtectedRecordProtector codec = new ProtectedRecordProtector(() => Options(), pool);
        var envelope = codec.Protect(Context, "{\"Value\":7}"u8);
        var result = codec.Unprotect(Context, envelope, bytes => JsonSerializer.Deserialize<SyntheticObservation>(bytes)!);
        Assert.Equal(7, result.Value); pool.AssertCleared();
        var invalidPool = new ObservedPool(); var invalidCodec = new ProtectedRecordProtector(() => Options(), invalidPool);
        var invalid = invalidCodec.Protect(Context, "not-json"u8);
        Assert.Throws<JsonException>(() => invalidCodec.Unprotect(Context, invalid, bytes => JsonSerializer.Deserialize<SyntheticObservation>(bytes)));
        invalidPool.AssertCleared();
    }
}
