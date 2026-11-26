namespace PowerOfChaos.Launcher.Core.Launching;

/// <summary>The initial HD game client area in physical screen pixels.</summary>
public readonly record struct HdWindowPlacement(int Width, int Height, int X, int Y)
{
    public static HdWindowPlacement? Fit(int x, int y, int workWidth, int workHeight, double scaling)
    {
        if (workWidth <= 0 || workHeight <= 0) return null;
        var scale = double.IsFinite(scaling) ? Math.Clamp(scaling, 1, 4) : 1;
        // The provider positions the client rect, then adds the window frame and caption around it.
        var frame = (int)Math.Ceiling(12 * scale);
        var caption = (int)Math.Ceiling(40 * scale);
        var usableWidth = workWidth - 2 * frame;
        var usableHeight = workHeight - caption - frame;
        var units = Math.Max(200, Math.Min(usableWidth / 4, usableHeight / 3));
        var width = 4 * units;
        var height = 3 * units;
        return new HdWindowPlacement(width, height,
            x + frame + Math.Max(0, (usableWidth - width) / 2),
            y + caption + Math.Max(0, (usableHeight - height) / 2));
    }
}
