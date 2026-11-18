using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Contracts;

namespace Jarvis.Windows.Client;

public sealed record WindowsActionResult(bool Accepted, string Detail, IReadOnlyList<string>? Names = null);
public interface IWindowsActionPlatform { Task<WindowsActionResult> ExecuteAsync(string capability, WindowsActionArguments arguments, CancellationToken ct); }
public interface IWindowsCommandCore
{
    Task<DeviceReceiptView> ReceiptAsync(Guid commandId, SubmitDeviceReceiptRequest request, CancellationToken ct);
}

public sealed record WindowsCommandJournalEntry(Guid Id, Guid Dispatch, DateTimeOffset ExpiresAt, Guid AckId, Guid FinalId,
    WindowsActionResult? Result = null, bool Reported = false);
public sealed record WindowsCommandJournalState(string Binding, IReadOnlyList<WindowsCommandJournalEntry> Entries);

public sealed class WindowsCommandJournal(string path, string binding, ICredentialProtector protector)
{
    private WindowsCommandJournalState? state;
    private WindowsCommandJournalState Read()
    {
        if (state is not null) return state;
        if (!File.Exists(path)) return state = new(binding, []);
        if (new FileInfo(path).Length > 4_000_000) throw new InvalidOperationException("Command journal exceeded its bound.");
        var plain = protector.Unprotect(File.ReadAllBytes(path));
        try { var loaded = JsonSerializer.Deserialize<WindowsCommandJournalState>(plain) ?? throw new InvalidOperationException("Command journal is invalid.");
            if (loaded.Binding != binding || loaded.Entries.Count > 2048 || loaded.Entries.Select(e => e.Id).Distinct().Count() != loaded.Entries.Count)
                throw new InvalidOperationException("Command journal belongs to another enrolment or is invalid.");
            return state = loaded;
        } finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public IReadOnlyList<WindowsCommandJournalEntry> Entries => Read().Entries;
    public WindowsCommandJournalEntry? Begin(DeliveredDeviceCommand command)
    {
        var current = Read(); if (current.Entries.Any(e => e.Id == command.Id)) return null;
        if (current.Entries.Count >= 2048) throw new InvalidOperationException("Command journal is full; owner maintenance is required.");
        var entry = new WindowsCommandJournalEntry(command.Id, command.DispatchRequestId, command.ExpiresAt, Guid.NewGuid(), Guid.NewGuid());
        Save(current with { Entries = [..current.Entries, entry] }); return entry;
    }
    public WindowsCommandJournalEntry Finish(WindowsCommandJournalEntry entry, WindowsActionResult result)
    {
        var updated = entry with { Result = result }; Save(Read() with { Entries = Read().Entries.Select(e => e.Id == entry.Id ? updated : e).ToArray() }); return updated;
    }
    public void Reported(WindowsCommandJournalEntry entry) => Save(Read() with { Entries = Read().Entries.Select(e => e.Id == entry.Id ? e with { Reported = true } : e).ToArray() });
    private void Save(WindowsCommandJournalState value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try {
            var encrypted = protector.Protect(plain); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, path, overwrite: true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            state = value;
        } finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

public sealed class WindowsCommandExecutor(string deviceId, string credential, IWindowsCommandCore core, IWindowsActionPlatform platform,
    ILockState locks, WindowsCommandJournal journal, TimeProvider clock)
{
    private readonly SemaphoreSlim serial = new(1, 1);
    public async Task<WindowsActionResult> ExecuteAsync(DeliveredDeviceCommand command, CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        try {
            var now = clock.GetUtcNow();
            if (!DeviceCommandProof.Verify(command, deviceId, credential) || command.Id == Guid.Empty || command.DispatchRequestId == Guid.Empty ||
                command.CapabilityVersion != WindowsCommandProtocol.Version || command.ExpiresAt <= now || command.DispatchedAt > now.AddSeconds(30) ||
                command.ExpiresAt - command.DispatchedAt > TimeSpan.FromMinutes(5)) {
                var refused = new WindowsActionResult(false, "Unverified, expired or invalid command; no action was executed.");
                await DirectFailure(command, refused, ct); return refused;
            }
            WindowsCommandJournalEntry? entry;
            try { entry = journal.Begin(command); }
            catch { var refused = new WindowsActionResult(false, "Protected replay state is unavailable; no action was executed."); await DirectFailure(command, refused, ct); return refused; }
            if (entry is null) return new(false, "Replayed command ID refused; the original receipt remains authoritative.");
            WindowsActionResult result;
            try {
                var arguments = WindowsCommandProtocol.Parse(command.CapabilityId, command.ArgumentsJson);
                if (locks.IsLocked && !WindowsCommandProtocol.AllowedLocked(command.CapabilityId)) throw new InvalidOperationException("This capability is unavailable while locked.");
                await Acknowledge(entry, ct); // Core rechecks the live grant, command and dispatch fence here.
                if (clock.GetUtcNow() >= command.ExpiresAt || locks.IsLocked && !WindowsCommandProtocol.AllowedLocked(command.CapabilityId))
                    throw new InvalidOperationException("Command expired or the workstation locked before execution.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(command.ExpiresAt - clock.GetUtcNow());
                result = await platform.ExecuteAsync(command.CapabilityId, arguments, deadline.Token);
                if (result.Detail.Length > 1000 || result.Names is { Count: > 20 } || result.Names?.Any(n => n.Length > 120 || n.IndexOfAny(['\\', '/', ':']) >= 0) == true)
                    result = new(false, "Windows result exceeded the receipt schema; outcome requires review.");
            }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested) {
                result = new(false, "Windows did not verify this action. No automatic replay; inspect the device before retrying.");
            }
            entry = journal.Finish(entry, result);
            await Report(entry, ct); return result;
        } finally { serial.Release(); }
    }
    public async Task FlushAsync(CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        try { foreach (var saved in journal.Entries.Where(e => !e.Reported).ToArray()) {
            var entry = saved.Result is null ? journal.Finish(saved, new(false, "Previous execution outcome is unknown after interruption. Automatic replay was refused.")) : saved;
            await Report(entry, ct); // Reporting a known/unknown outcome never authorizes execution.
        } } finally { serial.Release(); }
    }
    private async Task Acknowledge(WindowsCommandJournalEntry entry, CancellationToken ct)
    {
        var receipt = await core.ReceiptAsync(entry.Id, Request(entry.Id, entry.AckId, entry.Dispatch, DeviceReceiptKind.Acknowledged, new(true, "Windows received the authenticated command.")), ct);
        if (receipt.Late || receipt.Kind != DeviceReceiptKind.Acknowledged || receipt.CommandId != entry.Id || receipt.DispatchRequestId != entry.Dispatch || receipt.Id != entry.AckId)
            throw new InvalidOperationException("Core refused current dispatch acknowledgement.");
    }
    private async Task Report(WindowsCommandJournalEntry entry, CancellationToken ct)
    {
        var result = entry.Result!; var kind = result.Accepted ? DeviceReceiptKind.Succeeded : DeviceReceiptKind.Failed;
        var receipt = await core.ReceiptAsync(entry.Id, Request(entry.Id, entry.FinalId, entry.Dispatch,
            result.Accepted ? DeviceReceiptKind.Succeeded : DeviceReceiptKind.Failed, result), ct);
        if (receipt.CommandId != entry.Id || receipt.DispatchRequestId != entry.Dispatch || receipt.Id != entry.FinalId || receipt.Kind != kind)
            throw new InvalidOperationException("Core returned a mismatched command receipt.");
        journal.Reported(entry);
    }
    private async Task DirectFailure(DeliveredDeviceCommand command, WindowsActionResult result, CancellationToken ct) =>
        _ = await core.ReceiptAsync(command.Id, Request(command.Id, Guid.NewGuid(), command.DispatchRequestId, DeviceReceiptKind.Failed, result), ct);
    private SubmitDeviceReceiptRequest Request(Guid commandId, Guid id, Guid dispatch, DeviceReceiptKind kind, WindowsActionResult result)
    {
        var payload = result.Names is null ? JsonSerializer.Serialize(new { accepted = result.Accepted, detail = result.Detail })
            : JsonSerializer.Serialize(new { accepted = result.Accepted, detail = result.Detail, names = result.Names });
        var request = new SubmitDeviceReceiptRequest(id, dispatch, kind, payload, "");
        return request with { Signature = DeviceCommandProof.ReceiptSignature(commandId, request, credential) };
    }
}
