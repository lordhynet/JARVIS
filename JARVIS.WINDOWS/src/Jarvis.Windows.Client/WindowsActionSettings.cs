using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Jarvis.Windows.Client;

public sealed record AllowedWindowsApp(string Path, string Sha256);
public sealed record WindowsActionSettings(IReadOnlyDictionary<string, AllowedWindowsApp> Apps,
    IReadOnlyDictionary<string, string> Urls, IReadOnlyDictionary<string, string> Folders)
{
    public static WindowsActionSettings Load(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 32000) throw new InvalidOperationException("Owner action allow-lists are missing or too large.");
        var settings = JsonSerializer.Deserialize<WindowsActionSettings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Action allow-lists are invalid.");
        if (settings.Apps is null || settings.Urls is null || settings.Folders is null || settings.Apps.Count > 50 || settings.Urls.Count > 50 || settings.Folders.Count > 50 ||
            settings.Apps.Keys.Concat(settings.Urls.Keys).Concat(settings.Folders.Keys).Any(id => !Jarvis.Contracts.WindowsCommandProtocol.IsId(id)))
            throw new InvalidOperationException("Action allow-list IDs or bounds are invalid.");
        foreach (var app in settings.Apps.Values) {
            if (!System.IO.Path.IsPathFullyQualified(app.Path) || !app.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || app.Sha256.Length != 64 || !app.Sha256.All(Uri.IsHexDigit))
                throw new InvalidOperationException("Each allowed app needs an absolute executable path and SHA-256 hash.");
        }
        foreach (var url in settings.Urls.Values) if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0)
            throw new InvalidOperationException("Allowed URLs must be exact HTTPS URLs without credentials.");
        foreach (var folder in settings.Folders.Values) SafeWindowsPaths.RequireDirectory(folder).Dispose();
        return settings;
    }
}

public static class SafeWindowsPaths
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes, Tag; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out AttributeTag value, uint size);
    private static void RefuseLink(SafeFileHandle handle) {
        if (!GetFileInformationByHandleEx(handle, 9, out var info, (uint)Marshal.SizeOf<AttributeTag>()) || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new IOException("Reparse-point handles are not allowed.");
    }
    public static string FinalPath(SafeFileHandle handle)
    {
        var text = new StringBuilder(32768); var length = GetFinalPathNameByHandleW(handle, text, (uint)text.Capacity, 0);
        if (length == 0 || length >= text.Capacity) throw new IOException("Cannot resolve the allowed file handle.");
        var value = text.ToString();
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Network paths are not allowed.");
        return value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value[4..] : value;
    }
    public static SafeFileHandle RequireDirectory(string path)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
            throw new IOException("Use an absolute local owner-allowed folder.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase)) throw new IOException("Allow a specific folder, never an entire drive root.");
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Symlinks, junctions and reparse points are refused.");
        var handle = CreateFileW(full, 0, 1, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot open the allowed folder."); }
        try { RefuseLink(handle); if (!string.Equals(Path.TrimEndingDirectorySeparator(FinalPath(handle)), full, StringComparison.OrdinalIgnoreCase)) throw new IOException("Allowed folder changed or resolves outside its path."); return handle; }
        catch { handle.Dispose(); throw; }
    }
    public static IReadOnlyList<string> FindNames(string root, string query, CancellationToken ct, Func<bool>? unlocked = null)
    {
        using var pinned = RequireDirectory(root); var canonical = Path.TrimEndingDirectorySeparator(FinalPath(pinned));
        var results = new List<string>(); var count = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true, MaxRecursionDepth = 8 };
        foreach (var path in Directory.EnumerateFiles(canonical, "*", options)) {
            ct.ThrowIfCancellationRequested(); if (unlocked?.Invoke() == false) throw new IOException("Search stopped because the workstation locked.");
            if (++count > 5000 || results.Count >= 20) break;
            var name = Path.GetFileName(path); if (name.Length > 120 || !name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            using var handle = CreateFileW(path, 0, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid) continue;
            RefuseLink(handle);
            var final = FinalPath(handle);
            if (!final.StartsWith(canonical + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Search entry escaped its allowed folder.");
            // Recheck every parent so link changes cannot introduce an alternate scope.
            using var parent = RequireDirectory(Path.GetDirectoryName(path)!);
            if (!string.Equals(final, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) throw new IOException("Linked search entries are refused.");
            results.Add(Path.GetFileName(final));
        }
        return results;
    }
    public static FileStream OpenAllowedApp(AllowedWindowsApp app)
    {
        var full = Path.GetFullPath(app.Path); using var parent = RequireDirectory(Path.GetDirectoryName(full)!);
        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("Only an allowed executable is supported.");
        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        try {
            var blocked = new[] { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regsvr32.exe", "wsl.exe", "bash.exe", "python.exe", "node.exe", "wt.exe", "conhost.exe" };
            var original = System.Diagnostics.FileVersionInfo.GetVersionInfo(full).OriginalFilename;
            if (!string.Equals(FinalPath(stream.SafeFileHandle), full, StringComparison.OrdinalIgnoreCase) || blocked.Contains(Path.GetFileName(full), StringComparer.OrdinalIgnoreCase) ||
                original is not null && blocked.Contains(original, StringComparer.OrdinalIgnoreCase) ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(stream)), app.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Executable identity is not in the owner allow-list.");
            return stream;
        } catch { stream.Dispose(); throw; }
    }
}
