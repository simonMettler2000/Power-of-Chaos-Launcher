using System.Buffers.Binary;
using System.Numerics;
using PowerOfChaos.Launcher.Core.Collections;

namespace PowerOfChaos.Launcher.Tests;

internal static class TemporaryUnlockCodecChecks
{
    private const int Payload = 8;
    private const int Checksum = Payload + 0x1184;
    private static readonly IReadOnlyDictionary<int, byte> CanonicalCodes = Enumerable.Range(0, 256)
        .ToDictionary(value => (int)BigInteger.ModPow(value, 5, 299), value => (byte)value);

    public static async Task RunAll(string root, Func<string, Func<Task>, Task> run)
    {
        _ = root; // Every fixture is authored in memory; these checks perform no save or registry writes.
        await run("temporary unlock codec preserves every byte on a canonical empty request", () =>
        {
            var decoded = SyntheticDecoded();
            Require(decoded.Distinct().Count() == 256, "Fixture does not cover all byte values.");
            var source = OracleEncode(decoded);
            var before = source.ToArray();
            var output = CollectionSave.CreateUnlocked(source, []);
            Require(!ReferenceEquals(source, output), "Empty request returned the source array.");
            Require(output.SequenceEqual(source), "Canonical empty request changed bytes.");
            Require(OracleDecode(output).SequenceEqual(decoded), "All-byte round trip differs.");
            Require(source.SequenceEqual(before), "Empty request modified its source.");
            Validate(output, decoded);
            output[0] ^= 1;
            Require(source.SequenceEqual(before), "Output aliases the caller's source.");
            return Task.CompletedTask;
        });

        await run("temporary unlock codec retains independent small golden vectors and all header values", () =>
        {
            foreach (var first in Enumerable.Range(0, 32))
            {
                var decoded = SyntheticDecoded();
                for (var index = 0; index < 8; index++) decoded[index] = (byte)(first * 8 + index);
                var source = OracleEncode(decoded);
                var output = CollectionSave.CreateUnlocked(source, [17]);
                var result = OracleDecode(output);
                Require(result.AsSpan(0, 8).SequenceEqual(decoded.AsSpan(0, 8)), "Eight-byte identity changed.");
                if (first == 0)
                    Require(output.AsSpan(4, 9).SequenceEqual(Convert.FromHexString("00028098F7E790801F")), "00..07 golden vector differs.");
                Validate(output, result);
            }
            var high = SyntheticDecoded();
            high.AsSpan(0, 8).Fill(255);
            var allHigh = CollectionSave.CreateUnlocked(OracleEncode(high), [17]);
            Require(allHigh.AsSpan(4, 9).SequenceEqual(Convert.FromHexString("D8B061C3860D1B366C")), "FF golden vector differs.");
            return Task.CompletedTask;
        });

        await run("temporary unlock changes only native quantities, derived copy total and inner checksum", () =>
        {
            // External card ID zero deliberately maps to slot 17, not slot zero.
            var identities = new[] { (Id: 0, Slot: 17), (Id: 42, Slot: 900), (Id: 70, Slot: 1114) };
            var requested = identities.Select(card => card.Slot).ToArray();
            var decoded = SyntheticDecoded();
            decoded[QuantityOffset(0)] = 9;
            decoded[QuantityOffset(17)] = 9;
            decoded[QuantityOffset(900)] = 0;
            decoded[QuantityOffset(1114)] = 1;
            SetTotalCopies(decoded);
            SetInnerChecksum(decoded);
            var source = OracleEncode(decoded);
            var before = source.ToArray();
            var output = CollectionSave.CreateUnlocked(source, requested);
            var result = OracleDecode(output);
            var expected = decoded.ToArray();
            foreach (var slot in requested) expected[QuantityOffset(slot)] = 3;
            SetTotalCopies(expected);
            SetInnerChecksum(expected);
            Require(result.SequenceEqual(expected), "Bytes outside quantities, copy total and checksum changed.");
            Require(output.SequenceEqual(OracleEncode(expected)), "Output differs from independent canonical encoder.");
            var changed = result.Select((value, index) => (value, index)).Where(item => item.value != decoded[item.index]);
            Require(changed.All(item => requested.Any(slot => item.index == QuantityOffset(slot))
                || item.index is 0x12 or 0x13 || item.index == Checksum || item.index == Checksum + 1), "Unrelated decoded byte changed.");
            Require(result.AsSpan(0, 8).SequenceEqual(decoded.AsSpan(0, 8)), "Identity changed.");
            Require(result.AsSpan(^2).SequenceEqual(decoded.AsSpan(^2)) && result[^2] != 0 && result[^1] != 0,
                "Final two payload bytes were cleared or recalculated.");
            Require(result[QuantityOffset(0)] == 9, "External ID zero was used as a native slot.");
            Require(result[QuantityOffset(500)] == decoded[QuantityOffset(500)], "Unavailable card quantity changed.");
            Require(result[QuantityOffset(17) + 1] == decoded[QuantityOffset(17) + 1], "Deck flag changed.");
            Require(source.SequenceEqual(before), "Unlock modified the caller's source.");
            var quantities = CollectionSave.ReadQuantities(output);
            Require(requested.All(slot => quantities[slot] == 3), "Requested inventory does not contain exactly three copies.");
            Require(quantities.Where((_, slot) => !requested.Contains(slot))
                .SequenceEqual(CollectionSave.ReadQuantities(source).Where((_, slot) => !requested.Contains(slot))),
                "Unrequested inventory changed.");
            Validate(output, result);
            return Task.CompletedTask;
        });

        await run("temporary unlock accepts native bounds and the complete supported pool", () =>
        {
            var decoded = SyntheticDecoded();
            for (var slot = 0; slot < 1115; slot++) decoded[QuantityOffset(slot)] = 0;
            SetTotalCopies(decoded);
            SetInnerChecksum(decoded);
            var source = OracleEncode(decoded);
            foreach (var slots in new[] { new[] { 0, 1114 }, Enumerable.Range(0, 771).ToArray() })
            {
                var output = CollectionSave.CreateUnlocked(source, slots);
                var result = OracleDecode(output);
                var expected = decoded.ToArray();
                foreach (var slot in slots) expected[QuantityOffset(slot)] = 3;
                SetTotalCopies(expected);
                SetInnerChecksum(expected);
                Require(result.SequenceEqual(expected), "Accepted pool did not preserve unrequested bytes.");
                Require(BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(0x12)) == slots.Length * 3,
                    "Derived copy total does not equal 2,313 for the complete supported pool.");
                Validate(output, result);
            }
            return Task.CompletedTask;
        });

