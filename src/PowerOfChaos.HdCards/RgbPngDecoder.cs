using System.Buffers.Binary;
using System.IO.Compression;

namespace PowerOfChaos.HdCards;

internal static class RgbPngDecoder
{
    private const int Width = 800, Height = 1160, Stride = Width * 3;

    internal static RgbCardImage Decode(byte[] png)
    {
        ReadOnlySpan<byte> signature = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(signature)) Fail("Invalid PNG signature.");
        bool header = false, data = false, dataEnded = false, end = false, palette = false;
        int offset = 8, chunkCount = 0;
        using var compressed = new MemoryStream();
        while (offset < png.Length)
        {
            if (++chunkCount > 4096 || png.Length - offset < 12) Fail("Truncated or excessive PNG chunks.");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            if (length > int.MaxValue || length > png.Length - offset - 12) Fail("Invalid PNG chunk length.");
            int count = (int)length;
            ReadOnlySpan<byte> type = png.AsSpan(offset + 4, 4), body = png.AsSpan(offset + 8, count);
            foreach (byte c in type) if (c is not (>= 65 and <= 90) and not (>= 97 and <= 122)) Fail("Invalid PNG chunk name.");
            if ((type[2] & 32) != 0) Fail("Invalid PNG reserved chunk bit.");
            uint actual = Crc32(png.AsSpan(offset + 4, count + 4));
            uint expected = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + count, 4));
            if (actual != expected) Fail("PNG CRC mismatch.");
            bool isHeader = type.SequenceEqual("IHDR"u8), isData = type.SequenceEqual("IDAT"u8);
            if (!header && !isHeader) Fail("IHDR must be first.");
            if (data && !isData) dataEnded = true;
            if (isHeader)
            {
                if (header || count != 13) Fail("Invalid or duplicate IHDR.");
                if (BinaryPrimitives.ReadUInt32BigEndian(body[..4]) != Width ||
                    BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)) != Height ||
                    body[8] != 8 || body[9] != 2 || body[10] != 0 || body[11] != 0 || body[12] != 0)
                    Fail("Only 800x1160 RGB8 noninterlaced PNG is supported.");
                header = true;
            }
            else if (isData)
            {
                if (dataEnded) Fail("IDAT chunks must be consecutive.");
                compressed.Write(body);
                data = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (count != 0 || !data || offset + 12 != png.Length) Fail("Invalid IEND or trailing PNG data.");
                end = true;
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (palette || data || count is < 3 or > 768 || count % 3 != 0) Fail("Invalid suggested palette.");
                palette = true;
            }
            else if ((type[0] & 32) == 0 || type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8) || type.SequenceEqual("tRNS"u8))
                Fail("Unsupported PNG feature.");
            offset += count + 12;
        }
        if (!header || !data || !end) Fail("Incomplete PNG.");
        byte[] zlib = compressed.ToArray();
        int rawLength = checked((Stride + 1) * Height);
        DeflateStructure.ValidateZlib(zlib, rawLength);
        using var input = new MemoryStream(zlib, false);
        using var inflater = new ZLibStream(input, CompressionMode.Decompress);
        byte[] pixels = new byte[Stride * Height], previous = new byte[Stride], row = new byte[Stride];
        uint adlerA = 1, adlerB = 0;
        for (int y = 0; y < Height; y++)
        {
            int filter = inflater.ReadByte();
            if (filter is < 0 or > 4) Fail("Truncated PNG row or unsupported filter.");
            AddAdler((byte)filter, ref adlerA, ref adlerB);
            inflater.ReadExactly(row);
            foreach (byte value in row) AddAdler(value, ref adlerA, ref adlerB);
            for (int x = 0; x < Stride; x++)
            {
                byte left = x >= 3 ? row[x - 3] : (byte)0;
                byte up = previous[x], upperLeft = x >= 3 ? previous[x - 3] : (byte)0;
                int predictor = filter switch
                {
                    0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2,
                    4 => Paeth(left, up, upperLeft), _ => throw new InvalidDataException()
                };
                row[x] = unchecked((byte)(row[x] + predictor));
            }
            row.CopyTo(pixels, y * Stride);
            (row, previous) = (previous, row);
        }
        if (inflater.ReadByte() != -1) Fail("PNG expands beyond fixed dimensions.");
        uint adler = (adlerB << 16) | adlerA;
        if (BinaryPrimitives.ReadUInt32BigEndian(zlib.AsSpan(zlib.Length - 4)) != adler) Fail("Zlib checksum mismatch.");
        return new(pixels);
    }

    private static void AddAdler(byte value, ref uint a, ref uint b) { a = (a + value) % 65521; b = (b + a) % 65521; }
    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & unchecked(0u - (crc & 1)));
        }
        return ~crc;
    }
    private static void Fail(string message) => throw new InvalidDataException(message);
}

