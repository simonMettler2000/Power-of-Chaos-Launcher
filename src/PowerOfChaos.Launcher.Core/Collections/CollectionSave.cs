using System.Buffers.Binary;

namespace PowerOfChaos.Launcher.Core.Collections;

/// <summary>Reads the stock system.dat format. It never changes a save or registry value.</summary>
internal static class CollectionSave
{
    public const int EncodedLength = 5062;
    public const int DecodedLength = 4496;
    public const int CardSlots = 1115;

    public static byte[] ReadQuantities(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != EncodedLength) throw new InvalidDataException("Unsupported collection save length.");
        Span<byte> checksum = stackalloc byte[] { 0x83, 0xED, 0x76, 0x45 };
        // The original outer checksum covers this prefix, not the whole encoded file.
        for (var index = 4; index < DecodedLength; index++)
            checksum[index & 3] = unchecked((byte)(checksum[index & 3] - encoded[index]));
        if (!encoded[..4].SequenceEqual(checksum)) throw new InvalidDataException("Collection save checksum failed.");

        var decoded = new byte[DecodedLength];
        for (var block = 0; block < DecodedLength / 8; block++)
        {
            var packed = encoded.Slice(4 + block * 9, 9);
            for (var index = 0; index < 8; index++)
            {
                var code = ((packed[index] | packed[index + 1] << 8) >> index) & 511;
                var value = PowerMod(code, 53);
                if (code >= 299 || value > 255 || PowerMod(value, 5) != code)
                    throw new InvalidDataException("Unsupported collection save encoding.");
                decoded[block * 8 + index] = (byte)value;
            }
        }
        var payload = decoded.AsSpan(8);
        if (!payload.Slice(0x117A, 8).SequenceEqual("YUGIOH01"u8)
            || BinaryPrimitives.ReadUInt16LittleEndian(payload[0x1182..]) != 0xFBA5)
            throw new InvalidDataException("Unsupported collection save signature.");
        var sum = (int)BinaryPrimitives.ReadUInt16LittleEndian(payload[0x1184..]);
        for (var index = 0; index < 0x8C2; index++)
            sum += BinaryPrimitives.ReadUInt16LittleEndian(payload[(index * 2)..]);
        if ((sum & 65535) != 0) throw new InvalidDataException("Collection data checksum failed.");

        var quantities = new byte[CardSlots];
        for (var index = 0; index < quantities.Length; index++)
            quantities[index] = decoded[0x14 + index * 2];
        return quantities;
    }

    private static int PowerMod(int value, int exponent)
    {
        var result = 1;
        while (exponent != 0)
        {
            if ((exponent & 1) != 0) result = result * value % 299;
            value = value * value % 299;
            exponent >>= 1;
        }
        return result;
    }
}
