using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PowerOfChaos.Launcher.Core.Launching;

internal sealed record ScalarChange(uint Rva, int FileOffset, uint Expected, uint Replacement);
internal sealed record SplashProfile(string GameId, int Length, string OriginalHash, string ModifiedHash,
    IReadOnlyList<ScalarChange> Changes);

/// <summary>Authored validator for numeric startup settings; contains no game instructions.</summary>
internal static class SkipSplashPatch
{
    internal static IReadOnlyList<SplashProfile> Profiles { get; } = Array.AsReadOnly(new[]
    {
        new SplashProfile("yugi", 1781760,
            "1e5d81b1e44edf09e91addab582ef963694aa285e4be5eb5ad8e0ce4ab04664c",
            "f435d198940f7a044d9e5d42604e1fabe760750300dbd360127ab9effbb503e3",
            Array.AsReadOnly(new[] { new ScalarChange(0x66E4A, 0x66E4A, 4294967292, 476), new ScalarChange(0x66E51, 0x66E51, 0, 1) })),
        new SplashProfile("kaiba", 1789952,
            "a829b16eee11356159850029f5a809886b47c33498ce1d849dd1591a603c0309",
            "83f97698674c3d5f240cbec9061ba19cb785f766f6d53023fee2720c1c3012fc",
            Array.AsReadOnly(new[] { new ScalarChange(0x692FA, 0x692FA, 4294967292, 476), new ScalarChange(0x69301, 0x69301, 0, 1) })),
        new SplashProfile("joey", 2060288,
            "08018ebbc73f3fa51fdf220e903f69cd9a22572e508ea9cd145853abf8ed2dda",
            "3979b923e9c1e020570660159e1ddf168d77f4da898b5013b4b742b5b53e2eaa",
            Array.AsReadOnly(new[] { new ScalarChange(0x884DE, 0x884DE, 4294967292, 476), new ScalarChange(0x884E5, 0x884E5, 0, 2) }))
    });

    internal static byte[] Apply(ReadOnlySpan<byte> original, SplashProfile profile)
    {
        if (original.Length != profile.Length || !MatchesHash(original, profile.OriginalHash))
            throw new InvalidDataException("Skip Splash Screens does not support this executable version. Untick the mod to launch normally.");
        ValidateImage(original, profile.Changes);
        var modified = original.ToArray();
        foreach (var change in profile.Changes)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(original.Slice(change.FileOffset, 4)) != change.Expected)
                throw new InvalidDataException("The startup settings do not match this mod profile.");
            BinaryPrimitives.WriteUInt32LittleEndian(modified.AsSpan(change.FileOffset, 4), change.Replacement);
        }
        if (!MatchesHash(modified, profile.ModifiedHash))
            throw new InvalidDataException("The modified executable failed verification. No game was started.");
        return modified;
    }

    internal static bool MatchesHash(ReadOnlySpan<byte> bytes, string expected) =>
        Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static void ValidateImage(ReadOnlySpan<byte> image, IReadOnlyList<ScalarChange> changes)
    {
        // All addressing is derived from the PE section table, not assumed to equal the RVA.
        if (image.Length < 0x100 || Read16(image, 0) != 0x5A4D) Invalid();
        var pe = checked((int)Read32(image, 0x3C));
        if (Read32(image, pe) != 0x4550 || Read16(image, pe + 4) != 0x14C) Invalid();
        var count = Read16(image, pe + 6);
        var optionalSize = Read16(image, pe + 20);
        var characteristics = Read16(image, pe + 22);
        var optional = checked(pe + 24);
        if (count is 0 or > 96 || optionalSize < 224 || (characteristics & 2) == 0
            || (characteristics & 0x2000) != 0 || Read16(image, optional) != 0x10B) Invalid();
        var sections = checked(optional + optionalSize);
        Bounds(image, sections, checked(count * 40));
        foreach (var change in changes)
        {
            if (change.FileOffset < 0) Invalid();
            Bounds(image, change.FileOffset, 4);
            var offset = Map(image, sections, count, change.Rva, 4);
            if (offset != change.FileOffset) Invalid();
            if (changes.Any(other => !ReferenceEquals(change, other)
                && (long)other.FileOffset < (long)change.FileOffset + 4 && (long)change.FileOffset < (long)other.FileOffset + 4)) Invalid();
        }
        // No relocation may rewrite any part of a modified scalar at load time.
        var directoryCount = Read32(image, optional + 92);
        if (directoryCount <= 5) return;
        var relocationRva = Read32(image, optional + 96 + 5 * 8);
        var relocationSize = Read32(image, optional + 100 + 5 * 8);
        if (relocationRva == 0 && relocationSize == 0) return;
        if (relocationRva == 0 || relocationSize < 8 || relocationSize > int.MaxValue) Invalid();
        var start = Map(image, sections, count, relocationRva, checked((int)relocationSize));
        var end = checked(start + (int)relocationSize);
        for (var block = start; block < end;)
        {
            if (end - block < 8) Invalid();
            var page = Read32(image, block);
            var size = Read32(image, block + 4);
            if (size < 8 || (size & 1) != 0 || size > end - block) Invalid();
            for (var item = block + 8; item < block + size; item += 2)
            {
                var relocation = Read16(image, item);
                var type = relocation >> 12;
                if (type == 0) continue;
                // This mod accepts the documented x86 HIGHLOW format only.
                if (type != 3) Invalid();
                var rva = (ulong)page + (uint)(relocation & 0xFFF);
                if (changes.Any(change => rva < (ulong)change.Rva + 4 && (ulong)change.Rva < rva + 4)) Invalid();
            }
            block = checked(block + (int)size);
        }
    }

    private static int Map(ReadOnlySpan<byte> image, int sections, int count, uint rva, int width)
    {
        int? result = null;
        for (var i = 0; i < count; i++)
        {
            var section = sections + i * 40;
            var start = Read32(image, section + 12);
            var rawSize = Read32(image, section + 16);
            var raw = Read32(image, section + 20);
            if (rva < start || (ulong)rva + (uint)width > (ulong)start + rawSize) continue;
            var offset = (ulong)raw + rva - start;
            if (result is not null || offset > int.MaxValue) Invalid();
            result = (int)offset;
            Bounds(image, result.Value, width);
        }
        return result ?? throw new InvalidDataException("The executable has an unsupported PE section layout.");
    }

    private static ushort Read16(ReadOnlySpan<byte> image, int offset)
    { Bounds(image, offset, 2); return BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(offset, 2)); }
    private static uint Read32(ReadOnlySpan<byte> image, int offset)
    { Bounds(image, offset, 4); return BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(offset, 4)); }
    private static void Bounds(ReadOnlySpan<byte> image, int offset, int width)
    { if (offset < 0 || width < 0 || offset > image.Length - width) Invalid(); }
    private static void Invalid() => throw new InvalidDataException("The executable has an unsupported PE layout or relocation.");
}
