namespace PowerOfChaos.HdRendering;

/// <summary>One 4:3 logical viewport; dimensions refer to physical output pixels.</summary>
public readonly record struct HdViewport
{
    public const int LogicalWidth = 800;
    public const int LogicalHeight = 600;
    public int OutputWidth { get; }
    public int OutputHeight { get; }
    public double Scale { get; }
    public double Left { get; }
    public double Top { get; }
    public double Width => LogicalWidth * Scale;
    public double Height => LogicalHeight * Scale;

    public HdViewport(int outputWidth, int outputHeight)
    {
        if (outputWidth is < 1 or > 16384 || outputHeight is < 1 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(outputWidth), "Output dimensions must be in 1..16384.");
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        Scale = Math.Min(outputWidth / (double)LogicalWidth, outputHeight / (double)LogicalHeight);
        Left = (outputWidth - Width) / 2;
        Top = (outputHeight - Height) / 2;
    }

    /// <summary>Bars and the right/bottom edge are outside; no input is synthesized.</summary>
    public bool TryToLogical(double physicalX, double physicalY, out double x, out double y)
    {
        x = y = 0;
        if (!double.IsFinite(physicalX) || !double.IsFinite(physicalY)
            || physicalX < Left || physicalY < Top || physicalX >= Left + Width || physicalY >= Top + Height)
            return false;
        x = (physicalX - Left) / Scale;
        y = (physicalY - Top) / Scale;
        return true;
    }
}

/// <summary>A rectangle in the unchanged 800 by 600 game coordinate system.</summary>
public readonly record struct LogicalRect(double X, double Y, double Width, double Height)
{
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y)
        && double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0
        && double.IsFinite(X + Width) && double.IsFinite(Y + Height);
}

public enum CardFit { Contain, Cover }
public enum CardRotation { Upright, Clockwise, UpsideDown, Counterclockwise }
