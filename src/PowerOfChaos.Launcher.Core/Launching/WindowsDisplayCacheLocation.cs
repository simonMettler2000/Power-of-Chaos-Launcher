using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PowerOfChaos.Launcher.Core.Launching;

/// <summary>Uses a private short Windows cache when an ANSI display component cannot use the user's normal profile path.</summary>
internal sealed class WindowsDisplayCacheLocation
{
    internal const string UnavailableMessage = "A secure short ASCII display cache could not be created. Check access to ProgramData, or choose a short ASCII launcher data folder.";
    private const string LegacyPathMessage = "Restore Original Aspect Ratio requires a short ASCII-only launcher data path. Set POC_LAUNCHER_DATA_DIR to a short path using only ASCII characters, or untick this mod.";
    private static readonly string[] TrustedSidValues = ["S-1-5-18", "S-1-5-32-544"];
    private readonly string? _privateAnchor;
    private readonly string? _ownerSid;

    private WindowsDisplayCacheLocation(string root, string? privateAnchor, string? ownerSid)
    {
        Root = Path.GetFullPath(root);
        _privateAnchor = privateAnchor;
        _ownerSid = ownerSid;
    }

    internal string Root { get; }

    internal static WindowsDisplayCacheLocation Select(string dataDirectory)
    {
        var preferred = Path.GetFullPath(Path.Combine(dataDirectory, "display", "v1"));
        if (CanUseNativeRoot(preferred)) return new(preferred, null, null);
        if (!OperatingSystem.IsWindows()) throw new InvalidDataException(LegacyPathMessage);
        return FromFallbackBase(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    }

    /// <summary>Provides an explicit owned base for synthetic tests; normal launches use the Windows known folder.</summary>
    [SupportedOSPlatform("windows")]
    internal static WindowsDisplayCacheLocation FromFallbackBase(string asciiBaseDirectory)
    {
        var parent = Path.GetFullPath(asciiBaseDirectory);
        if (!Path.IsPathFullyQualified(asciiBaseDirectory) || parent.Any(character => character > 0x7F || char.IsControl(character)))
            throw new InvalidDataException(UnavailableMessage);
        var owner = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidDataException(UnavailableMessage);
        var anchor = Path.Combine(parent, "PowerOfChaosLauncher-" + owner);
        var root = Path.Combine(anchor, "display", "v1");
        if (!CanUseNativeRoot(root)) throw new InvalidDataException(UnavailableMessage);
        return new(root, anchor, owner);
    }

    internal static bool CanUseNativeRoot(string root)
    {
        if (root.Any(character => character > 0x7F || char.IsControl(character))) return false;
        // Preserve the native module-local INI and screenshot prefix limits before any output is written.
        var bucket = Path.Combine(root, new string('0', 32));
        return Path.Combine(bucket, "ddraw.ini").Length < 260 && Path.Combine(bucket, "screenshots").Length + 1 < 260;
    }

    internal void EnsureCreated()
    {
        OriginalAspectRatioPreparer.RefuseLinks(Root);
        if (_privateAnchor is null)
        {
            Directory.CreateDirectory(Root);
            OriginalAspectRatioPreparer.RefuseLinks(Root);
            return;
        }
        if (!OperatingSystem.IsWindows()) throw new InvalidDataException(UnavailableMessage);
        try
        {
            var owner = WindowsIdentity.GetCurrent().User;
            if (owner is null || owner.Value != _ownerSid) throw new InvalidDataException(UnavailableMessage);
            var parent = Path.GetDirectoryName(_privateAnchor)!;
            OriginalAspectRatioPreparer.RefuseLinks(parent);
            if (!Directory.Exists(parent)) throw new InvalidDataException(UnavailableMessage);
            EnsurePrivateDirectory(_privateAnchor, owner);
            EnsurePrivateDirectory(Path.Combine(_privateAnchor, "display"), owner);
            EnsurePrivateDirectory(Root, owner);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new InvalidDataException(UnavailableMessage, error);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void EnsurePrivateDirectory(string path, SecurityIdentifier owner)
    {
        OriginalAspectRatioPreparer.RefuseLinks(path);
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            // Set the DACL at creation, rather than briefly exposing game-derived files in an inherited shared directory.
            var security = CreatePrivateSecurity(owner);
            directory = security.CreateDirectory(path);
        }
        OriginalAspectRatioPreparer.RefuseLinks(path);
        var observed = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (observed.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier actualOwner || actualOwner != owner
            || !observed.AreAccessRulesProtected || !HasPrivateRules(observed, owner))
            throw new InvalidDataException(UnavailableMessage);
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity CreatePrivateSecurity(SecurityIdentifier owner)
    {
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in TrustedSidValues.Select(value => new SecurityIdentifier(value)).Append(owner))
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static bool HasPrivateRules(DirectorySecurity security, SecurityIdentifier owner)
    {
        var expected = TrustedSidValues.Append(owner.Value).ToHashSet(StringComparer.Ordinal);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        if (rules.Count != expected.Count) return false;
        foreach (FileSystemAccessRule rule in rules)
            if (!expected.Remove(rule.IdentityReference.Value) || rule.AccessControlType != AccessControlType.Allow
                || rule.FileSystemRights != FileSystemRights.FullControl || rule.IsInherited
                || rule.InheritanceFlags != (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)
                || rule.PropagationFlags != PropagationFlags.None) return false;
        return expected.Count == 0;
    }
}
