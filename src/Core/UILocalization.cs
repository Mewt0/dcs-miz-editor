using System.Text.Json;
using System.Windows;
using System.IO;
using System.Linq;

namespace MizEdit.Core;

public static class UILocalization
{
    private const string DefaultLanguage = "RU";
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "EN", "RU"
    };

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MizEdit",
        "ui-settings.json");

    private static string _currentLanguage = DefaultLanguage;

    public static event Action<string>? LanguageChanged;

    public static string CurrentLanguage => _currentLanguage;

    public static void Initialize()
    {
        var savedLanguage = ReadSavedLanguage();
        ApplyLanguage(savedLanguage, save: false);
    }

    public static void SetLanguage(string language) => ApplyLanguage(language, save: true);

    public static string Get(string key)
    {
        return Application.Current.TryFindResource(key) as string ?? $"[{key}]";
    }

    public static string GetCurrentLanguage() => _currentLanguage;

    public static List<string> GetAvailableLanguages() => ["EN", "RU"];

    private static void ApplyLanguage(string? language, bool save)
    {
        var normalized = SupportedLanguages.Contains(language ?? string.Empty)
            ? language!.ToUpperInvariant()
            : DefaultLanguage;

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains("Resources/Strings.", StringComparison.OrdinalIgnoreCase) == true);
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"/Resources/Strings.{normalized.ToLowerInvariant()}.xaml", UriKind.Relative)
        };

        if (existing == null)
            dictionaries.Add(replacement);
        else
            dictionaries[dictionaries.IndexOf(existing)] = replacement;

        var changed = !string.Equals(_currentLanguage, normalized, StringComparison.OrdinalIgnoreCase);
        _currentLanguage = normalized;
        if (save)
            SaveLanguage(normalized);
        if (changed)
            LanguageChanged?.Invoke(normalized);
    }

    private static string ReadSavedLanguage()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return DefaultLanguage;
            var settings = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(SettingsPath));
            return settings?.Language ?? DefaultLanguage;
        }
        catch
        {
            return DefaultLanguage;
        }
    }

    private static void SaveLanguage(string language)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new UiSettings(language)));
        }
        catch
        {
            // A read-only profile must not prevent MizEdit from running.
        }
    }

    private sealed record UiSettings(string Language);
}
