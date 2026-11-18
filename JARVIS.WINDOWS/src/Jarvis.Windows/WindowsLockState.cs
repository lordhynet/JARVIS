using System.Runtime.InteropServices;
using System.Text;
using Jarvis.Windows.Client;

namespace Jarvis.Windows;

internal sealed class WindowsLockState : ILockState
{
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int required);
    public bool IsLocked
    {
        get {
            var desktop = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS, with no desktop switch.
            if (desktop == IntPtr.Zero) return true;
            try {
                var name = new StringBuilder(256);
                return !GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) ||
                    !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
            } finally { CloseDesktop(desktop); }
        }
    }
}
