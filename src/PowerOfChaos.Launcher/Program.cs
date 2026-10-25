using Avalonia;
using System.Runtime.InteropServices;

namespace PowerOfChaos.Launcher;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
            _ = SetCurrentProcessExplicitAppUserModelID("PowerOfChaos.Launcher.Desktop");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
