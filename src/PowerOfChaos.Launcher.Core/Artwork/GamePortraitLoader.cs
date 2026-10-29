using System.Buffers.Binary;
using System.Security;

namespace PowerOfChaos.Launcher.Core.Artwork;

/// <summary>Reads the requested portraits from an installation without extracting files to disk.</summary>
public sealed class GamePortraitLoader
{
    private const int MaximumPayloadBytes = 8_000_000;
    private const int MaximumDimension = 4096;
    // The requested eye frame aligns at this position on the original, unscaled Yugi canvas.
    private const int OverlayX = 277;
    private const int OverlayY = 238;

    private const string KaibaPortrait = @"k\bust_up\kaiba_bu\start_2#.bmp";
    private const string JoeyPortrait = @"j\bust_up\start\reaction16.bmp";
    private const string YugiPortrait = @"y\bust_up\yugi_bu\start_2#.bmp";
    private const string YugiEyes = @"y\bust_up\yugi_bu\kaisi_00_2.bmp";

    public Task<PortraitPixels?> LoadAsync(GameInstallation installation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Load(installation, cancellationToken), cancellationToken);
    }

    private static PortraitPixels? Load(GameInstallation installation, CancellationToken cancellationToken)
    {
        try
        {
            var names = installation.GameId switch
            {
                "kaiba" => new[] { KaibaPortrait },
                "joey" => new[] { JoeyPortrait },
                "yugi" => new[] { YugiPortrait, YugiEyes },
                _ => []
            };
            if (names.Length == 0 || string.IsNullOrWhiteSpace(installation.DataPath)) return null;

            var resources = Storage.GameArchive.Read(installation.DataPath, names, MaximumPayloadBytes, cancellationToken);
            if (resources is null) return null;

            var keyBlue = installation.GameId == "joey" ? (byte)6 : (byte)0;
            var portrait = DecodeBitmap(resources[0], keyBlue, cancellationToken);
            if (portrait is null || names.Length == 1) return portrait;
            var eyes = DecodeBitmap(resources[1], 0, cancellationToken);
            if (eyes is null || !Overlay(portrait, eyes, cancellationToken)) return null;
            return portrait;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or SecurityException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static PortraitPixels? DecodeBitmap(ReadOnlySpan<byte> bitmap, byte keyBlue, CancellationToken cancellationToken)
    {
        if (bitmap.Length < 54 || bitmap[0] != 'B' || bitmap[1] != 'M') return null;
        var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(bitmap[2..]);
        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(bitmap[10..]);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bitmap[14..]);
        if (fileSize > bitmap.Length || headerSize < 40 || headerSize > bitmap.Length - 14
            || pixelOffset < 14L + headerSize || pixelOffset > fileSize) return null;
        var width = BinaryPrimitives.ReadInt32LittleEndian(bitmap[18..]);
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(bitmap[22..]);
        var height = Math.Abs((long)signedHeight);
        if (width <= 0 || width > MaximumDimension || height == 0 || height > MaximumDimension
            || (long)width * height * 4 > MaximumPayloadBytes
            || BinaryPrimitives.ReadUInt16LittleEndian(bitmap[26..]) != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(bitmap[28..]) != 24
            || BinaryPrimitives.ReadUInt32LittleEndian(bitmap[30..]) != 0) return null;
        var stride = ((long)width * 3 + 3) & ~3L;
        var pixelBytes = stride * height;
        var declaredPixelBytes = BinaryPrimitives.ReadUInt32LittleEndian(bitmap[34..]);
        if (pixelBytes > fileSize - pixelOffset || (declaredPixelBytes != 0 && declaredPixelBytes != pixelBytes)) return null;

        var pixels = new byte[width * (int)height * 4];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceY = signedHeight < 0 ? y : height - 1 - y;
            var sourceRow = bitmap.Slice((int)(pixelOffset + sourceY * stride), width * 3);
            var targetRow = pixels.AsSpan(y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                var source = x * 3;
                var target = x * 4;
                var blue = sourceRow[source];
                var green = sourceRow[source + 1];
                var red = sourceRow[source + 2];
                targetRow[target] = blue;
                targetRow[target + 1] = green;
                targetRow[target + 2] = red;
                targetRow[target + 3] = blue == keyBlue && green == 255 && red == 0 ? (byte)0 : (byte)255;
            }
        }
        return new PortraitPixels(width, (int)height, pixels);
    }

    private static bool Overlay(PortraitPixels portrait, PortraitPixels overlay, CancellationToken cancellationToken)
    {
        if ((long)OverlayX + overlay.Width > portrait.Width || (long)OverlayY + overlay.Height > portrait.Height) return false;
        for (var y = 0; y < overlay.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < overlay.Width; x++)
            {
                var source = (y * overlay.Width + x) * 4;
                if (overlay.BgraPixels[source + 3] == 0) continue;
                var target = ((y + OverlayY) * portrait.Width + x + OverlayX) * 4;
                overlay.BgraPixels.AsSpan(source, 4).CopyTo(portrait.BgraPixels.AsSpan(target, 4));
            }
        }
        return true;
    }

}
