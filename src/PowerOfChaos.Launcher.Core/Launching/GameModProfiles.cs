namespace PowerOfChaos.Launcher.Core.Launching;

/// <summary>Factual numeric profiles for exact installed revisions; no game code or new instructions.</summary>
internal static class GameModProfiles
{
    internal static IReadOnlyList<SplashProfile> Profiles { get; } = Create();

    private static IReadOnlyList<SplashProfile> Create()
    {
        var profiles = new List<SplashProfile>(SkipSplashPatch.Profiles);

        // Yugi: optional presentation callbacks and voice/intro fields only.
        Add(profiles, "yugi",
            "78fc90f601986c7865c01659e1ef7df016ed48ca92d3cdb2b08f5cc2a8897338",
            "91f77587fab00cec692d665e071668d9cd381d3f9e5f6cafac314980af52407c",
            [
                // Optional callback: existing function 0x401CA0 -> existing no-op 0x402600.
                new ScalarChange(0x19A4AC, 0x19A4AC, 4201632u, 4204032u, 4),
                // Optional callback: existing function 0x401E00 -> existing no-op 0x402600.
                new ScalarChange(0x19A4B0, 0x19A4B0, 4201984u, 4204032u, 4),
                // Optional callback: existing function 0x402090 -> existing no-op 0x402600.
                new ScalarChange(0x19A4B4, 0x19A4B4, 4202640u, 4204032u, 4),
                // Optional callback: existing function 0x4022F0 -> existing no-op 0x402600.
                new ScalarChange(0x19A4B8, 0x19A4B8, 4203248u, 4204032u, 4),
                // Optional callback: existing function 0x402330 -> existing no-op 0x402600.
                new ScalarChange(0x19A4BC, 0x19A4BC, 4203312u, 4204032u, 4),
                // Optional callback: existing function 0x4023D0 -> existing no-op 0x402600.
                new ScalarChange(0x19A4C0, 0x19A4C0, 4203472u, 4204032u, 4),
                // Optional callback: existing function 0x4024C0 -> existing no-op 0x402600.
                new ScalarChange(0x19A4C4, 0x19A4C4, 4203712u, 4204032u, 4),
                // Optional callback: existing function 0x4025F0 -> existing no-op 0x402600.
                new ScalarChange(0x19A4C8, 0x19A4C8, 4204016u, 4204032u, 4),
                // Nonblocking character voice ID -> native invalid ID 0; no waiting presentation is created.
                new ScalarChange(0x271B, 0x271B, 268435515u, 0u, 4),
                // Nonblocking character voice ID -> native invalid ID 0; no waiting presentation is created.
                new ScalarChange(0x2E8F, 0x2E8F, 268435527u, 0u, 4),
                // Nonblocking character voice ID -> native invalid ID 0; no waiting presentation is created.
                new ScalarChange(0x6A67, 0x6A67, 268435550u, 0u, 4),
                // Fusion voice cue only: unreachable signed -1 (clock advances in multiples of four).
                new ScalarChange(0x3A483, 0x3A483, 64u, 255u, 1),
                // Fusion voice cue only: unreachable positive timer; normal fusion sounds and rendering remain.
                new ScalarChange(0x3A498, 0x3A498, 384u, 2147483647u, 4),
                // Fusion voice cue only: unreachable positive timer; normal fusion sounds and rendering remain.
                new ScalarChange(0x3A4A8, 0x3A4A8, 128u, 2147483647u, 4),
                // Phase-zero branch operand: existing target 0x40FCE6 -> clean epilogue 0x40FCF4.
                new ScalarChange(0xFC9E, 0xFC9E, 71u, 85u, 1),
                // Optional phase-zero voice 44/45: skip allocation and append, retaining native phase increment.
                new ScalarChange(0x6326, 0x6326, 168u, 256u, 4),
                // Phase-three voice 65 wait: existing case 0x406B69 -> common bookkeeping 0x406BEF.
                // The following phase-four native queue check and card animation remain.
                new ScalarChange(0x6C30, 0x6C30, 0x406B69u, 0x406BEFu, 4),
            ]);

        // Kaiba: optional presentation callbacks and voice/intro fields only.
        Add(profiles, "kaiba",
            "547ce2e4c78434e375029b3c5958dc6b5e3c42ae8521f6ee4d2131ac2c872465",
            "4d8b1c994fd41f5e34a15a2626bd46c7fc110da725d19e921070315295f8cd03",
            [
                // Optional callback: existing function 0x401C80 -> existing no-op 0x5864D0.
                new ScalarChange(0x19D4AC, 0x19D4AC, 4201600u, 5792976u, 4),
                // Optional callback: existing function 0x401E40 -> existing no-op 0x5864D0.
                new ScalarChange(0x19D4B0, 0x19D4B0, 4202048u, 5792976u, 4),
                // Optional callback: existing function 0x4021A0 -> existing no-op 0x5864D0.
                new ScalarChange(0x19D4B4, 0x19D4B4, 4202912u, 5792976u, 4),
                // Optional callback: existing function 0x4023A0 -> existing no-op 0x5864D0.
                new ScalarChange(0x19D4B8, 0x19D4B8, 4203424u, 5792976u, 4),
                // Optional callback: existing function 0x4023E0 -> existing no-op 0x5864D0.
                new ScalarChange(0x19D4BC, 0x19D4BC, 4203488u, 5792976u, 4),
                // Optional callback: existing function 0x402460 -> existing no-op 0x5864D0.
                new ScalarChange(0x19D4C0, 0x19D4C0, 4203616u, 5792976u, 4),
                // Optional callback: existing function 0x4028E0 -> existing no-op 0x5864D0.
                new ScalarChange(0x19D4C4, 0x19D4C4, 4204768u, 5792976u, 4),
                // Optional callback: existing function 0x402570 -> existing no-op 0x5864D0.
                new ScalarChange(0x19D4C8, 0x19D4C8, 4203888u, 5792976u, 4),
                // Optional callback: existing function 0x4025B0 -> existing no-op 0x5864D0.
                new ScalarChange(0x19D4CC, 0x19D4CC, 4203952u, 5792976u, 4),
                // Phase-zero branch operand: existing target 0x40FA46 -> clean epilogue 0x40FA54.
                new ScalarChange(0xF9FE, 0xF9FE, 71u, 85u, 1),
                // Phase-zero branch operand: existing target 0x4031B4 -> clean epilogue 0x4031CB.
                new ScalarChange(0x30B1, 0x30B1, 255u, 278u, 4),
                // Phase-zero branch operand: existing target 0x406692 -> clean epilogue 0x4066EA.
                new ScalarChange(0x65E6, 0x65E6, 168u, 256u, 4),
                // Optional voice CALL operand: existing target 0x402700 -> existing no-op 0x5864D0.
                new ScalarChange(0x14E4A3, 0x14E4A3, 4293608025u, 229417u, 4),
                // Fusion voice cue only: unreachable positive timer; normal fusion sounds and rendering remain.
                new ScalarChange(0x3ABC2, 0x3ABC2, 384u, 2147483647u, 4),
                // Fusion voice cue only: unreachable positive timer; normal fusion sounds and rendering remain.
                new ScalarChange(0x3ABD2, 0x3ABD2, 128u, 2147483647u, 4),
            ]);

        // Joey: optional presentation callbacks and voice/intro fields only.
        Add(profiles, "joey",
            "9f2f201f41a9e181cfc47ea96be0511e62f38a64a0663d6a661638c6b3e57889",
            "4030136b8311a7b8d7a2503b86bee6da253d18e0db7ad8b9dc950bf061bb78b6",
            [
                // Optional callback: existing function 0x402C20 -> existing no-op 0x4484D0.
                new ScalarChange(0x1DA3D8, 0x1DA3D8, 4205600u, 4490448u, 4),
                // Optional callback: existing function 0x403140 -> existing no-op 0x4484D0.
                new ScalarChange(0x1DA3DC, 0x1DA3DC, 4206912u, 4490448u, 4),
                // Optional callback: existing function 0x403360 -> existing no-op 0x4484D0.
                new ScalarChange(0x1DA3E0, 0x1DA3E0, 4207456u, 4490448u, 4),
                // Optional callback: existing function 0x403750 -> existing no-op 0x4484D0.
                new ScalarChange(0x1DA3E4, 0x1DA3E4, 4208464u, 4490448u, 4),
                // Optional callback: existing function 0x4037C0 -> existing no-op 0x4484D0.
                new ScalarChange(0x1DA3E8, 0x1DA3E8, 4208576u, 4490448u, 4),
                // Optional callback: existing function 0x403850 -> existing no-op 0x4484D0.
                new ScalarChange(0x1DA3EC, 0x1DA3EC, 4208720u, 4490448u, 4),
                // Optional callback: existing function 0x403950 -> existing no-op 0x4484D0.
                new ScalarChange(0x1DA3F0, 0x1DA3F0, 4208976u, 4490448u, 4),
                // Optional callback: existing function 0x403B70 -> existing no-op 0x4484D0.
                new ScalarChange(0x1DA3F4, 0x1DA3F4, 4209520u, 4490448u, 4),
                // Optional callback: existing function 0x403BC0 -> existing no-op 0x4484D0.
                new ScalarChange(0x1DA3F8, 0x1DA3F8, 4209600u, 4490448u, 4),
                // Optional voice CALL operand: existing target 0x44BCE0 -> existing constant-true gate 0x580FB0.
                new ScalarChange(0x3DC1, 0x3DC1, 294683u, 1561067u, 4),
                // Optional voice CALL operand: existing target 0x44BCE0 -> existing constant-true gate 0x580FB0.
                new ScalarChange(0x3E21, 0x3E21, 294587u, 1560971u, 4),
                // Optional voice CALL operand: existing target 0x44BCE0 -> existing constant-true gate 0x580FB0.
                new ScalarChange(0x3E61, 0x3E61, 294523u, 1560907u, 4),
                // Optional voice CALL operand: existing target 0x44BCE0 -> existing constant-true gate 0x580FB0.
                new ScalarChange(0x3F11, 0x3F11, 294347u, 1560731u, 4),
                // Phase-zero branch operand: existing target 0x418469 -> clean epilogue 0x4184D3.
                new ScalarChange(0x18359, 0x18359, 268u, 374u, 4),
            ]);
        return profiles.AsReadOnly();
    }

    private static void Add(List<SplashProfile> profiles, string gameId, string characterHash, string combinedHash,
        IReadOnlyList<ScalarChange> characterChanges)
    {
        var splash = SkipSplashPatch.Profiles.Single(profile => profile.GameId == gameId);
        profiles.Add(splash with
        {
            Options = new GameModOptions(SkipCharacterInteractions: true),
            ModifiedHash = characterHash,
            Changes = Array.AsReadOnly(characterChanges.ToArray())
        });
        profiles.Add(splash with
        {
            Options = new GameModOptions(SkipSplashScreens: true, SkipCharacterInteractions: true),
            ModifiedHash = combinedHash,
            Changes = Array.AsReadOnly(splash.Changes.Concat(characterChanges).ToArray())
        });
    }
}
