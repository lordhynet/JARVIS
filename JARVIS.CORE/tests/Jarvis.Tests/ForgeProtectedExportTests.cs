using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Api;
using Jarvis.Api.Security;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jarvis.Tests;

internal static class ForgeExportFixture
{
    public static readonly DateTimeOffset At = ForgeProtectedStorageFixture.At;
    public static readonly string OldKey = Convert.ToBase64String(Enumerable.Repeat((byte)41, 32).ToArray());
    public static readonly string NewKey = Convert.ToBase64String(Enumerable.Repeat((byte)73, 32).ToArray());
    public static ProtectedRecordProtector Protector(string? active = "new", bool old = true) => new(() => new() {
        ActiveKeyId = active, Keys = old ? new Dictionary<string, string> { ["old"] = OldKey, ["new"] = NewKey } : new() { ["new"] = NewKey }
    });
    public static void Configure(IWebHostBuilder builder) {
        builder.UseSetting(ConfigKeys.ProtectedRecords + ":ActiveKeyId", "new");
        builder.UseSetting(ConfigKeys.ProtectedRecords + ":Keys:old", OldKey);
        builder.UseSetting(ConfigKeys.ProtectedRecords + ":Keys:new", NewKey);
        builder.ConfigureTestServices(s => s.RemoveAll<IHostedService>());
    }
    public static ForgeProtectedRecord Record<T>(ForgeProgramme programme, ForgeProtectedKind kind, Guid id, T payload,
        int revision = 1, Guid? request = null) where T : class {
        var protector = Protector(revision == 1 ? "old" : "new");
        var row = new ForgeProtectedRecord(kind, programme.OwnerScope, programme.Id, id, 0, revision, request ?? Guid.NewGuid(), revision - 1,
            At, new(1, "", [], [], []), new(1, "", []));
        return row with { Envelope = ForgeProtectedJson.Protect(protector, row.ValueContext, payload),
            RetryProof = protector.CreateRetryProof(row.RetryContext, ReadOnlySpan<byte>.Empty) };
    }
    public static ForgeCheckInPayload CheckIn() => new(1, "forge.tech", [
        new("historic", ForgeCheckInObservationMode.OwnerReported, 100000000000000000000m, null, null, "legacy-unit", null, null, null),
        new("unknown", ForgeCheckInObservationMode.OwnerReported, null, null, null, null, null, null, null),
        new("zero", ForgeCheckInObservationMode.OwnerReported, 0, null, null, "count", null, null, null),
        new("false", ForgeCheckInObservationMode.OwnerReported, null, null, false, null, null, null, null),
        new("reference", ForgeCheckInObservationMode.CanonicalReference, null, null, null, null, new("learning", Guid.NewGuid(), "historic-version"), null, null)
    ], true, At.AddTicks(-7).ToOffset(TimeSpan.FromHours(5.5)), "synthetic-private-export-note");

    public static async Task<ForgeProtectedRecord[]> Seed(IServiceProvider services) {
        var programme = ForgeProtectedStorageFixture.Programme(); var id = Guid.NewGuid(); var request = Guid.NewGuid();
        var first = CheckIn(); var day = new DateOnly(2027, 3, 28);
        var capacity = new ForgeCapacityPayload(1, day, "Europe/London", null, null, null, true, null, null, "synthetic-capacity-history");
        var rows = new[] { Record(programme, ForgeProtectedKind.CheckIn, id, first, request: request),
            Record(programme, ForgeProtectedKind.CheckIn, id, first with { Note = "corrected-private-note", Observations = [] }, 2),
            Record(programme, ForgeProtectedKind.Capacity, id, capacity, request: request),
            Record(programme, ForgeProtectedKind.Capacity, id, capacity with { NetUsableMinutes = 0, BufferMinutes = 0, FixedBlocks = [] }, 2) };
        await services.GetRequiredService<ICoreTransaction>().ExecuteAsync(async w => {
            await w.SaveForgeProgrammeAsync(programme);
            foreach (var row in rows) await w.AppendForgeProtectedRecordAsync(row);
            return true;
        });
        return rows;
    }