        await run("temporary unlock copy totals include nonpool quantities and repair stale totals for empty requests", () =>
        {
            var decoded = SyntheticDecoded();
            for (var slot = 0; slot < 1115; slot++) decoded[QuantityOffset(slot)] = 0;
            decoded[QuantityOffset(1000)] = 7;
            decoded[QuantityOffset(1114)] = 211;
            SetTotalCopies(decoded);
            SetInnerChecksum(decoded);
            var source = OracleEncode(decoded);
            var slots = Enumerable.Range(0, 771).ToArray();
            var output = CollectionSave.CreateUnlocked(source, slots);
            var result = OracleDecode(output);
            Require(BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(0x12)) == 2313 + 7 + 211,
                "Total copies omitted quantities outside the requested pool.");
            var expected = decoded.ToArray();
            foreach (var slot in slots) expected[QuantityOffset(slot)] = 3;
            SetTotalCopies(expected);
            SetInnerChecksum(expected);
            Require(result.SequenceEqual(expected), "Nonpool quantities or unrelated bytes changed.");
            Validate(output, result);

            var stale = decoded.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(stale.AsSpan(0x12), 60000);
            SetInnerChecksum(stale);
            var staleEncoded = OracleEncode(stale);
            var staleBefore = staleEncoded.ToArray();
            var repaired = CollectionSave.CreateUnlocked(staleEncoded, []);
            Require(OracleDecode(repaired).SequenceEqual(decoded), "Empty request did not recalculate only the stale derived total and checksum.");
            Require(staleEncoded.SequenceEqual(staleBefore), "Empty request modified the caller's stale source.");
            Require(CollectionSave.ReadQuantities(staleEncoded).SequenceEqual(CollectionSave.ReadQuantities(repaired)),
                "Existing quantity reader started interpreting the derived total as inventory.");
            Validate(repaired, decoded);
            return Task.CompletedTask;
        });

        await run("temporary unlock copy total accepts UInt16 maximum and rejects overflow without wrapping", () =>
        {
            var decoded = SyntheticDecoded();
            for (var slot = 0; slot < 1115; slot++) decoded[QuantityOffset(slot)] = slot < 257 ? (byte)255 : (byte)0;
            SetTotalCopies(decoded);
            SetInnerChecksum(decoded);
            var maximum = OracleEncode(decoded);
            var output = CollectionSave.CreateUnlocked(maximum, []);
            Require(output.SequenceEqual(maximum), "UInt16 maximum copy total was rejected or changed.");
            Require(BinaryPrimitives.ReadUInt16LittleEndian(OracleDecode(output).AsSpan(0x12)) == 65535,
                "Maximum copy total wrapped.");
            var maximumBefore = maximum.ToArray();
            Throws<InvalidDataException>(() => CollectionSave.CreateUnlocked(maximum, [258]));
            Require(maximum.SequenceEqual(maximumBefore), "Overflow request mutated the source.");

            for (var slot = 0; slot < 1115; slot++) decoded[QuantityOffset(slot)] = 255;
            // A syntactically valid input may have more copies than its derived UInt16 can represent.
            BinaryPrimitives.WriteUInt16LittleEndian(decoded.AsSpan(0x12), 65535);
            SetInnerChecksum(decoded);
            var overflow = OracleEncode(decoded);
            var before = overflow.ToArray();
            Throws<InvalidDataException>(() => CollectionSave.CreateUnlocked(overflow, []));
            Throws<InvalidDataException>(() => CollectionSave.CreateUnlocked(overflow, [17]));
            Throws<InvalidDataException>(() => CollectionSave.CreateUnlocked(overflow, Enumerable.Range(0, 771).ToArray()));
            Require(overflow.SequenceEqual(before), "Overflowing input was mutated.");
            Require(CollectionSave.ReadQuantities(overflow).All(quantity => quantity == 255),
                "Pure read validation behavior changed for a checksum-valid quantity array.");
            return Task.CompletedTask;
        });

        await run("temporary unlock rejects duplicate, oversized and invalid slot requests without mutation", () =>
        {
            var source = OracleEncode(SyntheticDecoded());
            var before = source.ToArray();
            Throws<ArgumentNullException>(() => CollectionSave.CreateUnlocked(source, null!));
            Throws<ArgumentException>(() => CollectionSave.CreateUnlocked(source, [17, 17]));
            Throws<ArgumentException>(() => CollectionSave.CreateUnlocked(source, Enumerable.Range(0, 772).ToArray()));
            Throws<ArgumentOutOfRangeException>(() => CollectionSave.CreateUnlocked(source, [-1]));
            Throws<ArgumentOutOfRangeException>(() => CollectionSave.CreateUnlocked(source, [1115]));
            Require(source.SequenceEqual(before), "Rejected request changed the source.");
            return Task.CompletedTask;
        });

        await run("temporary unlock rejects corrupt, noncanonical and torn saves even for an empty request", () =>
        {
            var decoded = SyntheticDecoded();
            var source = OracleEncode(decoded);
            var badOuter = source.ToArray(); badOuter[0] ^= 1;
            var badInner = decoded.ToArray(); badInner[2000] ^= 1;
            var badSignature = decoded.ToArray(); badSignature[Payload + 0x117A] ^= 1; SetInnerChecksum(badSignature);
            var badMarker = decoded.ToArray(); badMarker[Payload + 0x1182] ^= 1; SetInnerChecksum(badMarker);
            var outOfRange = source.ToArray(); WriteCode(outOfRange, 0, 0, 511); SetOuterChecksum(outOfRange);
            var unsupported = source.ToArray();
            WriteCode(unsupported, 0, 0, (int)BigInteger.ModPow(256, 5, 299)); SetOuterChecksum(unsupported);
            var tornTail = source.ToArray(); WriteCode(tornTail, 561, 7, 511); SetOuterChecksum(tornTail);
            byte[][] invalid = [[], new byte[5061], new byte[5063], badOuter, OracleEncode(badInner),
                OracleEncode(badSignature), OracleEncode(badMarker), outOfRange, unsupported, tornTail];
            foreach (var bytes in invalid)
            {
                var before = bytes.ToArray();
                Throws<InvalidDataException>(() => CollectionSave.CreateUnlocked(bytes, []));
                Throws<InvalidDataException>(() => CollectionSave.CreateUnlocked(bytes, [17]));
                Throws<InvalidDataException>(() => CollectionSave.ReadQuantities(bytes));
                Require(bytes.SequenceEqual(before), "Rejected save was mutated.");
            }
            return Task.CompletedTask;
        });
    }

    private static int QuantityOffset(int slot) => 0x14 + slot * 2;

    private static byte[] SyntheticDecoded()
    {
        var decoded = Enumerable.Range(0, 4496).Select(index => unchecked((byte)(index * 73 + 19))).ToArray();
        for (var slot = 0; slot < 1115; slot++) decoded[QuantityOffset(slot)] = (byte)(slot % 10);
        "YUGIOH01"u8.CopyTo(decoded.AsSpan(Payload + 0x117A));
        BinaryPrimitives.WriteUInt16LittleEndian(decoded.AsSpan(Payload + 0x1182), 0xFBA5);
        decoded[^2] = 0xA7;
        decoded[^1] = 0x5B;
        SetTotalCopies(decoded);
        SetInnerChecksum(decoded);
        return decoded;
    }

    private static void SetTotalCopies(byte[] decoded)
    {
        var total = Enumerable.Range(0, 1115).Sum(slot => decoded[QuantityOffset(slot)]);
        BinaryPrimitives.WriteUInt16LittleEndian(decoded.AsSpan(0x12), checked((ushort)total));
    }

    private static void SetInnerChecksum(byte[] decoded)
    {
        uint total = 0;
        for (var offset = Payload; offset < Checksum; offset += 2)
            total += (uint)(decoded[offset] + 256 * decoded[offset + 1]);
        var inverse = unchecked((ushort)(0u - total));
        decoded[Checksum] = (byte)inverse;
        decoded[Checksum + 1] = (byte)(inverse >> 8);
    }

    // Independent test oracle uses BigInteger powers and bit-by-bit fields, not the production arithmetic or packing.
    private static byte[] OracleEncode(byte[] decoded)
    {
        var encoded = new byte[5062];
        for (var block = 0; block < 562; block++)
        for (var field = 0; field < 8; field++)
            WriteCode(encoded, block, field, (int)BigInteger.ModPow(decoded[block * 8 + field], 5, 299));
        SetOuterChecksum(encoded);
        return encoded;
    }

    private static void WriteCode(byte[] encoded, int block, int field, int code)
    {
        for (var bit = 0; bit < 9; bit++)
        {
            var position = field * 9 + bit;
            var offset = 4 + block * 9 + position / 8;
            var mask = 1 << (position % 8);
            encoded[offset] = (byte)((encoded[offset] & ~mask) | (((code >> bit) & 1) * mask));
        }
    }

    private static byte[] OracleDecode(byte[] encoded)
    {
        Require(encoded.Length == 5062, "Encoded extent differs.");
        var decoded = new byte[4496];
        for (var block = 0; block < 562; block++)
        for (var field = 0; field < 8; field++)
        {
            var code = 0;
            for (var bit = 0; bit < 9; bit++)
            {
                var position = field * 9 + bit;
                code |= ((encoded[4 + block * 9 + position / 8] >> (position % 8)) & 1) << bit;
            }
            Require(CanonicalCodes.TryGetValue(code, out var value), "Output contains a noncanonical field.");
            decoded[block * 8 + field] = value;
        }
        return decoded;
    }

    private static void SetOuterChecksum(byte[] encoded)
    {
        uint checksum = 0x4576ED83;
        for (var lane = 0; lane < 4; lane++)
        {
            var value = (int)((checksum >> (lane * 8)) & 255);
            for (var offset = 4 + lane; offset < 4496; offset += 4) value -= encoded[offset];
            encoded[lane] = unchecked((byte)value);
        }
    }

    private static void Validate(byte[] encoded, byte[] decoded)
    {
        var expected = encoded.ToArray();
        SetOuterChecksum(expected);
        Require(encoded.AsSpan(0, 4).SequenceEqual(expected.AsSpan(0, 4)), "Legacy outer checksum differs.");
        var wrongWholeFile = encoded.ToArray();
        for (var lane = 0; lane < 4; lane++)
        {
            var total = lane switch { 0 => 0x83, 1 => 0xED, 2 => 0x76, _ => 0x45 };
            for (var offset = 4 + lane; offset < encoded.Length; offset += 4) total -= encoded[offset];
            wrongWholeFile[lane] = unchecked((byte)total);
        }
        Require(!encoded.AsSpan(0, 4).SequenceEqual(wrongWholeFile.AsSpan(0, 4)), "Fixture does not distinguish the legacy checksum window.");
        uint inner = 0;
        for (var offset = Payload; offset <= Checksum; offset += 2)
            inner += (uint)(decoded[offset] + 256 * decoded[offset + 1]);
        Require((inner & 65535) == 0, "Inner checksum differs.");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(decoded.AsSpan(0x12))
            == Enumerable.Range(0, 1115).Sum(slot => decoded[QuantityOffset(slot)]), "Derived copy total differs.");
        Require(decoded.AsSpan(Payload + 0x117A, 8).SequenceEqual("YUGIOH01"u8)
            && BinaryPrimitives.ReadUInt16LittleEndian(decoded.AsSpan(Payload + 0x1182)) == 0xFBA5,
            "Signature changed.");
        Require(OracleEncode(decoded).SequenceEqual(encoded), "Output is not canonical.");
        Require(CollectionSave.ReadQuantities(encoded).SequenceEqual(Enumerable.Range(0, 1115).Select(slot => decoded[QuantityOffset(slot)])),
            "Existing quantity reader behavior differs.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
