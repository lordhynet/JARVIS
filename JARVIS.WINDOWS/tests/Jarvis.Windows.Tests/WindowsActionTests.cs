using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Windows.Client;

namespace Jarvis.Windows.Tests;

public sealed class WindowsActionTests
{
    private const string Device = "windows-action-tests", Secret = "jdv1_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static DeliveredDeviceCommand Command(string capability = WindowsCommandProtocol.Media, string arguments = "{\"action\":\"pause\"}")
    {
        var now = DateTimeOffset.UtcNow;
        var command = new DeliveredDeviceCommand(Guid.NewGuid(), capability, WindowsCommandProtocol.Version, arguments, DeviceCommandProof.CredentialHash(arguments), now.AddMinutes(2), now, Guid.NewGuid(), Device);
        return command with { CoreSignature = DeviceCommandProof.Sign(command, DeviceCommandProof.CredentialHash(Secret)) };
    }
    [Fact]
    public async Task Authenticated_command_is_acknowledged_before_execution_and_replay_after_restart_is_refused()
    {
        using var files = new Files(); var core = new Core(); var platform = new Platform { Before = () => Assert.Contains(core.Receipts, r => r.Kind == DeviceReceiptKind.Acknowledged) };
        var command = Command(); var executor = Executor(files, core, platform);
        Assert.True((await executor.ExecuteAsync(command, default)).Accepted);
        var restarted = Executor(files, core, platform);
        Assert.False((await restarted.ExecuteAsync(command, default)).Accepted);
        Assert.Equal(1, platform.Executed); Assert.Single(core.Receipts, r => r.Kind == DeviceReceiptKind.Succeeded);
    }
    [Fact]
    public async Task Unknown_capability_tampered_arguments_and_invalid_signature_never_execute()
    {
        using var files = new Files(); var core = new Core(); var platform = new Platform(); var executor = Executor(files, core, platform);
        Assert.False((await executor.ExecuteAsync(Command("windows.shell", "{}"), default)).Accepted);
        Assert.False((await executor.ExecuteAsync(Command() with { ArgumentsJson = "{\"action\":\"play\"}" }, default)).Accepted);
        Assert.False((await executor.ExecuteAsync(Command() with { CoreSignature = new string('0', 64) }, default)).Accepted);
        Assert.Equal(0, platform.Executed); Assert.Equal(3, core.Receipts.Count(r => r.Kind == DeviceReceiptKind.Failed));
    }
    [Theory]
    [InlineData(WindowsCommandProtocol.Media, "{\"action\":\"pause\"}", true)]
    [InlineData(WindowsCommandProtocol.Lock, "{}", true)]
    [InlineData(WindowsCommandProtocol.App, "{\"appId\":\"editor\"}", false)]
    [InlineData(WindowsCommandProtocol.Url, "{\"urlId\":\"portal\"}", false)]
    [InlineData(WindowsCommandProtocol.Volume, "{\"action\":\"up\",\"steps\":1}", false)]
    [InlineData(WindowsCommandProtocol.Notification, "{\"message\":\"Hello\"}", false)]
    [InlineData(WindowsCommandProtocol.Files, "{\"folderId\":\"cv\",\"name\":\"report\"}", false)]
    public async Task Locked_executor_allows_only_media_and_lock(string capability, string arguments, bool allowed)
    {
        using var files = new Files(); var core = new Core(); var platform = new Platform();
        Assert.Equal(allowed, (await Executor(files, core, platform, locked: true).ExecuteAsync(Command(capability, arguments), default)).Accepted);
        Assert.Equal(allowed ? 1 : 0, platform.Executed);
        Assert.Single(core.Receipts, r => r.Kind == (allowed ? DeviceReceiptKind.Succeeded : DeviceReceiptKind.Failed));
    }
    [Fact]
    public async Task Core_refusal_at_acknowledgement_prevents_all_native_effects()
    {
        using var files = new Files(); var core = new Core { RefuseAcknowledgement = true }; var platform = new Platform();
        Assert.False((await Executor(files, core, platform).ExecuteAsync(Command(), default)).Accepted);
        Assert.Equal(0, platform.Executed); Assert.Single(core.Receipts, r => r.Kind == DeviceReceiptKind.Failed);
    }
    [Fact]
    public async Task Lock_during_acknowledgement_is_checked_again_before_an_app_can_start()
    {
        using var files = new Files(); var locks = new Locks(); var core = new Core { OnAcknowledgement = () => locks.Locked = true }; var platform = new Platform();
        var executor = new WindowsCommandExecutor(Device, Secret, core, platform, locks, Journal(files), TimeProvider.System);
        Assert.False((await executor.ExecuteAsync(Command(WindowsCommandProtocol.App, "{\"appId\":\"editor\"}"), default)).Accepted);
        Assert.Equal(0, platform.Executed);
    }
    [Fact]
    public async Task Interrupted_execution_reports_unknown_after_restart_without_replaying()
    {
        using var files = new Files(); var command = Command(); var journal = Journal(files); Assert.NotNull(journal.Begin(command));
        var core = new Core(); var platform = new Platform(); await Executor(files, core, platform).FlushAsync(default);
        Assert.Equal(0, platform.Executed); var final = Assert.Single(core.Receipts); Assert.Equal(DeviceReceiptKind.Failed, final.Kind);
        Assert.Contains("unknown", final.PayloadJson); Assert.False((await Executor(files, core, platform).ExecuteAsync(command, default)).Accepted);
    }
    [Theory]
    [InlineData(WindowsCommandProtocol.App, "{\"appId\":\"..\\\\cmd.exe\"}")]
    [InlineData(WindowsCommandProtocol.Files, "{\"folderId\":\"cv\",\"name\":\"../secret\"}")]
    [InlineData(WindowsCommandProtocol.Files, "{\"folderId\":\"C:\\\\Users\",\"name\":\"cv\"}")]
    [InlineData(WindowsCommandProtocol.Url, "{\"urlId\":\"https://evil.example\"}")]
    [InlineData(WindowsCommandProtocol.Media, "{\"action\":\"pause\",\"shell\":\"cmd\"}")]
    [InlineData(WindowsCommandProtocol.Media, "{\"action\":\"pause\",\"action\":\"play\"}")]
    public void Wire_arguments_refuse_paths_traversal_unknown_fields_and_duplicates(string capability, string arguments) =>
        Assert.ThrowsAny<Exception>(() => WindowsCommandProtocol.Parse(capability, arguments));

