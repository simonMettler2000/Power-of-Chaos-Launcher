using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace PowerOfChaos.Launcher.TestFixtures;

// Authored synthetic data only. No original save, card list or archive payload.
internal static class CollectionFixtures
{
    public static int Slot(int syntheticCard) => syntheticCard * 137 % 1114 + 1;

    public static List<(string Name, byte[] Data)> CardTables()
    {
        var pack = new byte[2230];
        var ids = Enumerable.Repeat((byte)255, 2230).ToArray();
        for (var card = 0; card < 771; card++)
        {
            var offset = Slot(card) * 2;
            BinaryPrimitives.WriteUInt16LittleEndian(pack.AsSpan(offset), (ushort)(card < 155 ? 1 : card < 466 ? 2 : 4));
            BinaryPrimitives.WriteUInt16LittleEndian(ids.AsSpan(offset), (ushort)card);
        }
        return [(@"bin#\card_pack.bin", pack), (@"bin#\card_id.bin", ids)];
    }

    public static void WriteArchive(string path, List<(string Name, byte[] Data)>? entries = null)
    {
        entries ??= CardTables();
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("KCEJYUGI"));
        writer.Write(entries.Count);
        var offset = 12 + entries.Count * 268;
        foreach (var entry in entries)
        {
            var name = new byte[256];
            Encoding.ASCII.GetBytes(entry.Name).CopyTo(name, 0);
            for (var index = 0; index < name.Length; index++)
                name[index] = (byte)(name[index] << 4 | name[index] >> 4);
            writer.Write(name);
            writer.Write(offset); writer.Write(entry.Data.Length); writer.Write(entry.Data.Length);
            offset += entry.Data.Length;
        }
        foreach (var entry in entries) writer.Write(entry.Data);
    }

    public static byte[] Save(IEnumerable<(int Slot, byte Quantity)> quantities, byte[]? header = null, bool validInnerChecksum = true,
        IEnumerable<int>? deckFlags = null)
    {
        var decoded = new byte[4496];
        (header ?? new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 }).CopyTo(decoded, 0);
        foreach (var item in quantities) decoded[0x14 + item.Slot * 2] = item.Quantity;
        foreach (var slot in deckFlags ?? []) decoded[0x15 + slot * 2] = 255;
        var payload = decoded.AsSpan(8);
        "YUGIOH01"u8.CopyTo(payload[0x117A..]);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[0x1182..], 0xFBA5);
        var sum = 0;
        for (var index = 0; index < 2242; index++)
            sum += BinaryPrimitives.ReadUInt16LittleEndian(payload[(index * 2)..]);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[0x1184..], unchecked((ushort)(-sum + (validInnerChecksum ? 0 : 1))));

        var encoded = new byte[5062];
        for (var block = 0; block < 562; block++)
        {
            BigInteger packed = 0;
            for (var index = 0; index < 8; index++)
                packed |= BigInteger.ModPow(decoded[block * 8 + index], 5, 299) << (index * 9);
            packed.ToByteArray(isUnsigned: true, isBigEndian: false).CopyTo(encoded, 4 + block * 9);
        }
        SetOuterChecksum(encoded);
        return encoded;
    }

    public static void SetOuterChecksum(byte[] encoded)
    {
        byte[] seed = [0x83, 0xED, 0x76, 0x45];
        for (var index = 4; index < 4496; index++) seed[index % 4] = unchecked((byte)(seed[index % 4] - encoded[index]));
        seed.CopyTo(encoded, 0);
    }
}
