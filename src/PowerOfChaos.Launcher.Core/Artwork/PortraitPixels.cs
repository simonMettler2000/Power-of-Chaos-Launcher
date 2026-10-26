namespace PowerOfChaos.Launcher.Core.Artwork;

/// <summary>An in-memory, top-down image in unpremultiplied BGRA byte order.</summary>
public sealed record PortraitPixels(int Width, int Height, byte[] BgraPixels);
