using System.Text.Json;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;

namespace Jarvis.Tests;

public sealed class ProtectedRecordTests
{
    private static readonly ProtectedRecordContext Context = new("forge.checkin", "owner",
        Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), 3, 4);
    private static string TestKey(int start = 0) => Convert.ToBase64String(Enumerable.Range(start, 32).Select(x => (byte)x).ToArray());
    private static ProtectedRecordKeyOptions Options(string id = "key-1", string? key = null) =>
        new() { ActiveKeyId = id, Keys = new Dictionary<string, string> { [id] = key ?? TestKey() } };
    private static void Error(int status, Action action) => Assert.Equal(status, Assert.Throws<DomainException>(action).StatusCode);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65_536)]
    public void Payload_roundtrips_with_random_nonces_and_no_plaintext_or_root_key_in_envelope(int bytes)
    {
        var options = Options(); var codec = new ProtectedRecordProtector(() => options);
        var payload = Enumerable.Repeat((byte)'a', bytes).ToArray(); var first = codec.Protect(Context, payload); var second = codec.Protect(Context, payload);
        Assert.True(codec.Ready); Assert.Equal(payload, codec.Unprotect(Context, first)); Assert.Equal(payload, codec.Unprotect(Context, second));
        Assert.NotEqual(first.Nonce, second.Nonce); Assert.Equal(12, first.Nonce.Length); Assert.Equal(16, first.Tag.Length);
        Assert.Equal(bytes, first.Ciphertext.Length); Assert.Equal(1, first.Version);
        Assert.DoesNotContain(TestKey(), JsonSerializer.Serialize(first));
        if (bytes >= 64) Assert.DoesNotContain(new string('a', 64), JsonSerializer.Serialize(first));
        Assert.All(payload, b => Assert.Equal((byte)'a', b)); // Borrowed caller buffer remains owned by the caller.
    }

    [Theory]
    [InlineData("purpose")]
    [InlineData("owner")]
    [InlineData("scope")]
    [InlineData("record")]
    [InlineData("epoch")]
    [InlineData("revision")]
    public void Copied_identity_cannot_decrypt_or_replay_a_private_record(string field)
    {
        var codec = new ProtectedRecordProtector(() => Options()); var payload = "private-synthetic-observation"u8.ToArray();
        var envelope = codec.Protect(Context, payload); var proof = codec.CreateRetryProof(Context, payload);
        var copied = field switch {
            "purpose" => Context with { Purpose = "forge.capacity" }, "owner" => Context with { OwnerScope = "another-owner" },
            "scope" => Context with { ScopeId = Guid.NewGuid() }, "record" => Context with { RecordId = Guid.NewGuid() },
            "epoch" => Context with { Epoch = 4 }, _ => Context with { Revision = 5 }
        };
        Error(409, () => codec.Unprotect(copied, envelope)); Assert.False(codec.MatchesRetryProof(copied, proof, payload));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("key-alias")]
    [InlineData("nonce")]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    [InlineData("short-nonce")]
    [InlineData("short-tag")]
    [InlineData("null-ciphertext")]
    [InlineData("oversized")]
    public void Tampering_and_malformed_envelopes_fail_without_returning_plaintext(string field)
    {
        var key = TestKey(); var codec = new ProtectedRecordProtector(() => new() { ActiveKeyId = "key-1", Keys = new Dictionary<string, string> { ["key-1"] = key, ["key-2"] = key } });
        var envelope = codec.Protect(Context, "private-synthetic-value"u8);
        static byte[] Flip(byte[] bytes) { var result = bytes.ToArray(); result[0] ^= 1; return result; }
        var changed = field switch {
            "version" => envelope with { Version = 2 }, "key-alias" => envelope with { KeyId = "key-2" },
            "nonce" => envelope with { Nonce = Flip(envelope.Nonce) }, "ciphertext" => envelope with { Ciphertext = Flip(envelope.Ciphertext) },
            "tag" => envelope with { Tag = Flip(envelope.Tag) }, "short-nonce" => envelope with { Nonce = new byte[11] },
            "short-tag" => envelope with { Tag = new byte[15] }, "null-ciphertext" => envelope with { Ciphertext = null! },
            _ => envelope with { Ciphertext = new byte[ProtectedRecordProtector.MaximumPlaintextBytes + 1] }
        };
        Error(409, () => codec.Unprotect(Context, changed));
        Assert.Equal("private-synthetic-value"u8.ToArray(), codec.Unprotect(Context, envelope));
    }

    [Fact]
    public void Retry_proof_is_stable_content_bound_and_uses_the_separate_HKDF_purpose()
    {
        var codec = new ProtectedRecordProtector(() => Options()); var request = "{\"value\":7}"u8.ToArray();
        var proof = codec.CreateRetryProof(Context, request);
        // Independently calculated HKDF-SHA256 extract/expand + HMAC-SHA256 known answer;
        // public root bytes 00..1f, canonical AAD described in ADR 0029, request above.
        Assert.Equal("6393E0C63250655733EED25FBE72945C76F5EFEBCD2381D830310548BDCE0E57", Convert.ToHexString(proof.Mac));
        Assert.Equal(proof.Mac, codec.CreateRetryProof(Context, request).Mac);
        Assert.True(codec.MatchesRetryProof(Context, proof, request)); Assert.False(codec.MatchesRetryProof(Context, proof, "{\"value\":8}"u8));
        Assert.False(codec.MatchesRetryProof(Context, proof with { Mac = new byte[31] }, request));
        Assert.False(codec.MatchesRetryProof(Context, proof with { Version = 2 }, request));
    }

    [Fact]
    public void Rotation_and_synthetic_key_backup_restore_preserve_reads_and_old_retries()
    {
        var options = Options(); var codec = new ProtectedRecordProtector(() => options); var payload = "private-synthetic-value"u8.ToArray();
        var original = codec.Protect(Context, payload); var proof = codec.CreateRetryProof(Context, payload);
        options = new() { ActiveKeyId = "key-2", Keys = new Dictionary<string, string> { ["key-1"] = TestKey(), ["key-2"] = TestKey(32) } };
        Assert.Equal(payload, codec.Unprotect(Context, original)); Assert.True(codec.MatchesRetryProof(Context, proof, payload));
        var next = codec.Protect(Context, payload); Assert.Equal("key-2", next.KeyId); Assert.NotEqual(proof.Mac, codec.CreateRetryProof(Context, payload).Mac);
        var recoveredKeys = new Dictionary<string, string>(options.Keys!);
        var restored = new ProtectedRecordProtector(() => new() { ActiveKeyId = "key-2", Keys = recoveredKeys });
        Assert.Equal(payload, restored.Unprotect(Context, original)); Assert.Equal(payload, restored.Unprotect(Context, next));
        Assert.True(restored.MatchesRetryProof(Context, proof, payload));
        options = Options("key-2", TestKey(32));
        Error(503, () => codec.Unprotect(Context, original)); Error(503, () => codec.MatchesRetryProof(Context, proof, payload));
        Assert.Equal(payload, codec.Unprotect(Context, next));
    }

    [Fact]
    public void Wrong_present_key_fails_authentication_and_read_keys_do_not_require_a_writable_active_key()
    {
        var envelope = new ProtectedRecordProtector(() => Options()).Protect(Context, "synthetic"u8);
        Error(409, () => new ProtectedRecordProtector(() => Options(key: TestKey(32))).Unprotect(Context, envelope));
        var reader = new ProtectedRecordProtector(() => new() { ActiveKeyId = null, Keys = Options().Keys });
        Assert.False(reader.Ready); Assert.Equal("synthetic"u8.ToArray(), reader.Unprotect(Context, envelope));
        Error(503, () => reader.Protect(Context, "synthetic"u8));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    [InlineData("eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eA==")]
    public void Missing_or_malformed_keys_fail_closed_with_generic_errors(string key)
    {
        var codec = new ProtectedRecordProtector(() => Options(key: key));
        Assert.False(codec.Ready); Error(503, () => codec.Protect(Context, "synthetic"u8));
        Error(503, () => codec.CreateRetryProof(Context, "synthetic"u8));
        var missing = new ProtectedRecordProtector(() => new()); Assert.False(missing.Ready);
        Error(503, () => missing.Protect(Context, "synthetic"u8));
    }

    [Fact]
    public void Oversized_requests_and_invalid_identities_are_rejected_before_key_access()
    {
        var codec = new ProtectedRecordProtector(() => throw new InvalidOperationException("Key access should not occur."));
        Error(400, () => codec.Protect(Context, new byte[65_537])); Error(400, () => codec.CreateRetryProof(Context, new byte[65_537]));
        foreach (var bad in new[] { Context with { ScopeId = Guid.Empty }, Context with { RecordId = Guid.Empty },
            Context with { OwnerScope = "bad\nowner" }, Context with { Purpose = "bad\uD800" }, Context with { Epoch = -1 }, Context with { Revision = 0 } }) {
            Error(400, () => codec.Protect(bad, "synthetic"u8));
        }
    }
}
