namespace PowerOfChaos.Launcher.Core.Collections;

public sealed record CardLanguage(string Id, string DisplayName, string ResourceCode, int CodePage);

/// <summary>The localized name resources supplied by the games.</summary>
public static class CardLanguages
{
    public static IReadOnlyList<CardLanguage> All { get; } = Array.AsReadOnly(new[]
    {
        new CardLanguage("en", "English", "eng", 1252),
        new CardLanguage("de", "Deutsch", "ger", 1252),
        new CardLanguage("fr", "Français", "fra", 1252),
        new CardLanguage("it", "Italiano", "ita", 1252),
        new CardLanguage("es", "Español", "spa", 1252),
        new CardLanguage("ja", "日本語", "jpn", 932)
    });

    /// <summary>Unknown or absent preferences use English.</summary>
    public static string Normalize(string? value)
        => All.FirstOrDefault(language => language.Id.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id ?? "en";

    public static CardLanguage Get(string? value)
        => All.First(language => language.Id == Normalize(value));
}
