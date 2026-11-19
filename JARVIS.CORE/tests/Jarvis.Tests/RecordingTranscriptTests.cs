using Jarvis.Contracts;
using Jarvis.Orchestrator.Recordings;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Jarvis.Orchestrator.Intelligence;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace Jarvis.Tests;

internal sealed class RecordingTranscriberFake : IRecordingTranscriber
{
    public int Calls; public bool Fail; public List<int> Sizes = []; public Queue<string> Text = new();
    public RecordingTranscriptionPrice Price = new("synthetic-recording-transcription", .01m, new(10, 20, 10, 10, 10));
    public Func<Task<string>>? Response;
    public RecordingTranscriptionPrice Pricing() => Price;
    public async Task<string> TranscribeAsync(byte[] wave, RecordingTranscriptionPrice price, CancellationToken ct) {
        Calls++; Sizes.Add(wave.Length); if (Fail) throw new HttpRequestException("Synthetic unknown provider outcome");
        if (Response is not null) return await Response(); return Text.Count > 0 ? Text.Dequeue() : "A private recorded answer.";
    }
}
internal sealed class RecordingTranscriptRig
{
    public RecordingRig Base = new(); public RecordingTranscriberFake Provider = new(); public UsageBudget Budget; public RecordingTranscriptService Service;
    public RecordingTranscriptRig() {
        Budget = new(Base.Runtime, Base.Clock, new TestBudgetSource(Base.Clock));
        Service = new(Base.Store, Base.Files, Provider, Budget, Base.Service, Base.Clock);
    }
    public async Task<Guid> Capture(bool windows = false, int chunks = 1, bool full = false) {
        var recording = await Base.Prepare(windows); using var actor = Base.Actor(); await Base.Start(recording.Id);
        foreach (var track in recording.Tracks) for (var i = 0; i < chunks; i++)
            await Base.Chunk(recording.Id, i, RecordingRig.Wav(full || i < chunks - 1 ? 16000 * 60 : 16000), track);
        await Base.Service.CaptureAsync(new("finish", RecordingRig.Device, recording.Id, FinalCounts: recording.Tracks.ToDictionary(t => t, _ => chunks)));
        return recording.Id;
    }
    public async Task<RecordingTranscriptProgress> Quote(Guid id) => await Service.QuoteAsync(id, new((await Base.Service.ListAsync()).Single(r => r.Id == id).Revision, false));
    public static AdvanceRecordingTranscriptRequest Advance(RecordingTranscriptProgress quote, Guid? operation = null, bool confirm = true, bool reconcile = false) =>
        new(operation ?? Guid.NewGuid(), quote.QuoteId, quote.Revision, true, confirm, false, reconcile);
}
public sealed class RecordingTranscriptTests
{
    [Fact]
    public async Task A_late_capture_discontinuity_cannot_leave_a_completed_transcript_label()
    {
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient); var rig = new RecordingTranscriptRig(); var id = await rig.Capture();
        var quote = await rig.Quote(id); await rig.Service.AdvanceAsync(id, RecordingTranscriptRig.Advance(quote));
        using (rig.Base.Actor()) {
            var wave = RecordingRig.Wav(); await rig.Base.Service.CaptureAsync(new("chunk", RecordingRig.Device, id, Track: "unknown", Sequence: 0,
                Sha256: RecordingAudio.Hash(wave), AudioBase64: Convert.ToBase64String(wave), Discontinuous: true));
        }
        var changed = (await rig.Service.GetAsync(id, false))!; Assert.Equal(RecordingTranscriptStates.Partial, changed.Status); Assert.True(changed.Transcript!.Partial); Assert.Equal(1, rig.Provider.Calls);
    }
    private sealed class Wire : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls; public byte[]? Audio; public string? Model;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Assert.Equal("https://api.openai.com/v1/audio/transcriptions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("synthetic-not-live", request.Headers.Authorization!.Parameter); Calls++;
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            foreach (var part in form) if (part.Headers.ContentDisposition!.Name!.Trim('"') == "model") Model = await part.ReadAsStringAsync(ct);
            else { Assert.Equal("audio.wav", part.Headers.ContentDisposition.FileName!.Trim('"')); Audio = await part.ReadAsByteArrayAsync(ct); }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { text = "A fake provider answer." }) };
        }
    }
    [Fact]
    public async Task The_real_configured_adapter_uses_one_existing_budget_reservation_and_the_canonical_in_memory_upload()
    {
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient); var wire = new Wire();
        using var root = new JarvisApiFactory(); using var host = root.WithWebHostBuilder(b => {
            b.UseSetting("OpenAI:ApiKey", "synthetic-not-live"); b.UseSetting("OpenAI:TranscriptionModel", "gpt-transcribe"); b.UseSetting("AI:Transcription:UsdPerMinute", "0.01");
            b.ConfigureServices(s => { s.AddSingleton<IHttpClientFactory>(wire); s.AddSingleton<IRecordingAudioFiles>(new RecordingFilesFake()); });
        });
        var services = host.Services; const string device = "wire-recording";
        await services.GetRequiredService<DeviceService>().RegisterAsync(new(device, "Phone", DeviceKind.AndroidCompanion, new(true, true, false, false, [])));
        var credential = await services.GetRequiredService<DeviceCredentials>().IssueAsync(device, default); var id = Guid.NewGuid(); var recording = services.GetRequiredService<RecordingService>();
        await recording.PrepareAsync(new(id, device, "Wire fixture", true, true, Locked: false)); var wave = RecordingRig.Wav();
        using (DeviceCredentialContext.Enter(new("device:" + device, device, PrivacyData.Hash(credential.Secret)))) {
            await recording.CaptureAsync(new("start", device, id, false, true, true));
            await recording.CaptureAsync(new("chunk", device, id, Track: "unknown", Sequence: 0, Sha256: RecordingAudio.Hash(wave), AudioBase64: Convert.ToBase64String(wave)));
            await recording.CaptureAsync(new("finish", device, id, FinalCounts: new Dictionary<string, int> { ["unknown"] = 1 }));
        }
        var service = services.GetRequiredService<RecordingTranscriptService>(); var quote = await service.QuoteAsync(id, new((await recording.ListAsync()).Single().Revision, false));
        await service.AdvanceAsync(id, RecordingTranscriptRig.Advance(quote)); Assert.Equal(1, wire.Calls); Assert.Equal("gpt-transcribe", wire.Model); Assert.Equal(wave, wire.Audio);
        var charge = Assert.Single(await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.UsageReservations.Values.ToArray())); Assert.Equal(quote.MaximumUsd, charge.ReservedUsd);
    }
    [Fact]
    public async Task Http_transcription_requires_an_unlocked_owner_and_cannot_be_requested_by_the_audio_companion()
    {
        var provider = new RecordingTranscriberFake(); var files = new RecordingFilesFake(); using var root = new JarvisApiFactory();
        using var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => { s.AddSingleton<IRecordingTranscriber>(provider); s.AddSingleton<IRecordingAudioFiles>(files); }));
        using var owner = host.CreateClient(); owner.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(owner);
        const string device = "record-transcript-http"; await host.Services.GetRequiredService<DeviceService>().RegisterAsync(new(device, "Phone", DeviceKind.AndroidCompanion, new(true, true, false, false, [])));
        var credential = await host.Services.GetRequiredService<DeviceCredentials>().IssueAsync(device, default); using var native = BrowserEnrollmentTests.Browser(host, credential);
        var id = Guid.NewGuid(); using var prepared = await owner.PostAsJsonAsync("/api/recordings", new PrepareRecordingRequest(id, device, "Private HTTP interview", true, true, Locked: false)); prepared.EnsureSuccessStatusCode();
        using var start = await native.PostAsJsonAsync(RecordingProtocol.Capture, new RecordingCaptureRequest("start", device, id, false, true, true)); start.EnsureSuccessStatusCode();
        var wave = RecordingRig.Wav(); using var chunk = await native.PostAsJsonAsync(RecordingProtocol.Capture, new RecordingCaptureRequest("chunk", device, id, Track: "unknown", Sequence: 0, Sha256: RecordingAudio.Hash(wave), AudioBase64: Convert.ToBase64String(wave))); chunk.EnsureSuccessStatusCode();
        using var finish = await native.PostAsJsonAsync(RecordingProtocol.Capture, new RecordingCaptureRequest("finish", device, id, FinalCounts: new Dictionary<string, int> { ["unknown"] = 1 })); finish.EnsureSuccessStatusCode();
        var recording = Assert.Single((await owner.GetFromJsonAsync<InterviewRecording[]>("/api/recordings?locked=false"))!);
        using var forbidden = await native.PostAsJsonAsync($"/api/recordings/{id}/transcription/quote", new QuoteRecordingTranscriptRequest(recording.Revision, false)); Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var locked = await owner.GetAsync($"/api/recordings/{id}/transcription?locked=true"); Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
        var quoteResponse = await owner.PostAsJsonAsync($"/api/recordings/{id}/transcription/quote", new QuoteRecordingTranscriptRequest(recording.Revision, false)); quoteResponse.EnsureSuccessStatusCode();
        var quote = (await quoteResponse.Content.ReadFromJsonAsync<RecordingTranscriptProgress>())!; Assert.Equal(0, provider.Calls);
        using var transcribed = await owner.PostAsJsonAsync($"/api/recordings/{id}/transcription/advance", RecordingTranscriptRig.Advance(quote)); transcribed.EnsureSuccessStatusCode(); Assert.Equal(1, provider.Calls);
        using var privateView = await native.GetAsync($"/api/recordings/{id}/transcription?locked=false"); Assert.Equal(HttpStatusCode.Forbidden, privateView.StatusCode);
    }
    [Fact]
    public async Task Quote_is_unpaid_requires_explicit_confirmation_and_matches_each_budget_reservation()
    {
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var rig = new RecordingTranscriptRig(); var id = await rig.Capture(); var quote = await rig.Quote(id);
        Assert.Equal(0, rig.Provider.Calls); Assert.Empty(await rig.Base.Runtime.ReadAsync(s => s.UsageReservations.Values.ToArray()));
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.AdvanceAsync(id, RecordingTranscriptRig.Advance(quote, confirm: false)));
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.AdvanceAsync(id, RecordingTranscriptRig.Advance(quote) with { ConfirmCloudProcessing = false }));
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.AdvanceAsync(id, RecordingTranscriptRig.Advance(quote) with { Locked = true }));
        var request = RecordingTranscriptRig.Advance(quote); var result = await rig.Service.AdvanceAsync(id, request);
        Assert.Equal(RecordingTranscriptStates.Complete, result.Status); Assert.Equal(1, rig.Provider.Calls);
        var reservation = Assert.Single(await rig.Base.Runtime.ReadAsync(s => s.UsageReservations.Values.ToArray()));
        Assert.Equal(quote.MaximumUsd, reservation.ReservedUsd); Assert.Equal(quote.Pieces[0].MaximumGbp, rig.Budget.EstimateGbp(reservation.ReservedUsd));
        Assert.Equal(ProviderIds.OpenAi, reservation.Provider); Assert.Equal(UsageLane.Interactive, reservation.Lane);
        Assert.Single((await rig.Service.AdvanceAsync(id, request)).Pieces); Assert.Equal(1, rig.Provider.Calls);
        Assert.Equal("interview", result.Transcript!.Kind); Assert.False(result.Transcript.Partial); Assert.Equal("unknown", Assert.Single(result.Transcript.Segments).Speaker);
        Assert.DoesNotContain("A private recorded answer", await rig.Base.Store.ReadAsync(s => System.Text.Json.JsonSerializer.Serialize(s)));
    }
    [Fact]
    public async Task An_archive_larger_than_ten_MB_is_transcribed_as_bounded_capture_pieces()
    {
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var rig = new RecordingTranscriptRig(); var id = await rig.Capture(chunks: 6, full: true); var progress = await rig.Quote(id);
        Assert.True((await rig.Base.Service.ListAsync()).Single().AudioBytes > 10_000_000); Assert.Equal(6, progress.Pieces.Count);
        while (progress.Status is not (RecordingTranscriptStates.Complete or RecordingTranscriptStates.Partial)) progress = await rig.Service.AdvanceAsync(id, RecordingTranscriptRig.Advance(progress));
        Assert.Equal(6, rig.Provider.Calls); Assert.All(rig.Provider.Sizes, size => Assert.InRange(size, 1, 10_000_000));
        Assert.Equal(progress.MaximumUsd, await rig.Base.Runtime.ReadAsync(s => s.UsageReservations.Values.Sum(r => r.ReservedUsd)));
    }
    [Fact]
    public async Task Laptop_tracks_merge_by_capture_timestamp_and_phone_speaker_labels_remain_owner_editable()
    {
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var rig = new RecordingTranscriptRig(); var id = await rig.Capture(windows: true); rig.Provider.Text.Enqueue("Why retries?"); rig.Provider.Text.Enqueue("For transient failures.");
        var progress = await rig.Quote(id); progress = await rig.Service.AdvanceAsync(id, RecordingTranscriptRig.Advance(progress));
        Assert.True(progress.Transcript!.Partial); progress = await rig.Service.AdvanceAsync(id, RecordingTranscriptRig.Advance(progress));
        Assert.Equal(new[] { "interviewer", "user" }, progress.Transcript!.Segments.Select(s => s.Speaker));
        Assert.Equal(new[] { 0d, 0d }, progress.Transcript.Segments.Select(s => s.StartSeconds));
        Assert.Contains("not word timing", progress.Transcript.TimingSource); Assert.Null(ImportService.Review(progress.Transcript).UserPausesAtLeastOneSecond);
        var first = progress.Transcript.Segments[0]; var labelled = await rig.Service.RelabelAsync(id, new(progress.Transcript.Revision, first.RecordingPieceId!.Value, "unknown", false));
        Assert.Equal("unknown", labelled.Segments[0].Speaker); Assert.Equal("interviewer", labelled.Segments[0].CaptureTrack);
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.RelabelAsync(id, new(progress.Transcript.Revision, first.RecordingPieceId!.Value, "user", false)));
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.RelabelAsync(id, new(labelled.Revision, first.RecordingPieceId!.Value, "user", true)));
    }
    [Fact]
    public async Task An_unknown_provider_result_blocks_all_paid_work_and_continuation_until_verified_reconciliation_without_replaying_the_failed_piece()
    {
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var rig = new RecordingTranscriptRig(); var id = await rig.Capture(chunks: 2); var quote = await rig.Quote(id); rig.Provider.Fail = true;
        var failedRequest = RecordingTranscriptRig.Advance(quote);
        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Service.AdvanceAsync(id, failedRequest));
        var uncertain = (await rig.Service.GetAsync(id, false))!; Assert.Equal(RecordingTranscriptStates.Uncertain, uncertain.Status);
        var charge = Assert.Single(await rig.Base.Runtime.ReadAsync(s => s.UsageReservations.Values.ToArray())); Assert.True(charge.ReconciliationRequired);
        await Assert.ThrowsAsync<DomainException>(() => rig.Budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "synthetic", .01m, rig.Provider.Price.Limits, default));
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.AdvanceAsync(id, RecordingTranscriptRig.Advance(uncertain)));
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.AdvanceAsync(id, RecordingTranscriptRig.Advance(uncertain, reconcile: true)));
        Assert.Equal(1, rig.Provider.Calls); rig.Base.Clock.Now = rig.Base.Clock.Now.AddMinutes(11);
        await new UsageReconciler(rig.Base.Runtime, rig.Base.Clock).ReconcileAsync(TestBudgetSource.Owner, charge.Id,
            new(Guid.NewGuid(), .01m, "synthetic verified billing", "CONFIRM VERIFIED CHARGE"));
        rig.Provider.Fail = false; var result = await rig.Service.AdvanceAsync(id, RecordingTranscriptRig.Advance(uncertain, reconcile: true));
        Assert.Equal(2, rig.Provider.Calls); Assert.Equal(RecordingTranscriptStates.Partial, result.Status); Assert.True(result.Transcript!.Partial);
        Assert.Equal(RecordingTranscriptStates.Failed, result.Pieces[0].Status); Assert.Equal(60, Assert.Single(result.Transcript.Segments).StartSeconds);
        await rig.Service.AdvanceAsync(id, failedRequest); Assert.Equal(2, rig.Provider.Calls);
    }
    [Fact]
    public async Task Expired_quotes_and_agent_origins_cannot_start_paid_transcription()
    {
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var rig = new RecordingTranscriptRig(); var id = await rig.Capture(); var quote = await rig.Quote(id);
        using (CoreInvocationContext.Enter(InvocationOrigin.Agent)) await Assert.ThrowsAsync<DomainException>(() => rig.Service.AdvanceAsync(id, RecordingTranscriptRig.Advance(quote)));
        rig.Base.Clock.Now = rig.Base.Clock.Now.AddMinutes(11);
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.AdvanceAsync(id, RecordingTranscriptRig.Advance(quote))); Assert.Equal(0, rig.Provider.Calls);
    }
    [Fact]
    public async Task Deletion_during_provider_dispatch_cannot_recreate_the_recording_or_imported_text()
    {
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var rig = new RecordingTranscriptRig(); var id = await rig.Capture(); var quote = await rig.Quote(id);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider.Response = () => { entered.TrySetResult(); return release.Task; };
        var operation = rig.Service.AdvanceAsync(id, RecordingTranscriptRig.Advance(quote)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var current = (await rig.Base.Service.ListAsync()).Single(); await rig.Base.Service.DeleteAsync(id, current.Revision);
        release.TrySetResult("THIRD PARTY RESPONSE MUST NOT RETURN"); await Assert.ThrowsAsync<DomainException>(() => operation);
        Assert.Empty(await rig.Base.Service.ListAsync()); Assert.Empty(await rig.Base.Runtime.ReadAsync(s => s.ImportedDocuments.Values.ToArray()));
        Assert.Single(await rig.Base.Runtime.ReadAsync(s => s.UsageReservations.Values.ToArray()));
    }
    [Fact]
    public async Task Deleting_audio_after_transcription_keeps_the_existing_import_and_relabelling()
    {
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var rig = new RecordingTranscriptRig(); var id = await rig.Capture();
        await rig.Base.Store.UpdateAsync((s, _) => { var r = s.Sessions[id]; s.Sessions[id] = r with { View = r.View with { DeleteAudioAfterTranscription = true } }; return Task.FromResult(true); });
        var quote = await rig.Quote(id); var command = RecordingTranscriptRig.Advance(quote); var result = await rig.Service.AdvanceAsync(id, command);
        Assert.True((await rig.Base.Service.ListAsync()).Single().AudioDeleted); Assert.Empty(rig.Base.Files.Blobs); Assert.NotEmpty(result.Transcript!.Text);
        Assert.Equal(result.Transcript.Id, (await rig.Service.AdvanceAsync(id, command)).Transcript!.Id); Assert.Equal(1, rig.Provider.Calls);
        var segment = result.Transcript.Segments.Single(); Assert.Equal("user", (await rig.Service.RelabelAsync(id, new(result.Transcript.Revision, segment.RecordingPieceId!.Value, "user", false))).Segments.Single().Speaker);
    }
    [Fact]
    public async Task Interview_imports_cannot_become_memory_or_agent_context()
    {
        using var host = new JarvisApiFactory(); using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var imported = await host.Services.GetRequiredService<ImportService>().ImportAsync(new(Guid.NewGuid(), "interview", "Private interview", "A third party question?", true, true));
        var memory = host.Services.GetRequiredService<ContextMemoryService>(); var now = host.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        await Assert.ThrowsAsync<DomainException>(() => memory.SaveAsync("general", new(Guid.NewGuid(), "import", imported.Id, null, now.AddDays(1), Length: 10)));
        Assert.False(await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => AgentRuntime.SourceCurrent(s, new("import", imported.Id, imported.ContentHash))));
    }
}
