using PowerOfChaos.Launcher.Core.Collections;

namespace PowerOfChaos.Launcher.Tests;

// Existing fake-session tests must never share the real user's transaction journal or lock.
internal static class TestCollectionProfiles
{
    public static string Root { get; set; } = Path.Combine(AppContext.BaseDirectory, ".local", "profile-tests");

    public static TemporaryCollectionService Create() => new(
        Path.Combine(Root, "profile-" + Guid.NewGuid().ToString("N")), reader: new PowerOfChaos.Launcher.Core.Collections.CardCollectionReader(_ => []), hasRunningGames: () => false);
}
