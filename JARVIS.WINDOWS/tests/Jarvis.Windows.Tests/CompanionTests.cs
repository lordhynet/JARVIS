using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Jarvis.Contracts;
using Jarvis.Windows.Client;

namespace Jarvis.Windows.Tests;

public sealed class CompanionTests
{
    internal static Connection Connection(bool consent = true) => new(new Uri("https://core.example/"), "jdv1_" + new string('a', 64), AllowCloudProcessing: consent);

    [Theory]
    [InlineData("Hey JARVIS", true, true)]
    [InlineData("hey jarvis", true, true)]
    [InlineData("Hey JARVIS", false, false)]
    [InlineData("hello Jarvis", true, false)]
    [InlineData("Jarvis", true, false)]
    [InlineData("hey Travis", true, false)]
    [InlineData("Hey JARVIS turn on a light", true, false)]
    public void Wake_is_exact_and_high_confidence(string phrase, bool high, bool expected) => Assert.Equal(expected, WakeMatcher.Accept(phrase, high));

    [Fact]
    public async Task Local_listening_and_rejected_phrases_make_zero_HTTP_calls()
    {
        await using var fixture = new Fixture();
        await fixture.Controller.ListenAsync();
        fixture.Wake.Say("hello Jarvis"); fixture.Wake.Say("Jarvis"); fixture.Wake.Say("hey Travis"); fixture.Wake.Say("Hey JARVIS", false);
        Assert.Empty(fixture.Http.Requests); Assert.Equal(1, fixture.Wake.Starts); Assert.Equal(0, fixture.Audio.Starts);
        await fixture.Controller.StopAsync();
        Assert.False(fixture.Wake.Listening); Assert.False(fixture.Audio.Active);
    }