    public static void AssertComplete(CoreDataExport export, ForgeProtectedRecord[] rows) {
        Assert.Equal(4, export.ForgeProtectedRecords!.Count);
        Assert.Equal(ForgeProtectedRevisionExport.Sensitivity, export.ExportSensitivity);
        Assert.Equal(ForgeProtectedRevisionExport.StorageAdvice, export.ExportStorageAdvice);
        foreach (var row in rows) {
            var item = Assert.Single(export.ForgeProtectedRecords, x => x.Kind == (row.Kind == ForgeProtectedKind.CheckIn ? "check-in" : "capacity") && x.Revision == row.Revision);
            Assert.Equal(row.Id, item.Id); Assert.Equal(row.OwnerScope, item.OwnerScope); Assert.Equal(row.ProgrammeId, item.ProgrammeId);
            Assert.Equal(row.Epoch, item.Epoch); Assert.Equal(row.RequestId, item.RequestId); Assert.Equal(row.ExpectedRevision, item.ExpectedRevision); Assert.Equal(row.CreatedAt, item.CreatedAt);
        }
        var checkIn = export.ForgeProtectedRecords.Single(x => x.Kind == "check-in" && x.Revision == 1).Payload.Deserialize<ForgeCheckInPayload>()!;
        var observations = checkIn.Observations!;
        Assert.Equal(100000000000000000000m, observations.Single(x => x.MetricKey == "historic").NumericValue);
        Assert.Equal("legacy-unit", observations.Single(x => x.MetricKey == "historic").Unit);
        Assert.Null(observations.Single(x => x.MetricKey == "unknown").NumericValue);
        Assert.Equal(0, observations.Single(x => x.MetricKey == "zero").NumericValue);
        Assert.False(observations.Single(x => x.MetricKey == "false").BooleanValue);
        Assert.Equal("historic-version", observations.Single(x => x.MetricKey == "reference").Source!.Version);
        Assert.Equal(CheckIn().ObservedAt!.Value.Ticks, checkIn.ObservedAt!.Value.Ticks); Assert.Equal(TimeSpan.FromHours(5.5), checkIn.ObservedAt.Value.Offset);
        var unknown = export.ForgeProtectedRecords.Single(x => x.Kind == "capacity" && x.Revision == 1).Payload.Deserialize<ForgeCapacityPayload>()!;
        var known = export.ForgeProtectedRecords.Single(x => x.Kind == "capacity" && x.Revision == 2).Payload.Deserialize<ForgeCapacityPayload>()!;
        Assert.Null(unknown.NetUsableMinutes); Assert.Null(unknown.BufferMinutes); Assert.Null(unknown.FixedBlocks);
        Assert.Equal(0, known.NetUsableMinutes); Assert.Equal(0, known.BufferMinutes); Assert.Empty(known.FixedBlocks!);
        var json = JsonSerializer.Serialize(export);
        Assert.DoesNotContain(OldKey, json); Assert.DoesNotContain(NewKey, json); Assert.DoesNotContain("RetryProof", json); Assert.DoesNotContain("Ciphertext", json);
    }
}

