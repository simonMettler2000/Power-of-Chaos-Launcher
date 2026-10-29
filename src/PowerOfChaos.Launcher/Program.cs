using Avalonia;
using System.Runtime.InteropServices;
using PowerOfChaos.Launcher.Core.Launching;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PowerOfChaos.Launcher;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--package-check")
        {
            if (args is not [_, var report]) return 2;
            return ReleasePackageProbe.Run(report, CheckDesktopDependencies);
        }
        if (OperatingSystem.IsWindows())
            _ = SetCurrentProcessExplicitAppUserModelID("PowerOfChaos.Launcher.Desktop");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();

    private static bool CheckDesktopDependencies()
    {
        // No desktop lifetime/window is created, so App never constructs MainWindow or reads user data.
        BuildAvaloniaApp().SetupWithoutStarting();
        var text = new TextBlock { Text = "Power of Chaos · Français", FontSize = 20,
            Foreground = Brushes.White, Background = Brushes.Black, Width = 320, Height = 48 };
        text.Measure(new Size(320, 48));
        text.Arrange(new Rect(0, 0, 320, 48));
        using var bitmap = new RenderTargetBitmap(new PixelSize(320, 48));
        bitmap.Render(text);
        var pixels = new byte[320 * 48 * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, 320, 48), handle.AddrOfPinnedObject(), pixels.Length, 320 * 4); }
        finally { handle.Free(); }
        return pixels.Where((_, index) => index % 4 != 3).Any(value => value > 0);
    }
}