/// <summary>Checks complete DEFLATE framing separately because ZLibStream alone may accept truncated input.
/// Decoding pixels and verifying the Adler checksum remain ZLibStream's/our separate responsibilities.</summary>
internal static class DeflateStructure
{
    private static readonly int[] LengthBase = { 3,4,5,6,7,8,9,10,11,13,15,17,19,23,27,31,35,43,51,59,67,83,99,115,131,163,195,227,258 };
    private static readonly int[] LengthExtra = { 0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,3,3,3,3,4,4,4,4,5,5,5,5,0 };
    private static readonly int[] DistanceBase = { 1,2,3,4,5,7,9,13,17,25,33,49,65,97,129,193,257,385,513,769,1025,1537,2049,3073,4097,6145,8193,12289,16385,24577 };
    private static readonly int[] DistanceExtra = { 0,0,0,0,1,1,2,2,3,3,4,4,5,5,6,6,7,7,8,8,9,9,10,10,11,11,12,12,13,13 };
    private static readonly int[] CodeOrder = { 16,17,18,0,8,7,9,6,10,5,11,4,12,3,13,2,14,1,15 };

    internal static void ValidateZlib(byte[] bytes, int expectedLength)
    {
        if (bytes.Length < 6 || (bytes[0] & 15) != 8 || (bytes[0] >> 4) > 7 ||
            ((bytes[0] << 8) + bytes[1]) % 31 != 0 || (bytes[1] & 32) != 0)
            throw new InvalidDataException("Unsupported or truncated zlib header.");
        var bits = new Bits(bytes, 2, bytes.Length - 4);
        int produced = 0, blocks = 0;
        bool final;
        do
        {
            if (++blocks > 100000) throw new InvalidDataException("Excessive DEFLATE blocks.");
            final = bits.Read(1) != 0;
            int kind = bits.Read(2);
            if (kind == 0)
            {
                bits.Align();
                int length = bits.Read(16), inverse = bits.Read(16);
                if ((length ^ 65535) != inverse) throw new InvalidDataException("Invalid stored block length.");
                bits.SkipBytes(length);
                produced = checked(produced + length);
            }
            else if (kind is 1 or 2)
            {
                Huffman literal, distance;
                if (kind == 1)
                {
                    var lengths = new int[288];
                    for (int i = 0; i < lengths.Length; i++) lengths[i] = i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8;
                    literal = new(lengths);
                    distance = new(Enumerable.Repeat(5, 32).ToArray());
                }
                else
                {
                    int literalCount = bits.Read(5) + 257, distanceCount = bits.Read(5) + 1, codeCount = bits.Read(4) + 4;
                    if (literalCount > 286) throw new InvalidDataException("Invalid literal alphabet.");
                    var codeLengths = new int[19];
                    for (int i = 0; i < codeCount; i++) codeLengths[CodeOrder[i]] = bits.Read(3);
                    var codeTree = new Huffman(codeLengths);
                    var lengths = new int[literalCount + distanceCount];
                    int iLength = 0;
                    while (iLength < lengths.Length)
                    {
                        int symbol = codeTree.Read(bits);
                        if (symbol < 16) lengths[iLength++] = symbol;
                        else
                        {
                            if (symbol == 16 && iLength == 0) throw new InvalidDataException("Invalid code-length repeat.");
                            int value = symbol == 16 ? lengths[iLength - 1] : 0;
                            int repeat = symbol switch { 16 => bits.Read(2) + 3, 17 => bits.Read(3) + 3, 18 => bits.Read(7) + 11, _ => throw new InvalidDataException() };
                            if (repeat > lengths.Length - iLength) throw new InvalidDataException("Code-length repeat overflow.");
                            Array.Fill(lengths, value, iLength, repeat);
                            iLength += repeat;
                        }
                    }
                    if (lengths[256] == 0) throw new InvalidDataException("Missing end-of-block symbol.");
                    literal = new(lengths[..literalCount]);
                    distance = new(lengths[literalCount..]);
                }
                while (true)
                {
                    int symbol = literal.Read(bits);
                    if (symbol == 256) break;
                    if (symbol < 256) produced = checked(produced + 1);
                    else
                    {
                        int index = symbol - 257;
                        if (index >= LengthBase.Length) throw new InvalidDataException("Reserved length symbol.");
                        int length = LengthBase[index] + bits.Read(LengthExtra[index]);
                        int d = distance.Read(bits);
                        if (d >= DistanceBase.Length) throw new InvalidDataException("Reserved distance symbol.");
                        int back = DistanceBase[d] + bits.Read(DistanceExtra[d]);
                        if (back > produced || back > (1 << ((bytes[0] >> 4) + 8))) throw new InvalidDataException("Invalid back reference.");
                        produced = checked(produced + length);
                    }
                    if (produced > expectedLength) throw new InvalidDataException("Excessive expanded PNG data.");
                }
            }
            else throw new InvalidDataException("Reserved DEFLATE block.");
            if (produced > expectedLength) throw new InvalidDataException("Excessive expanded PNG data.");
        } while (!final);
        bits.Align();
        if (bits.ByteOffset != bytes.Length - 4 || produced != expectedLength)
            throw new InvalidDataException("Truncated, trailing or incorrectly sized DEFLATE data.");
    }