public sealed class ForgeProtectedExportTests
{
    [Fact]
    public void ForgeProtected_export_bounds_count_and_padded_plaintext_before_decryption() {
        ForgeProtectedJson.RequireExportSize(1024, 32L * 1024 * 1024);
        Assert.Equal(413, Assert.Throws<DomainException>(() => ForgeProtectedJson.RequireExportSize(1025, 0)).StatusCode);
        Assert.Equal(413, Assert.Throws<DomainException>(() => ForgeProtectedJson.RequireExportSize(512, 32L * 1024 * 1024 + 1)).StatusCode);
        var row = ForgeExportFixture.Record(ForgeProtectedStorageFixture.Programme(), ForgeProtectedKind.CheckIn, Guid.NewGuid(), ForgeExportFixture.CheckIn());
        var full = row with { Envelope = row.Envelope with { Ciphertext = new byte[IProtectedRecordProtector.MaximumPlaintextBytes] } };
        Assert.Equal(413, Assert.Throws<DomainException>(() => ForgeProtectedJson.ExportRevisions(null, Enumerable.Repeat(full, 513).ToArray(), 0)).StatusCode);
        Assert.Equal(413, Assert.Throws<DomainException>(() => ForgeProtectedJson.ExportRevisions(null, Enumerable.Repeat(row, 1025).ToArray(), 0)).StatusCode);
    }
    [Fact]
    public async Task ForgeProtected_export_memory_includes_all_revisions_without_mutation_and_requires_retained_keys() {
        using var root = new ForgeApiFactory(); using var host = root.WithWebHostBuilder(ForgeExportFixture.Configure);
        var rows = await ForgeExportFixture.Seed(host.Services); var privacy = host.Services.GetRequiredService<IPrivacyStore>();
        var before = await host.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => JsonSerializer.Serialize(s));
        var export = await privacy.ExportAsync(ForgeExportFixture.At); ForgeExportFixture.AssertComplete(export, rows);
        Assert.Equal(before, await host.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => JsonSerializer.Serialize(s)));
        Assert.DoesNotContain("synthetic-private-export-note", before);
        var config = host.Services.GetRequiredService<IConfiguration>();
        config[ConfigKeys.ProtectedRecords + ":ActiveKeyId"] = ""; // retained keys alone suffice for export
        ForgeExportFixture.AssertComplete(await privacy.ExportAsync(ForgeExportFixture.At), rows);
        config[ConfigKeys.ProtectedRecords + ":Keys:old"] = "";
        Assert.Equal(503, (await Assert.ThrowsAsync<DomainException>(() => privacy.ExportAsync(ForgeExportFixture.At))).StatusCode);
        using (PrivacyContext.Enter(1)) Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => privacy.ExportAsync(ForgeExportFixture.At))).StatusCode);
        var status = await privacy.StatusAsync(); await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), new string('a', 64), ForgeExportFixture.At);
        Assert.Null((await privacy.ExportAsync(ForgeExportFixture.At)).ForgeProtectedRecords);
    }

    [Theory]
    [InlineData("tamper")]
    [InlineData("context")]
    [InlineData("schema")]
    [InlineData("unknown")]
    public void ForgeProtected_export_rejects_the_entire_batch_on_corruption_or_unrecognized_payload(string kind) {
        var programme = ForgeProtectedStorageFixture.Programme(); var good = ForgeExportFixture.Record(programme, ForgeProtectedKind.CheckIn, Guid.NewGuid(), ForgeExportFixture.CheckIn());
        var bad = kind switch {
            "schema" => ForgeExportFixture.Record(programme, ForgeProtectedKind.CheckIn, good.Id, ForgeExportFixture.CheckIn() with { Version = 2 }, 2),
            "unknown" => ForgeExportFixture.Record(programme, ForgeProtectedKind.Capacity, good.Id, new { Version = 1, UnknownPrivateField = "must-not-disappear" }),
            "context" => good with { Id = Guid.NewGuid() }, _ => good.CopyCiphertext()
        };
        if (kind == "tamper") bad.Envelope.Tag[0] ^= 1;
        Assert.Equal(409, Assert.Throws<DomainException>(() => ForgeProtectedJson.ExportRevisions(ForgeExportFixture.Protector(), [good, bad], 0)).StatusCode);
    }

    [Fact]
    public async Task ForgeProtected_export_http_enforces_owner_and_fresh_browser_scope_without_partial_attachments() {
        var clock = new ForgeClock(); using var root = new ForgeApiFactory(clock: clock);
        using var host = root.WithWebHostBuilder(ForgeExportFixture.Configure); var rows = await ForgeExportFixture.Seed(host.Services);
        using var shared = host.CreateClient(); shared.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        using var denied = await shared.GetAsync("/api/privacy/export"); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("synthetic-private-export-note", await denied.Content.ReadAsStringAsync());
        await host.Services.GetRequiredService<DeviceService>().RegisterAsync(new("export-phone", "Synthetic companion", DeviceKind.AndroidCompanion, new(true, true, false, false, [])));
        var phoneCredential = await host.Services.GetRequiredService<DeviceCredentials>().IssueAsync("export-phone", default);
        using var phone = host.CreateClient(); phone.DefaultRequestHeaders.Add("X-Jarvis-Key", phoneCredential.Secret); TestOwnerManagement.Authorize(phone);
        using var companionDenied = await phone.GetAsync("/api/privacy/export"); Assert.Equal(HttpStatusCode.Forbidden, companionDenied.StatusCode);
        using var owner = host.CreateClient(); owner.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(owner);
        using var ok = await owner.GetAsync("/api/privacy/export"); ok.EnsureSuccessStatusCode(); Assert.True(ok.Headers.CacheControl!.NoStore);
        ForgeExportFixture.AssertComplete((await ok.Content.ReadFromJsonAsync<CoreDataExport>(LifeScenarioTests.Json))!, rows);
        var credential = await BrowserEnrollmentTests.Enrol(host); using var browser = BrowserEnrollmentTests.Browser(host, credential);
        using var raw = await browser.GetAsync("/api/privacy/export"); Assert.Equal(HttpStatusCode.Forbidden, raw.StatusCode);
        var wrong = await BrowserEnrollmentTests.Elevate(browser, "device.manage"); browser.DefaultRequestHeaders.Add(BrowserOwnerAccess.ElevationHeader, wrong.Token);
        using var scopeDenied = await browser.GetAsync("/api/privacy/export"); Assert.Equal(HttpStatusCode.Forbidden, scopeDenied.StatusCode);
        browser.DefaultRequestHeaders.Remove(BrowserOwnerAccess.ElevationHeader);
        var elevation = await BrowserEnrollmentTests.Elevate(browser, "privacy.manage"); browser.DefaultRequestHeaders.Add(BrowserOwnerAccess.ElevationHeader, elevation.Token);
        using var allowed = await browser.GetAsync("/api/privacy/export"); allowed.EnsureSuccessStatusCode();
        clock.Now = elevation.ExpiresAt;
        using var expired = await browser.GetAsync("/api/privacy/export"); Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        host.Services.GetRequiredService<IConfiguration>()[ConfigKeys.ProtectedRecords + ":Keys:old"] = "";
        using var failed = await owner.GetAsync("/api/privacy/export"); Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        var error = await failed.Content.ReadAsStringAsync(); Assert.DoesNotContain("synthetic-private-export-note", error); Assert.DoesNotContain("forgeProtectedRecords", error);
        Assert.DoesNotContain(ForgeExportFixture.NewKey, error);
    }

    [Fact]
    public async Task ForgeProtected_export_empty_legacy_shape_omits_the_new_field_without_any_key() {
        using var host = new ForgeApiFactory(); var export = await host.Services.GetRequiredService<IPrivacyStore>().ExportAsync(ForgeExportFixture.At);
        Assert.Null(export.ForgeProtectedRecords);
        Assert.Null(export.ExportSensitivity); Assert.Null(export.ExportStorageAdvice);
        Assert.DoesNotContain("ForgeProtectedRecords", JsonSerializer.Serialize(export));
    }
}
