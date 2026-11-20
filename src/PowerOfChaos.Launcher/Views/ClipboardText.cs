using Avalonia.Controls;
using System.Runtime.InteropServices;

namespace PowerOfChaos.Launcher.Views;

internal static class ClipboardText
{
    public static async Task<bool> TryCopyAsync(Control source, string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(source)?.Clipboard is not { } clipboard) return false;
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException or ExternalException)
        {
            return false;
        }
    }
}
