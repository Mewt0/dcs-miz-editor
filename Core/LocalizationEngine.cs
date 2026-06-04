using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;

namespace MizEdit.Core;

public sealed class LocalizationEngine
{
    public LocalizationEngine(string workDir)
    {
        WorkDir = workDir ?? throw new ArgumentNullException(nameof(workDir));
    }

    public string WorkDir { get; }

    private string L10nRoot => Path.Combine(WorkDir, "l10n");

    public enum ResourceKind
    {
        Generic,
        Picture,
        Audio,
        Script
    }

    public Dictionary<string, string> LoadMapResource(string locale)
    {
        var path = Path.Combine(L10nRoot, locale, "mapResource");
        var resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
            return resources;

        try
        {
            var text = File.ReadAllText(path).TrimStart('\uFEFF');
            var code = BuildMapResourceWrapper(text);

            var script = new Script(CoreModules.Preset_Complete);
            script.DoString(code);

            var dyn = script.Globals.Get("mapResource");
            if (dyn.Type != DataType.Table) return resources;

            foreach (var pair in dyn.Table.Pairs)
            {
                if (pair.Key.Type == DataType.String && pair.Value.Type == DataType.String)
                {
                    resources[pair.Key.String] = pair.Value.String;
                }
            }
        }
        catch
        {
            // Если не удалось прочитать mapResource — вернём пустой словарь
        }

        return resources;
    }

    public Dictionary<string, string> LoadMapResourceWithFallback(string locale)
    {
        locale = NormalizeLocale(locale);
        if (locale.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
            return LoadMapResource("DEFAULT");

        var merged = LoadMapResource("DEFAULT");
        foreach (var kv in LoadMapResource(locale))
            merged[kv.Key] = kv.Value;

        return merged;
    }

    public void SaveMapResource(string locale, Dictionary<string, string> resources)
    {
        var dir = Path.Combine(L10nRoot, NormalizeLocale(locale));
        Directory.CreateDirectory(dir);

        var table = new Table(new Script());
        foreach (var kv in resources.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            table.Set(kv.Key, DynValue.NewString(kv.Value));
        }

        var path = Path.Combine(dir, "mapResource");
        File.WriteAllText(path, "mapResource = " + LuaTableSerializer.SerializeTable(table), Encoding.UTF8);
    }

    public (string Key, string FileName, string FullPath) AddResourceFile(string locale, string sourcePath, ResourceKind kind)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Source file path is empty.", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Resource file not found.", sourcePath);

        locale = NormalizeLocale(locale);
        var targetDir = Path.Combine(L10nRoot, locale);
        Directory.CreateDirectory(targetDir);

        var fileName = MakeUniqueFileName(targetDir, Path.GetFileName(sourcePath));
        var targetPath = Path.Combine(targetDir, fileName);
        File.Copy(sourcePath, targetPath);

        var map = LoadMapResource(locale);
        var key = MakeUniqueResourceKey(map, fileName, kind);
        map[key] = fileName;
        SaveMapResource(locale, map);