    [Fact]
    public async Task Explicit_consent_precedes_all_network_calls()
    {
        await using var fixture = new Fixture(Connection(false));
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Controller.ListenAsync);
        fixture.Controller.PushToTalk(); await fixture.StateAsync(CompanionState.Unavailable);
        Assert.Empty(fixture.Http.Requests); Assert.Equal(0, fixture.Audio.Starts);
    }

    [Fact]
    public async Task Legacy_Core_is_refused_before_claiming_or_opening_audio()
    {
        await using var fixture = new Fixture(); fixture.Http.Protocol = 0;
        await fixture.Controller.ListenAsync(); fixture.Wake.Say("Hey JARVIS");
        await fixture.StateAsync(CompanionState.Unavailable);
        Assert.DoesNotContain(fixture.Http.Requests, x => x.Path == CompanionRoutes.VoiceWakeCandidates);
        Assert.Equal(0, fixture.Audio.Starts); Assert.Equal(0, fixture.Socket.Connections);
    }

    [Fact]
    public async Task A_phone_holding_the_lease_keeps_Windows_silent()
    {
        await using var fixture = new Fixture(); fixture.Http.ClaimStatus = HttpStatusCode.Conflict;
        await fixture.Controller.ListenAsync(); fixture.Wake.Say("Hey JARVIS");
        await fixture.StateAsync(CompanionState.ListeningLocally, skipFirst: true);
        Assert.Single(fixture.Http.Requests, x => x.Path == CompanionRoutes.VoiceWakeCandidates);
        Assert.Equal(0, fixture.Audio.Starts); Assert.Equal(0, fixture.Audio.Spoken); Assert.Equal(0, fixture.Socket.Connections);
    }

    [Fact]
    public async Task Duplicate_wakes_make_one_claim_and_use_scoped_credential_epoch_and_lock()
    {
        await using var fixture = new Fixture(); fixture.Locks.IsLocked = true;
        await fixture.Controller.ListenAsync();
        for (var i = 0; i < 12; i++) fixture.Wake.Say("Hey JARVIS");
        await fixture.Socket.SentAsync("authenticate");
        var request = Assert.Single(fixture.Http.Requests, x => x.Path == CompanionRoutes.VoiceWakeCandidates);
        Assert.Equal(Connection().Credential, request.Key); Assert.Equal("7", request.Epoch);
        Assert.True(JsonSerializer.Deserialize<JsonElement>(request.Body!).GetProperty("locked").GetBoolean());
        Assert.Equal(0, fixture.Audio.Starts); Assert.False(fixture.Wake.Listening);
    }

    [Fact]
    public async Task Ready_fence_gates_audio_and_renewal_discards_old_capture()
    {
        await using var fixture = new Fixture(); await fixture.StartSocketAsync();
        fixture.Audio.Capture([1, 0]); Assert.DoesNotContain(fixture.Socket.Frames, x => x.GetProperty("type").GetString() == "audio");
        fixture.Socket.Incoming(new { type = "ready", fence = 42, sampleRate = 24000, window = 1, locked = false, mode = "Everyday" });
        await fixture.StateAsync(CompanionState.Live); fixture.Audio.Capture([2, 0]);
        var first = await fixture.Socket.SentAsync("audio"); Assert.Equal(1, first.GetProperty("window").GetInt32());
        fixture.Socket.Incoming(new { type = "window_renewing", window = 1 }); await fixture.DetailAsync("Renewing");
        fixture.Audio.Capture([3, 0]);
        fixture.Socket.Incoming(new { type = "ready", fence = 42, sampleRate = 24000, window = 2, locked = false, mode = "Everyday" });
        await fixture.StateAsync(CompanionState.Live); fixture.Audio.Capture([4, 0]);
        var second = await fixture.Socket.SentAsync("audio"); Assert.Equal(2, second.GetProperty("window").GetInt32());
        Assert.Equal(Convert.ToBase64String([4, 0]), second.GetProperty("audio").GetString());
        await fixture.Controller.StopAsync(); Assert.False(fixture.Audio.Active); Assert.False(fixture.Wake.Listening);
        Assert.Single(fixture.Http.Requests, x => x.Path == CompanionRoutes.VoiceLocalRelease);
    }

    [Theory]
    [InlineData(43, 24000)]
    [InlineData(42, 16000)]
    public async Task Invalid_ready_shuts_down_and_releases_without_capturing(int fence, int rate)
    {
        await using var fixture = new Fixture(); await fixture.StartSocketAsync();
        fixture.Socket.Incoming(new { type = "ready", fence, sampleRate = rate, window = 1, locked = false, mode = "Everyday" });
        await fixture.StateAsync(CompanionState.Unavailable);
        Assert.Equal(0, fixture.Audio.Starts); Assert.True(fixture.Socket.Disposed);
        Assert.Single(fixture.Http.Requests, x => x.Path == CompanionRoutes.VoiceLocalRelease);
    }

    [Fact]
    public async Task Locking_gates_microphone_immediately_and_reports_locked_on_heartbeat()
    {
        await using var fixture = new Fixture(); await fixture.StartReadyAsync();
        fixture.Locks.IsLocked = true; fixture.Audio.Capture([1, 0]);
        await fixture.Controller.LockChangedAsync(); var heartbeat = await fixture.Socket.SentAsync("heartbeat");
        Assert.True(heartbeat.GetProperty("locked").GetBoolean());
        Assert.False(fixture.Audio.Active); // Local shutdown precedes any Core response.
        Assert.DoesNotContain(fixture.Socket.Frames, x => x.GetProperty("type").GetString() == "audio");
        fixture.Socket.Incoming(new { type = "ended", reason = "device_locked" }); await fixture.StateAsync(CompanionState.Off);
        Assert.False(fixture.Audio.Active);
    }

    [Fact]
    public async Task Lock_during_audio_setup_cannot_reopen_the_old_window_after_unlock()
    {
        await using var fixture = new Fixture(); fixture.Audio.StartGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.StartSocketAsync();
        fixture.Socket.Incoming(new { type = "ready", fence = 42, sampleRate = 24000, window = 1, locked = false, mode = "Everyday" });
        await fixture.Audio.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Locks.IsLocked = true; await fixture.Controller.LockChangedAsync(); await fixture.Audio.StoppedAsync();
        fixture.Locks.IsLocked = false; fixture.Audio.StartGate.SetResult(); await fixture.Audio.StoppedAsync();
        Assert.False(fixture.Audio.Active); fixture.Audio.Capture([1, 0]);
        Assert.DoesNotContain(fixture.Socket.Frames, x => x.GetProperty("type").GetString() == "audio");
        fixture.Socket.Incoming(new { type = "audio", delta = "AgA=", itemId = "old-private-output" });
        fixture.Socket.Incoming(new { type = "ended" }); await fixture.StateAsync(CompanionState.Off);
        Assert.Equal(0, fixture.Audio.Played);
    }

    [Fact]
    public async Task Serious_price_requires_the_current_offer_and_window()
    {
        await using var fixture = new Fixture(); await fixture.StartReadyAsync(); var offered = new TaskCompletionSource<ModeOffer>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Controller.Quote += value => offered.TrySetResult(value);
        var id = Guid.NewGuid(); fixture.Socket.Incoming(new { type = "voice_mode_offer", offerId = id, window = 1, maximumGbpForTenMinutes = 1.25m });
        Assert.Equal(id, (await offered.Task.WaitAsync(TimeSpan.FromSeconds(5))).Id);
        await fixture.Controller.ConfirmModeAsync(Guid.NewGuid()); Assert.DoesNotContain(fixture.Socket.Frames, x => x.GetProperty("type").GetString() == "confirm_voice_mode");
        await fixture.Controller.ConfirmModeAsync(id); var frame = await fixture.Socket.SentAsync("confirm_voice_mode");
        Assert.Equal(id, frame.GetProperty("offerId").GetGuid()); Assert.Equal(1, frame.GetProperty("window").GetInt32());
        await fixture.Controller.ConfirmModeAsync(id);
        Assert.Single(fixture.Socket.Frames, x => x.GetProperty("type").GetString() == "confirm_voice_mode");
    }

    [Fact]
    public async Task Audio_failure_closes_socket_and_stops_all_audio()
    {
        await using var fixture = new Fixture(); await fixture.StartReadyAsync(); fixture.Audio.Fail();
        await fixture.StateAsync(CompanionState.Unavailable); Assert.True(fixture.Socket.Disposed); Assert.False(fixture.Audio.Active);
    }

    [Fact]
    public async Task Wrong_heartbeat_fence_ends_the_session()
    {
        await using var fixture = new Fixture(); await fixture.StartReadyAsync();
        fixture.Socket.Incoming(new { type = "lease", sequence = 1, fence = 41, leaseSeconds = 15 });
        await fixture.StateAsync(CompanionState.Unavailable); Assert.False(fixture.Audio.Active);
    }

    [Fact]
    public async Task Unsolicited_lease_ack_cannot_extend_audio_ownership()
    {
        await using var fixture = new Fixture(); fixture.Http.LeaseDuration = TimeSpan.FromSeconds(1);
        await fixture.StartReadyAsync();
        fixture.Socket.Incoming(new { type = "lease", sequence = 999, fence = 42, leaseSeconds = 15 });
        await fixture.StateAsync(CompanionState.Unavailable); Assert.False(fixture.Audio.Active); Assert.True(fixture.Socket.Disposed);
    }

    [Fact]
    public async Task Fallback_prompt_finishes_before_spoken_consent_capture_and_verifying_mutes_again()
    {
        await using var fixture = new Fixture(); await fixture.StartSocketAsync();
        fixture.Audio.SpeechGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Socket.Incoming(new { type = "voice_fallback_offer", captureId = "consent-1" });
        await fixture.Audio.SpokenAsync("Serious voice");
        fixture.Audio.Capture([1, 0]); Assert.DoesNotContain(fixture.Socket.Frames, x => x.GetProperty("type").GetString() == "voice_fallback_audio");
        fixture.Audio.SpeechGate.SetResult(); await fixture.Socket.SentAsync("voice_fallback_played");
        fixture.Socket.Incoming(new { type = "voice_fallback_capture", captureId = "consent-1" });
        await fixture.DetailAsync("Listening for Everyday");
        fixture.Audio.Capture([2, 0]); var frame = await fixture.Socket.SentAsync("voice_fallback_audio");
        Assert.Equal("consent-1", frame.GetProperty("captureId").GetString());
        fixture.Socket.Incoming(new { type = "voice_fallback_verifying", captureId = "consent-1" });
        fixture.Socket.Incoming(new { type = "ended" }); await fixture.StateAsync(CompanionState.Off); Assert.False(fixture.Audio.Active);
    }

    [Fact]
    public async Task Winner_uses_configured_name_and_holds_microphone_until_local_greeting_finishes()
    {
        await using var fixture = new Fixture(); fixture.Http.OwnerName = "Casey";
        fixture.Audio.SpeechGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.StartSocketAsync(); await fixture.Audio.SpokenAsync("Hi, Casey.");
        Assert.True(fixture.Socket.Frames.First().GetProperty("localGreeting").GetBoolean());
        fixture.Socket.Incoming(new { type = "ready", fence = 42, sampleRate = 24000, window = 1, locked = false, mode = "Everyday" });
        fixture.Audio.Capture([1, 0]); Assert.Equal(0, fixture.Audio.Starts);
        Assert.DoesNotContain(fixture.Socket.Frames, x => x.GetProperty("type").GetString() == "audio");
        fixture.Audio.SpeechGate.SetResult(); await fixture.StateAsync(CompanionState.Live); fixture.Audio.Capture([2, 0]);
        await fixture.Socket.SentAsync("audio");
        Assert.DoesNotContain(fixture.Socket.Frames, x => x.GetRawText().Contains("Hi, Casey.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failure_after_winning_has_a_local_failure_message_and_stops_audio()
    {
        await using var fixture = new Fixture(); await fixture.StartSocketAsync();
        fixture.Socket.Incoming(new { type = "error", code = "provider_unavailable" }); await fixture.StateAsync(CompanionState.Unavailable);
        Assert.Contains("Sorry, I can't reach Core right now.", fixture.Audio.SpokenTexts); Assert.False(fixture.Audio.Active);
    }

    [Fact]
    public async Task Connection_timeout_after_greeting_is_reported_as_failure_and_releases_audio()
    {
        await using var fixture = new Fixture(); fixture.Socket.ConnectCancelled = true;
        fixture.Controller.PushToTalk(); await fixture.StateAsync(CompanionState.Unavailable);
        Assert.Contains("Sorry, I can't reach Core right now.", fixture.Audio.SpokenTexts);
        Assert.False(fixture.Audio.Active); Assert.True(fixture.Socket.Disposed);
        Assert.Single(fixture.Http.Requests, x => x.Path == CompanionRoutes.VoiceLocalRelease);
    }

    [Fact]
    public async Task Changed_privacy_epoch_and_wrong_enrolment_fail_before_admission()
    {
        using var handler = new FakeHttp(); using var core = new CoreClient(Connection() with { PrivacyEpoch = 6 }, handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => core.ClaimAsync(false, CancellationToken.None));
        Assert.Single(handler.Requests);
        using var anotherHandler = new FakeHttp { Kind = DeviceKind.AndroidCompanion };
        using var another = new CoreClient(Connection(), anotherHandler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => another.ClaimAsync(false, CancellationToken.None));
        Assert.DoesNotContain(anotherHandler.Requests, x => x.Path == CompanionRoutes.VoiceWakeCandidates);
    }

    [Fact]
    public async Task Owner_capability_confirmation_is_required_before_saving_or_claiming_a_device()
    {
        using var handler = new FakeHttp { Verified = false }; using var core = new CoreClient(Connection(), handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => core.RefreshAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => core.ClaimAsync(false, CancellationToken.None));
        Assert.DoesNotContain(handler.Requests, x => x.Path == CompanionRoutes.VoiceWakeCandidates);
    }

    [Fact]
    public void CurrentUser_DPAPI_writes_ciphertext_and_rejects_tampering()
    {
        if (!OperatingSystem.IsWindows()) return; // This project runs on the Windows CI runner.
        var directory = Path.Combine(Path.GetTempPath(), "jarvis-vault-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "connection.dpapi");
        try {
            var vault = new CredentialVault(path, new DpapiProtector()); vault.Save(Connection());
            var stored = File.ReadAllBytes(path);
            Assert.DoesNotContain(Connection().Credential, Encoding.UTF8.GetString(stored)); Assert.Equal(Connection(), vault.Read());
            stored[stored.Length / 2] ^= 0x40; File.WriteAllBytes(path, stored);
            Assert.Throws<CryptographicException>(() => vault.Read()); vault.Clear(); Assert.False(File.Exists(path));
        } finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false); }
    }

    [Theory]
    [InlineData("http://core.example/")]
    [InlineData("https://core.example/path")]
    [InlineData("https://user:secret@core.example/")]
    [InlineData("https://core.example/?key=secret")]
    [InlineData("https://127.0.0.1/")]
    public void Unsafe_addresses_are_rejected(string address) => Assert.Throws<InvalidOperationException>(() => (Connection() with { Origin = new Uri(address) }).Validate());

    [Fact]
    public void Loopback_pin_never_bypasses_name_expiry_or_digest_validation()
    {
        using var key = ECDsa.Create();
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        using var leaf = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pinned = Connection() with { Origin = new Uri("https://localhost/"), CertificateSha256 = Convert.ToHexString(SHA256.HashData(leaf.RawData)) };
        pinned.Validate(); Assert.True(pinned.AcceptCertificate(leaf, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(pinned.AcceptCertificate(leaf, SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False((pinned with { CertificateSha256 = new string('0', 64) }).AcceptCertificate(leaf, SslPolicyErrors.RemoteCertificateChainErrors));
        using var expired = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
        Assert.False((pinned with { CertificateSha256 = Convert.ToHexString(SHA256.HashData(expired.RawData)) }).AcceptCertificate(expired, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(Connection().AcceptCertificate(leaf, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public FakeHttp Http { get; } = new(); public FakeWake Wake { get; } = new(); public FakeAudio Audio { get; } = new();
        public FakeLock Locks { get; } = new(); public FakeSocket Socket { get; } = new(); public CompanionController Controller { get; }
        private readonly Channel<(CompanionState State, string Detail)> states = Channel.CreateUnbounded<(CompanionState, string)>();
        public Fixture(Connection? connection = null) {
            var value = connection ?? Connection(); Controller = new(value, new CoreClient(value, Http), Wake, Audio, Locks, () => Socket);
            Controller.Changed += (state, detail) => states.Writer.TryWrite((state, detail)); Controller.Attach();
        }
        public async Task StateAsync(CompanionState expected, bool skipFirst = false) {
            if (skipFirst) await states.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            while ((await states.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).State != expected) { }
        }
        public async Task DetailAsync(string prefix) { while (!(await states.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Detail.StartsWith(prefix, StringComparison.Ordinal)) { } }
        public async Task StartSocketAsync() { Controller.PushToTalk(); await Socket.SentAsync("authenticate"); }
        public async Task StartReadyAsync() { await StartSocketAsync(); Socket.Incoming(new { type = "ready", fence = 42, sampleRate = 24000, window = 1, locked = false, mode = "Everyday" }); await StateAsync(CompanionState.Live); }
        public ValueTask DisposeAsync() => Controller.DisposeAsync();
    }

    internal sealed record Request(string Path, string? Key, string? Epoch, string? Body);
    internal sealed class FakeHttp : HttpMessageHandler
    {
        public ConcurrentQueue<Request> Requests { get; } = new(); public int Protocol { get; set; } = CompanionVoiceProtocol.Version;
        public HttpStatusCode ClaimStatus { get; set; } = HttpStatusCode.OK; public DeviceKind Kind { get; set; } = DeviceKind.WindowsAgent;
        public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(15);
        public string OwnerName { get; set; } = "";
        public bool Verified { get; set; } = true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Enqueue(new(path, request.Headers.TryGetValues(CompanionHeaders.ApiKey, out var keys) ? keys.Single() : null,
                request.Headers.TryGetValues(CompanionHeaders.DataEpoch, out var epochs) ? epochs.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            var registered = new RegisteredDevice(new("windows-test", "Test laptop", Kind, new(true, true, false, false, [CompanionVoiceProtocol.AudioOnly]), DateTimeOffset.UtcNow), DateTimeOffset.UtcNow, Verified);
            object result = path switch {
                CompanionRoutes.PrivacyStatus => new PrivacyStatus(7, 1, false, null, null),
                CompanionRoutes.Devices => new[] { new DeviceView(registered, true, OwnerName) },
                CompanionRoutes.VoiceStatus => new AiCapabilityStatus(true, "test", "Ready", Protocol),
                CompanionRoutes.VoiceWakeCandidates => new WakeCandidateResponse(Guid.NewGuid(), new("one-use-ticket", new(Guid.NewGuid(), "windows-test", 42, DateTimeOffset.UtcNow.Add(LeaseDuration)), 300, 24000)),
                CompanionRoutes.VoiceLocalRelease => true,
                _ when path == CompanionRoutes.Heartbeat("windows-test") => registered,
                _ => throw new InvalidOperationException("Unexpected route: " + path)
            };
            return new(path == CompanionRoutes.VoiceWakeCandidates ? ClaimStatus : HttpStatusCode.OK) { Content = JsonContent.Create(result, options: new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } }) };
        }
    }
    private sealed class FakeWake : IWakeListener {
        public event Action<string, bool>? Detected; public event Action<string>? Unavailable { add { } remove { } }
        public bool Listening { get; private set; } public int Starts { get; private set; }
        public void Say(string text, bool high = true) => Detected?.Invoke(text, high);
        public Task StartAsync(CancellationToken ct) { Listening = true; Starts++; return Task.CompletedTask; }
        public Task StopAsync() { Listening = false; return Task.CompletedTask; } public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakeLock : ILockState { public bool IsLocked { get; set; } }
    private sealed class FakeAudio : ICompanionAudio {
        public event Action<byte[]>? Captured; public event Action? Failed;
        public int Starts { get; private set; } public bool Active { get; private set; } public int Spoken { get; private set; }
        public TaskCompletionSource? SpeechGate { get; set; }
        public TaskCompletionSource? StartGate { get; set; }
        private readonly Channel<bool> stopped = Channel.CreateUnbounded<bool>();
        public async Task StoppedAsync() => await stopped.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public int Played { get; private set; }
        public ConcurrentQueue<string> SpokenTexts { get; } = new();
        private readonly Channel<string> speech = Channel.CreateUnbounded<string>();
        public async Task SpokenAsync(string prefix) { while (!(await speech.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).StartsWith(prefix, StringComparison.Ordinal)) { } }
        public TaskCompletionSource SpeechStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Capture(byte[] pcm) => Captured?.Invoke(pcm); public void Fail() => Failed?.Invoke();
        public async Task StartAsync(CancellationToken ct) { Starts++; Started.TrySetResult(); if (StartGate is not null) await StartGate.Task.WaitAsync(ct); ct.ThrowIfCancellationRequested(); Active = true; }
        public void Play(string item, ReadOnlySpan<byte> pcm) => Played++; public PlaybackPosition? Interrupt() => new("item", 25);
        public void Stop() { Active = false; stopped.Writer.TryWrite(true); }
        public async Task SpeakAsync(string text, CancellationToken ct) { Spoken++; SpokenTexts.Enqueue(text); speech.Writer.TryWrite(text); SpeechStarted.TrySetResult(); if (SpeechGate is not null) await SpeechGate.Task.WaitAsync(ct); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakeSocket : IVoiceSocket {
        private readonly Channel<JsonElement> incoming = Channel.CreateUnbounded<JsonElement>(), sent = Channel.CreateUnbounded<JsonElement>();
        public ConcurrentQueue<JsonElement> Frames { get; } = new(); public int Connections { get; private set; } public bool Disposed { get; private set; }
        public bool ConnectCancelled { get; set; }
        public Task ConnectAsync(Connection connection, CancellationToken ct) { Connections++; if (ConnectCancelled) throw new OperationCanceledException("Connection deadline elapsed."); return Task.CompletedTask; }
        public void Incoming(object frame) => incoming.Writer.TryWrite(JsonSerializer.SerializeToElement(frame));
        public Task SendAsync(object frame, CancellationToken ct) { ct.ThrowIfCancellationRequested(); var json = JsonSerializer.SerializeToElement(frame); Frames.Enqueue(json); sent.Writer.TryWrite(json); return Task.CompletedTask; }
        public async Task<JsonElement> SentAsync(string type) { JsonElement frame; do { frame = await sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); } while (frame.GetProperty("type").GetString() != type); return frame; }
        public async Task<JsonElement> ReceiveAsync(CancellationToken ct) => await incoming.Reader.ReadAsync(ct);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