    private sealed class Bits(byte[] bytes, int start, int end)
    {
        private int position = start * 8;
        internal int ByteOffset => position / 8;
        internal int Read(int count)
        {
            if (count < 0 || count > 16 || (long)position + count > (long)end * 8)
                throw new InvalidDataException("Truncated DEFLATE bits.");
            int value = 0;
            for (int i = 0; i < count; i++, position++) value |= ((bytes[position / 8] >> (position % 8)) & 1) << i;
            return value;
        }
        internal void Align() => position = checked((position + 7) & ~7);
        internal void SkipBytes(int count)
        {
            if ((long)position + (long)count * 8 > (long)end * 8) throw new InvalidDataException("Truncated stored block.");
            position = checked(position + count * 8);
        }
    }

    private sealed class Huffman
    {
        private readonly Dictionary<int, int> symbols = new();
        private readonly int maximum;
        internal Huffman(int[] lengths)
        {
            var counts = new int[16];
            foreach (int length in lengths)
            {
                if (length is < 0 or > 15) throw new InvalidDataException("Invalid Huffman length.");
                if (length > 0) counts[length]++;
                maximum = Math.Max(maximum, length);
            }
            int left = 1;
            for (int i = 1; i <= 15; i++)
            {
                left = (left << 1) - counts[i];
                if (left < 0) throw new InvalidDataException("Oversubscribed Huffman tree.");
            }
            var next = new int[16];
            int code = 0;
            for (int i = 1; i <= 15; i++) { code = (code + counts[i - 1]) << 1; next[i] = code; }
            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                int length = lengths[symbol];
                if (length != 0) symbols.Add((1 << length) | next[length]++, symbol);
            }
        }
        internal int Read(Bits bits)
        {
            int code = 0;
            for (int length = 1; length <= maximum; length++)
            {
                code = (code << 1) | bits.Read(1);
                if (symbols.TryGetValue((1 << length) | code, out int symbol)) return symbol;
            }
            throw new InvalidDataException("Invalid Huffman symbol.");
        }
    }
}