    [Fact]
    public void Filename_search_returns_only_names_and_never_follows_a_junction()
    {
        using var files = new Files(); var scope = Path.Combine(files.Root, "allowed"); var outside = Path.Combine(files.Root, "outside");
        Directory.CreateDirectory(scope); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(scope, "report.txt"), "fixture"); File.WriteAllText(Path.Combine(outside, "report-secret.txt"), "fixture");
        var junction = Path.Combine(scope, "linked"); files.Junction = junction;
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("New-Item -ItemType Junction -Path '" + junction.Replace("'", "''") + "' -Target '" + outside.Replace("'", "''") + "' | Out-Null");
        using (var process = Process.Start(info)!) { Assert.True(process.WaitForExit(10000)); Assert.Equal(0, process.ExitCode); }
        Assert.True((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);
        Assert.Throws<IOException>(() => SafeWindowsPaths.RequireDirectory(junction));
        Assert.Throws<IOException>(() => SafeWindowsPaths.RequireDirectory(Path.Combine(scope, "..", "outside")));
        var matches = SafeWindowsPaths.FindNames(scope, "report", default); Assert.Equal(["report.txt"], matches);
        Assert.All(matches, name => Assert.DoesNotContain(Path.DirectorySeparatorChar, name));
    }
    [Fact]
    public void Renamed_shell_and_wrong_executable_hash_are_refused_without_starting_a_process()
    {
        using var files = new Files(); var path = Path.Combine(files.Root, "renamed.exe"); File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), path);
        using var input = File.OpenRead(path); var hash = Convert.ToHexString(SHA256.HashData(input)); input.Dispose();
        Assert.Throws<IOException>(() => SafeWindowsPaths.OpenAllowedApp(new(path, hash)));
        Assert.Throws<IOException>(() => SafeWindowsPaths.OpenAllowedApp(new(path, new string('0', 64))));
    }
    private static WindowsCommandJournal Journal(Files files) => new(Path.Combine(files.Root, "commands.fixture"), "fixture-enrolment", new Protector());
    private static WindowsCommandExecutor Executor(Files files, Core core, Platform platform, bool locked = false) => new(Device, Secret, core, platform, new Locks { Locked = locked }, Journal(files), TimeProvider.System);
    private sealed class Locks : ILockState { public bool Locked; public bool IsLocked => Locked; }
    private sealed class Protector : ICredentialProtector { public byte[] Protect(byte[] value) => value.ToArray(); public byte[] Unprotect(byte[] value) => value.ToArray(); }
    private sealed class Platform : IWindowsActionPlatform {
        public int Executed; public Action? Before;
        public Task<WindowsActionResult> ExecuteAsync(string capability, WindowsActionArguments arguments, CancellationToken ct) { Before?.Invoke(); Executed++; return Task.FromResult(new WindowsActionResult(true, "Fake accepted; no native action.")); }
    }
    private sealed class Core : IWindowsCommandCore {
        public readonly List<SubmitDeviceReceiptRequest> Receipts = []; public bool RefuseAcknowledgement; public Action? OnAcknowledgement;
        public Task<DeviceReceiptView> ReceiptAsync(Guid commandId, SubmitDeviceReceiptRequest request, CancellationToken ct) {
            Assert.Equal(DeviceCommandProof.ReceiptSignature(commandId, request, Secret), request.Signature);
            if (request.Kind == DeviceReceiptKind.Acknowledged) { OnAcknowledgement?.Invoke(); if (RefuseAcknowledgement) throw new InvalidOperationException("Fake revoked grant."); }
            Receipts.Add(request); return Task.FromResult(new DeviceReceiptView(request.RequestId, commandId, request.DispatchRequestId, request.Kind, request.PayloadJson, DateTimeOffset.UtcNow, false));
        }
    }
    private sealed class Files : IDisposable {
        private static readonly string Base = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Jarvis.Windows.ActionTests"));
        public string Root { get; } = Path.Combine(Base, Guid.NewGuid().ToString("N")); public string? Junction;
        public Files() => Directory.CreateDirectory(Root);
        public void Dispose() {
            var full = Path.GetFullPath(Root); if (!full.StartsWith(Base + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe fixture cleanup.");
            if (Junction is not null && Directory.Exists(Junction)) Directory.Delete(Junction);
            Directory.Delete(full, true);
        }
    }
}
