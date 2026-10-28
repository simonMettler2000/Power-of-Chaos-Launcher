using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PowerOfChaos.Launcher.Core.Collections;

namespace PowerOfChaos.Launcher.Core.Localization;

/// <summary>Launcher-authored UI text. Game names, card names and arbitrary diagnostic details remain data.</summary>
public sealed class LauncherText
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _catalogs = LoadCatalogs();
    private readonly Dictionary<string, LocalizedTextEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _entryLock = new();
    private readonly Template[] _templates;
    private string _language = "en";

    public static LauncherText Current { get; } = new();

    private LauncherText()
    {
        // Only these complete, launcher-authored Core diagnostics accept already formatted input.
        // Every literal is escaped and both ends are anchored. Captures are never word-replaced.
        _templates = new[]
        {
            "Could not start the game: {0}",
            "The session could not be monitored: {0}",
            "Session tracking stopped: {0}",
            "Search was limited in {0}. Choose a more specific game folder.",
            "Could not completely inspect {0}.",
            "Ambiguous game filenames in {0}; files differ only by letter case.",
            "{0}: installation is incomplete; data.dat and Voice.dat are required beside the executable.",
            "{0}: the files in {1} are not a supported Power of Chaos installation.",
            "{0}: some installation files could not be read.",
            "Restore Original Aspect Ratio needs the verified local component bundle. Run the documented component setup, or untick this mod. Missing: {0}",
            "The original aspect ratio component is damaged or the wrong version: {0}. Run the documented component setup again, or untick this mod."
        }.Select(key => new Template(key, key is "Could not start the game: {0}" or "The session could not be monitored: {0}" or "Session tracking stopped: {0}")).ToArray();
    }

    public string Language => _language;
    public CultureInfo Culture => CultureInfo.GetCultureInfo(_language);
    public event EventHandler? LanguageChanged;

    /// <summary>Call on the UI thread, as with other changes to bound UI state.</summary>
    public void SetLanguage(string? language)
    {
        var normalized = CardLanguages.Normalize(language);
        if (_language == normalized) return;
        _language = normalized;
        LocalizedTextEntry[] entries;
        lock (_entryLock) entries = _entries.Values.ToArray();
        foreach (var entry in entries) entry.Refresh();
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Translate(string english) => Translate(english, 0);

    private string Translate(string english, int depth)
    {
        ArgumentNullException.ThrowIfNull(english);
        if (_catalogs[_language].TryGetValue(english, out var translated)) return translated;
        if (_language == "en" || english.Length > 32768 || depth >= 4) return english;
        foreach (var template in _templates)
        {
            Match match;
            try { match = template.Pattern.Match(english); }
            catch (RegexMatchTimeoutException) { return english; }
            if (!match.Success) continue;
            var values = Enumerable.Range(0, template.ArgumentCount).Select(index =>
            {
                var value = match.Groups[index + 1].Value;
                return (object)(template.TranslateDetail ? Translate(value, depth + 1) : value);
            }).ToArray();
            return string.Format(Culture, _catalogs[_language][template.Key], values);
        }
        return english;
    }

    public string Format(string englishTemplate, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var values = args.Select(value => value is LocalizedTextArgument argument ? argument.Render(this) : value).ToArray();
        return string.Format(Culture, Translate(englishTemplate), values);
    }

    /// <summary>Only authored catalog keys are retained. Unknown external input has no global cache or subscription.</summary>
    public LocalizedTextEntry Entry(string english)
    {
        ArgumentNullException.ThrowIfNull(english);
        if (!_catalogs["en"].ContainsKey(english)) return new LocalizedTextEntry(this, english);
        lock (_entryLock)
        {
            if (!_entries.TryGetValue(english, out var entry))
                _entries.Add(english, entry = new LocalizedTextEntry(this, english));
            return entry;
        }
    }

    internal IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalogs => _catalogs;
    internal int CachedEntryCount { get { lock (_entryLock) return _entries.Count; } }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadCatalogs()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var assembly = typeof(LauncherText).Assembly;
        foreach (var language in CardLanguages.All)
        {
            using var stream = assembly.GetManifestResourceStream($"PowerOfChaos.Launcher.Core.Localization.Strings.{language.Id}.json")
                ?? throw new InvalidOperationException($"Missing launcher text catalog: {language.Id}.");
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? throw new InvalidOperationException($"Invalid launcher text catalog: {language.Id}.");
            result.Add(language.Id, new ReadOnlyDictionary<string, string>(values));
        }
        var english = result["en"];
        foreach (var catalog in result.Values)
        {
            if (!english.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(catalog.Keys))
                throw new InvalidOperationException("Launcher text catalogs have different keys.");
            foreach (var pair in catalog)
            {
                if (string.IsNullOrWhiteSpace(pair.Value) || !Placeholders(pair.Key).SequenceEqual(Placeholders(pair.Value)))
                    throw new InvalidOperationException($"Invalid launcher text placeholders: {pair.Key}.");
            }
        }
        return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>(result);
    }

    internal static IEnumerable<string> Placeholders(string value) => Regex.Matches(value, @"\{\d+(?:,[^{}]+)?(?::[^{}]+)?\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
        .Select(match => match.Value).Order(StringComparer.Ordinal);

    private sealed class Template
    {
        public Template(string key, bool translateDetail)
        {
            Key = key;
            TranslateDetail = translateDetail;
            var expression = new StringBuilder(@"\A");
            var cursor = 0;
            foreach (Match placeholder in Regex.Matches(key, @"\{\d+\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            {
                expression.Append(Regex.Escape(key[cursor..placeholder.Index])).Append("(.+?)");
                cursor = placeholder.Index + placeholder.Length;
                ArgumentCount++;
            }
            expression.Append(Regex.Escape(key[cursor..])).Append(@"\z");
            Pattern = new Regex(expression.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking,
                TimeSpan.FromMilliseconds(50));
        }
        public string Key { get; }
        public bool TranslateDetail { get; }
        public int ArgumentCount { get; }
        public Regex Pattern { get; }
    }
}

/// <summary>Explicitly marks a launcher-authored argument for translation, including nested formatted text.</summary>
public sealed record LocalizedTextArgument(string English, params object?[] Arguments)
{
    internal string Render(LauncherText text) => Arguments.Length == 0 ? text.Translate(English) : text.Format(English, Arguments);
}

public sealed class LocalizedTextEntry : INotifyPropertyChanged
{
    private readonly LauncherText _owner;
    private readonly string _english;
    internal LocalizedTextEntry(LauncherText owner, string english) { _owner = owner; _english = english; }
    public string Value => _owner.Translate(_english);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
    public override string ToString() => Value;
}
