using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jarvis.Contracts;

namespace Jarvis.Windows.Client;

public sealed class CoreRejectedException(HttpStatusCode status) : Exception(status == HttpStatusCode.Conflict
    ? "Another device owns audio, or the previous lease is still draining."
    : status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        ? "Core refused this device. Check its enrolment and owner confirmation."
        : "Core could not start voice. Check Core readiness and budgets.")
{
    public HttpStatusCode Status { get; } = status;
}

public sealed class CoreClient : IDisposable, IWindowsCommandCore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly Connection connection;
    private readonly HttpClient http;
    private long? epoch;
    public long? Epoch => epoch;
    public string OwnerDisplayName { get; private set; } = "";
    public CoreClient(Connection connection, HttpMessageHandler? handler = null)
    {
        connection.Validate(); this.connection = connection; epoch = connection.PrivacyEpoch;
        handler ??= new HttpClientHandler { AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => connection.AcceptCertificate(certificate, errors) };
        http = new(handler) { BaseAddress = connection.Origin, Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 131072 };
        http.DefaultRequestHeaders.Add(CompanionHeaders.ApiKey, connection.Credential);
    }

    public async Task<string> RefreshAsync(CancellationToken ct)
    {
        var privacy = await GetAsync<PrivacyStatus>(CompanionRoutes.PrivacyStatus, ct);
        if (privacy.Erased || epoch is not null && epoch != privacy.Epoch) throw new InvalidOperationException("Core privacy changed. Pair this companion again.");
        epoch = privacy.Epoch;
        var devices = await GetAsync<DeviceView[]>(CompanionRoutes.Devices, ct);
        var device = devices.Length == 1 ? devices[0].Device : throw new InvalidOperationException("Core must enrol this as its own Windows device.");
        if (device.Registration.Kind != DeviceKind.WindowsAgent || device.Registration.Capabilities.HasDisplay ||
            !device.Registration.Capabilities.Extra.Contains(CompanionVoiceProtocol.AudioOnly, StringComparer.Ordinal))
            throw new InvalidOperationException("Enrol an audio-only WindowsAgent device on Core.");
        if (!device.CapabilitiesVerified || !device.Registration.Capabilities.CanListen || !device.Registration.Capabilities.CanSpeak)
            throw new InvalidOperationException("Have the owner confirm this device's listen and speak capabilities on Core first.");
        if (connection.DeviceId is not null && device.Registration.DeviceId != connection.DeviceId)
            throw new InvalidOperationException("Core returned a different enrolled device.");
        var voice = await GetAsync<AiCapabilityStatus>(CompanionRoutes.VoiceStatus, ct);
        if (voice.VoiceProtocolVersion < CompanionVoiceProtocol.Version)
            throw new InvalidOperationException("Upgrade Core to the reviewed locked-voice protocol before using Windows voice.");
        var name = devices[0].OwnerDisplayName.Trim();
        if (name.Length > 80 || name.Any(char.IsControl)) throw new InvalidOperationException("Core returned an invalid owner display name.");
        OwnerDisplayName = name;
        return device.Registration.DeviceId;
    }

    public async Task<WakeCandidateResponse> ClaimAsync(bool locked, CancellationToken ct)
    {
        if (!connection.AllowCloudProcessing) throw new InvalidOperationException("Enable explicit live-voice consent in setup first.");
        var id = await RefreshAsync(ct);
        await HeartbeatAsync(id, ct);
        return await PostAsync<WakeCandidateResponse>(CompanionRoutes.VoiceWakeCandidates, new WakeCandidateRequest(Guid.NewGuid(), id, true, locked), ct);
    }

    public async Task HeartbeatAsync(string deviceId, CancellationToken ct) =>
        _ = await PostAsync<RegisteredDevice>(CompanionRoutes.Heartbeat(deviceId), new { }, ct);

    public async Task ReleaseAsync(AudioLease lease, CancellationToken ct) =>
        _ = await PostAsync<bool>(CompanionRoutes.VoiceLocalRelease, lease, ct);

    public async Task<DeliveredDeviceCommand?> ClaimCommandAsync(Guid requestId, CancellationToken ct)
    {
        if (epoch is null) throw new InvalidOperationException("Read Core privacy state before claiming a command.");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/device-commands/claim") { Content = JsonContent.Create(new ClaimDeviceCommandRequest(requestId), options: Json) };
        request.Headers.Add(CompanionHeaders.DataEpoch, epoch.Value.ToString(CultureInfo.InvariantCulture));
        using var response = await http.SendAsync(request, ct);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await ReadAsync<DeliveredDeviceCommand>(response, ct);
    }
    public Task<DeviceReceiptView> ReceiptAsync(Guid commandId, SubmitDeviceReceiptRequest request, CancellationToken ct) =>
        PostAsync<DeviceReceiptView>("/api/device-commands/" + commandId + "/receipts", request, ct);

    private async Task<T> GetAsync<T>(string route, CancellationToken ct)
    {
        using var response = await http.GetAsync(route, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<T> PostAsync<T>(string route, object body, CancellationToken ct)
    {
        if (epoch is null) throw new InvalidOperationException("Read Core privacy state before a write.");
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Add(CompanionHeaders.DataEpoch, epoch.Value.ToString(CultureInfo.InvariantCulture));
        using var response = await http.SendAsync(request, ct);
        return await ReadAsync<T>(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw new CoreRejectedException(response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new InvalidOperationException("Core returned an invalid response.");
    }
    public void Dispose() => http.Dispose();
}
