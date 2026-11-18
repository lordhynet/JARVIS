using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Jarvis.Windows.Client;

public interface ICredentialProtector { byte[] Protect(byte[] value); byte[] Unprotect(byte[] value); }

public sealed class CredentialVault(string path, ICredentialProtector protector)
{
    public void Save(Connection connection)
    {
        connection.Validate();
        var plain = JsonSerializer.SerializeToUtf8Bytes(connection);
        try {
            var encrypted = protector.Protect(plain);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, path, overwrite: true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        } finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public Connection? Read()
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16384) throw new InvalidOperationException("Stored connection exceeded its bound.");
        var plain = protector.Unprotect(File.ReadAllBytes(path));
        try {
            var value = JsonSerializer.Deserialize<Connection>(plain) ?? throw new InvalidOperationException("Stored connection is invalid.");
            value.Validate(); return value;
        } finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Clear() { if (File.Exists(path)) File.Delete(path); }
}

public sealed class DpapiProtector : ICredentialProtector
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob data, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob result);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob data, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob result);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public byte[] Protect(byte[] value) => Transform(value, true);
    public byte[] Unprotect(byte[] value) => Transform(value, false);
    private static byte[] Transform(byte[] value, bool encrypt)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows CurrentUser DPAPI is required.");
        var input = new Blob { Length = value.Length, Data = Marshal.AllocHGlobal(value.Length) }; Blob output = default;
        try {
            Marshal.Copy(value, 0, input.Data, value.Length);
            var ok = encrypt ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("Windows could not protect or open this device credential.");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        } finally {
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) { for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
}
