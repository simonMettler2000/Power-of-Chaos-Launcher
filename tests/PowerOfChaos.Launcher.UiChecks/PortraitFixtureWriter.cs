using System.Text;

namespace PowerOfChaos.Launcher.UiChecks;

internal static class PortraitFixtureWriter
{
    public static void Write(string path, string executable)
    {
        var name = executable switch
        {
            "yugi_pc.exe" => "y\\bust_up\\yugi_bu\\start_2#.bmp",
            "kaiba_pc.exe" => "k\\bust_up\\kaiba_bu\\start_2#.bmp",
            "joey_pc.exe" => "j\\bust_up\\start\\reaction16.bmp",
            _ => throw new ArgumentException("Unknown synthetic game.", nameof(executable))
        };
        var entries = new List<(string Name, byte[] Data)> { (name, Bitmap(320, 280, executable == "joey_pc.exe" ? (byte)6 : (byte)0)) };
        if (executable == "yugi_pc.exe")
            entries.Add(("y\\bust_up\\yugi_bu\\kaisi_00_2.bmp", Bitmap(8, 4, 0)));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("KCEJYUGI"));
        writer.Write(entries.Count);
        var offset = 12 + entries.Count * 268;
        foreach (var entry in entries)
        {
            var encoded = new byte[256];
            Encoding.ASCII.GetBytes(entry.Name).CopyTo(encoded, 0);
            for (var i = 0; i < encoded.Length; i++) encoded[i] = (byte)((encoded[i] << 4) | (encoded[i] >> 4));
            writer.Write(encoded);
            writer.Write(offset); writer.Write(entry.Data.Length); writer.Write(entry.Data.Length);
            offset += entry.Data.Length;
        }
        foreach (var entry in entries) writer.Write(entry.Data);
    }

    private static byte[] Bitmap(int width, int height, byte keyBlue)
    {
        // Authored solid rectangles only: no original artwork or game pixels.
        var stride = (width * 3 + 3) & ~3;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4D42); writer.Write(54 + stride * height); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(width); writer.Write(height);
        writer.Write((ushort)1); writer.Write((ushort)24); writer.Write(0); writer.Write(stride * height);
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        for (var y = 0; y < height; y++)
        {
            var row = new byte[stride];
            for (var x = 0; x < width; x++)
            {
                var edge = x == 0 || y == 0 || x == width - 1 || y == height - 1;
                row[x * 3] = edge ? keyBlue : (byte)80;
                row[x * 3 + 1] = edge ? (byte)255 : (byte)95;
                row[x * 3 + 2] = edge ? (byte)0 : (byte)110;
            }
            writer.Write(row);
        }
        return stream.ToArray();
    }
}
