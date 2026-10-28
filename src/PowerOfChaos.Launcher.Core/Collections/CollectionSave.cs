using System.Buffers.Binary;

namespace PowerOfChaos.Launcher.Core.Collections;

/// <summary>Transforms stock system.dat bytes in memory without accessing files or the registry.</summary>
internal static class CollectionSave
{
    public const int EncodedLength = 5062;
    public const int DecodedLength = 4496;
    public const int CardSlots = 1115;
    private const int PayloadOffset = 8;
    private const int InnerChecksumOffset = 0x1184;
    private const int MaximumPoolCards = 771;
    private const int TotalCopiesOffset = 0x12;

    public static byte[] ReadQuantities(ReadOnlySpan<byte> encoded)
    {
        var decoded = Decode(encoded);
        var quantities = new byte[CardSlots];
        for (var index = 0; index < quantities.Length; index++)
            quantities[index] = decoded[0x14 + index * 2];
        return quantities;
    }

    /// <summary>Returns a validated copy with three copies per supplied native slot and a recalculated total, including for an empty request.</summary>
    public static byte[] CreateUnlocked(ReadOnlySpan<byte> encoded, IReadOnlyList<int> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count > MaximumPoolCards)
            throw new ArgumentException("The supported card pool contains at most 771 cards.", nameof(slots));
        var requested = new HashSet<int>();
        foreach (var slot in slots)
        {
            if (slot < 0 || slot >= CardSlots)
                throw new ArgumentOutOfRangeException(nameof(slots), "Card slot is outside the collection.");
            if (!requested.Add(slot))
                throw new ArgumentException("Card slots must be unique.", nameof(slots));
        }

        var decoded = Decode(encoded);
        foreach (var slot in requested) decoded[0x14 + slot * 2] = 3;
        var totalCopies = 0;
        for (var slot = 0; slot < CardSlots; slot++) totalCopies += decoded[0x14 + slot * 2];
        if (totalCopies > ushort.MaxValue)
            throw new InvalidDataException("Collection copy total exceeds the supported save range.");
        BinaryPrimitives.WriteUInt16LittleEndian(decoded.AsSpan(TotalCopiesOffset), checked((ushort)totalCopies));
        var payload = decoded.AsSpan(PayloadOffset);
        var sum = 0;
        // The checksum word and final two payload bytes are outside this sum.
        for (var offset = 0; offset < InnerChecksumOffset; offset += 2)
            sum += BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[InnerChecksumOffset..], unchecked((ushort)-sum));
        return Encode(decoded);
    }

    private static byte[] Decode(ReadOnlySpan<byte> encoded)
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
        var payload = decoded.AsSpan(PayloadOffset);
        if (!payload.Slice(0x117A, 8).SequenceEqual("YUGIOH01"u8)
            || BinaryPrimitives.ReadUInt16LittleEndian(payload[0x1182..]) != 0xFBA5)
            throw new InvalidDataException("Unsupported collection save signature.");
        var sum = (int)BinaryPrimitives.ReadUInt16LittleEndian(payload[InnerChecksumOffset..]);
        for (var offset = 0; offset < InnerChecksumOffset; offset += 2)
            sum += BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
        if ((sum & 65535) != 0) throw new InvalidDataException("Collection data checksum failed.");
        return decoded;
    }

    private static byte[] Encode(ReadOnlySpan<byte> decoded)
    {
        var encoded = new byte[EncodedLength];
        for (var block = 0; block < DecodedLength / 8; block++)
        {
            UInt128 packed = 0;
            for (var index = 0; index < 8; index++)
                packed |= (UInt128)PowerMod(decoded[block * 8 + index], 5) << (index * 9);
            for (var index = 0; index < 9; index++)
                encoded[4 + block * 9 + index] = (byte)(packed >> (index * 8));
        }
        Span<byte> checksum = stackalloc byte[] { 0x83, 0xED, 0x76, 0x45 };
        for (var index = 4; index < DecodedLength; index++)
            checksum[index & 3] = unchecked((byte)(checksum[index & 3] - encoded[index]));
        checksum.CopyTo(encoded);
        return encoded;
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