        return (key, fileName, targetPath);
    }

    public (string FileName, string FullPath) ReplaceResourceFile(string locale, string resourceKey, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(resourceKey))
            throw new ArgumentException("Resource key is empty.", nameof(resourceKey));
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Source file path is empty.", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Resource file not found.", sourcePath);

        locale = NormalizeLocale(locale);
        var map = LoadMapResource(locale);
        if (!map.TryGetValue(resourceKey, out var oldFileName) || string.IsNullOrWhiteSpace(oldFileName))
            throw new KeyNotFoundException($"Resource key '{resourceKey}' was not found in locale '{locale}'.");

        var targetDir = Path.Combine(L10nRoot, locale);
        Directory.CreateDirectory(targetDir);

        var sourceExt = Path.GetExtension(sourcePath);
        var oldExt = Path.GetExtension(oldFileName);
        var targetFileName = oldExt.Equals(sourceExt, StringComparison.OrdinalIgnoreCase)
            ? oldFileName
            : MakeUniqueFileName(targetDir, Path.GetFileName(sourcePath));
        var targetPath = Path.Combine(targetDir, targetFileName);

        if (!oldFileName.Equals(targetFileName, StringComparison.OrdinalIgnoreCase))
        {
            var oldPath = Path.Combine(targetDir, oldFileName);
            if (File.Exists(oldPath))
                File.Delete(oldPath);
        }

        File.Copy(sourcePath, targetPath, overwrite: true);
        map[resourceKey] = targetFileName;
        SaveMapResource(locale, map);

        return (targetFileName, targetPath);
    }

    public bool RemoveResource(string locale, string resourceKey, bool deletePhysicalFile)
    {
        if (string.IsNullOrWhiteSpace(resourceKey))
            return false;

        locale = NormalizeLocale(locale);
        var map = LoadMapResource(locale);
        if (!map.TryGetValue(resourceKey, out var fileName))
            return false;

        map.Remove(resourceKey);
        SaveMapResource(locale, map);

        if (deletePhysicalFile && !string.IsNullOrWhiteSpace(fileName))
        {
            var fullPath = Path.Combine(L10nRoot, locale, fileName);
            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }

        return true;
    }

    public string? ResolveResourceFile(string locale, string resourceOrFileName)
    {
        if (string.IsNullOrWhiteSpace(resourceOrFileName))
            return null;

        locale = NormalizeLocale(locale);
        var value = resourceOrFileName.Trim();
        var map = LoadMapResource(locale);

        if (map.TryGetValue(value, out var mappedFile))
            return Path.Combine(L10nRoot, locale, mappedFile);

        var localePath = Path.Combine(L10nRoot, locale, value);
        if (File.Exists(localePath))
            return localePath;

        if (!locale.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
        {
            var defaultMap = LoadMapResource("DEFAULT");
            if (defaultMap.TryGetValue(value, out mappedFile))
                return Path.Combine(L10nRoot, "DEFAULT", mappedFile);

            var defaultPath = Path.Combine(L10nRoot, "DEFAULT", value);
            if (File.Exists(defaultPath))
                return defaultPath;
        }

        return null;
    }

    private static string BuildMapResourceWrapper(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("mapResource", StringComparison.Ordinal))
            return text;
        if (trimmed.StartsWith("return", StringComparison.Ordinal))
        {
            var rest = trimmed.Substring("return".Length).TrimStart();
            return "mapResource = " + rest;
        }
        if (trimmed.StartsWith("{"))
            return "mapResource = " + trimmed;
        return "mapResource = " + trimmed;
    }

    public IReadOnlyList<string> GetLocales()
    {
        var locales = new List<string>();

        if (Directory.Exists(L10nRoot))
        {
            locales.AddRange(Directory.GetDirectories(L10nRoot)
                .Select(Path.GetFileName)
                .Where(name => name != null)
                .Select(name => name!));
        }

        if (!locales.Contains("DEFAULT"))
            locales.Insert(0, "DEFAULT");

        return locales;
    }

    public void AddLocale(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return;
        Directory.CreateDirectory(Path.Combine(L10nRoot, NormalizeLocale(locale)));
    }

    public void DeleteLocale(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return;
        if (locale.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase)) return;

        var dir = Path.Combine(L10nRoot, NormalizeLocale(locale));
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    public string ResolveBriefingString(MissionLua mission, string missionKey, string locale)
    {
        var raw = mission.GetString(missionKey);
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        if (raw.StartsWith("DictKey_", StringComparison.OrdinalIgnoreCase))
        {
            var dictVal = GetFromDictionary(raw, locale);
            if (!string.IsNullOrEmpty(dictVal)) return dictVal;

            // fallback на DEFAULT
            dictVal = GetFromDictionary(raw, "DEFAULT");
            if (!string.IsNullOrEmpty(dictVal)) return dictVal;

            // Если нигде не нашли — вернем сам ключ, чтобы было видно
            return raw;
        }

        return raw;
    }

    public void SetBriefingString(MissionLua mission, string missionKey, string value, string locale)
    {
        var current = mission.GetString(missionKey);
        if (current.StartsWith("DictKey_", StringComparison.OrdinalIgnoreCase))
        {
            // обновляем dictionary по ключу
            WriteToDictionary(current, locale, value);
        }
        else
        {
            // прямое значение в mission
            mission.SetString(missionKey, value);
        }
    }

    public void ExportTxt(MissionLua mission, string locale, string outPath)
    {
        var lines = new[]
        {
            $"locale={locale}",
            $"name={ResolveBriefingString(mission, "name", locale)}",
            $"descriptionText={ResolveBriefingString(mission, "descriptionText", locale)}",
            $"descriptionRedTask={ResolveBriefingString(mission, "descriptionRedTask", locale)}",
            $"descriptionBlueTask={ResolveBriefingString(mission, "descriptionBlueTask", locale)}",
        };

        File.WriteAllLines(outPath, lines);
    }

    public void ImportTxt(MissionLua mission, string locale, string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("TXT not found", path);

        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..];

            switch (key)
            {
                case "name":
                    SetBriefingString(mission, "name", value, locale);
                    break;
                case "descriptionText":
                    SetBriefingString(mission, "descriptionText", value, locale);
                    break;
                case "descriptionRedTask":
                    SetBriefingString(mission, "descriptionRedTask", value, locale);
                    break;
                case "descriptionBlueTask":
                    SetBriefingString(mission, "descriptionBlueTask", value, locale);
                    break;
                default:
                    break;
            }
        }
    }

    private string GetFromDictionary(string dictKey, string locale)
    {
        var dict = LoadDictionary(locale);
        return dict.TryGetValue(dictKey, out var val) ? val : string.Empty;
    }

    private void WriteToDictionary(string dictKey, string locale, string value)
    {
        var dict = LoadDictionary(locale);
        dict[dictKey] = value ?? string.Empty;
        SaveDictionary(locale, dict);
    }

    public string? GetDictionaryValue(string locale, string dictKey)
    {
        return GetFromDictionary(dictKey, locale);
    }

    public void UpdateDictionaryEntry(string locale, string dictKey, string value)
    {
        WriteToDictionary(dictKey, locale, value);
    }

    private Dictionary<string, string> LoadDictionary(string locale)
    {
        var path = Path.Combine(L10nRoot, locale, "dictionary");
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
            return dict;

        try
        {
            var text = File.ReadAllText(path).TrimStart('\uFEFF');
            var code = BuildDictionaryWrapper(text);

            var script = new Script(CoreModules.Preset_Complete);
            script.DoString(code);

            var dyn = script.Globals.Get("dictionary");
            if (dyn.Type != DataType.Table) return dict;

            foreach (var pair in dyn.Table.Pairs)
            {
                if (pair.Key.Type == DataType.String && pair.Value.Type == DataType.String)
                {
                    dict[pair.Key.String] = pair.Value.String;
                }
            }
        }
        catch
        {
            // Если не удалось прочитать dictionary — вернём пустой словарь
        }

        return dict;
    }

    private void SaveDictionary(string locale, Dictionary<string, string> dict)
    {
        locale = NormalizeLocale(locale);
        Directory.CreateDirectory(Path.Combine(L10nRoot, locale));
        var path = Path.Combine(L10nRoot, locale, "dictionary");

        var table = new Table(new Script());
        foreach (var kv in dict)
        {
            table.Set(kv.Key, DynValue.NewString(kv.Value));
        }

        var serialized = LuaTableSerializer.SerializeTable(table);
        var text = "dictionary = " + serialized;
        File.WriteAllText(path, text, Encoding.UTF8);
    }

    private static string BuildDictionaryWrapper(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("dictionary", StringComparison.Ordinal))
            return text;
        if (trimmed.StartsWith("return", StringComparison.Ordinal))
        {
            var rest = trimmed.Substring("return".Length).TrimStart();
            return "dictionary = " + rest;
        }
        if (trimmed.StartsWith("{"))
            return "dictionary = " + trimmed;
        return "dictionary = " + trimmed;
    }

    private static string NormalizeLocale(string locale)
    {
        return string.IsNullOrWhiteSpace(locale) ? "DEFAULT" : locale.Trim();
    }

    private static string MakeUniqueFileName(string directory, string requestedFileName)
    {
        var clean = string.Join("_", requestedFileName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(clean))
            clean = "resource";

        var stem = Path.GetFileNameWithoutExtension(clean);
        var ext = Path.GetExtension(clean);
        var candidate = clean;
        var index = 1;

        while (File.Exists(Path.Combine(directory, candidate)))
        {
            candidate = $"{stem}_{index}{ext}";
            index++;
        }

        return candidate;
    }

    private static string MakeUniqueResourceKey(Dictionary<string, string> map, string fileName, ResourceKind kind)
    {
        var prefix = kind switch
        {
            ResourceKind.Picture => "ResKey_Pic_",
            ResourceKind.Audio => "ResKey_Snd_",
            ResourceKind.Script => "ResKey_Script_",
            _ => "ResKey_File_"
        };

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var safeStem = Regex.Replace(stem, @"[^A-Za-z0-9_]+", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(safeStem))
            safeStem = "Resource";

        var candidate = prefix + safeStem;
        var index = 1;
        while (map.ContainsKey(candidate))
        {
            candidate = $"{prefix}{safeStem}_{index}";
            index++;
        }

        return candidate;
    }
}
