using System.Diagnostics;
using System.Text.Json;

namespace Jarvis.Tests;

public sealed class VerificationCredentialCleanupTests
{
    private static string SourceDirectory()
    {
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root is not null; root = root.Parent) {
            var candidate = Path.Combine(root.FullName, "tests", "Jarvis.Tests");
            if (File.Exists(Path.Combine(candidate, "Scripts", "VerificationCleanupCases.ps1"))) return candidate;
        }
        throw new DirectoryNotFoundException("Cleanup regression requires the repository test sources.");
    }

    [Fact]
    public async Task Retained_verification_credentials_cleanup_is_owned_bounded_and_fails_closed()
    {
        var source = SourceDirectory();
        var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.Combine(temporaryParent, "jarvis-cleanup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-File", Path.Combine(source, "Scripts", "VerificationCleanupCases.ps1"),
                "-DeploymentScript", Path.GetFullPath(Path.Combine(source, "..", "..", "scripts", "test-deployment.ps1")), "-TemporaryRoot", root })
                start.ArgumentList.Add(argument);
            foreach (var variable in new[] { "TEMP", "TMP", "TMPDIR" }) start.Environment[variable] = root;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            try {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await process.WaitForExitAsync(timeout.Token);
                _ = await errors; // Never echo process configuration or fixture contents.
                Assert.True(process.ExitCode == 0, $"Cleanup regression process exited {process.ExitCode}.");
                using var result = JsonDocument.Parse(await output);
                Assert.Equal(9, result.RootElement.GetProperty("passed").GetInt32());
                Assert.True(result.RootElement.GetProperty("dockerMocked").GetBoolean());
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        }
        finally {
            // Remove only this exact owned test root, after its child process has exited.
            var absolute = Path.GetFullPath(root);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.GetDirectoryName(absolute), temporaryParent, comparison) ||
                !Path.GetFileName(absolute).StartsWith("jarvis-cleanup-tests-", StringComparison.Ordinal) ||
                (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned temporary test root.");
            Directory.Delete(absolute, recursive: true);
        }
    }
}
