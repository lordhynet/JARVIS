namespace Jarvis.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(true, "Local\\Jarvis.Windows.Companion", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayCompanion());
    }
}
