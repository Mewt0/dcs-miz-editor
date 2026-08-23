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
    private static readonly Encoding LuaFileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
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

            var script = new Script(CoreModules.None);
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
        catch (Exception ex)
        {
            // Если не удалось прочитать mapResource — вернём пустой словарь
            throw new InvalidDataException(UserMessages.Get("LocalizationReadFailed", path), ex);
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
        File.WriteAllText(path, "mapResource = " + LuaTableSerializer.SerializeTable(table), LuaFileEncoding);
    }

    public (string Key, string FileName, string FullPath) AddResourceFile(string locale, string sourcePath, ResourceKind kind)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException(UserMessages.Get("SourcePathEmpty"), nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(UserMessages.Get("ResourceFileMissing"), sourcePath);

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
            throw new ArgumentException(UserMessages.Get("ResourceKeyEmpty"), nameof(resourceKey));
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException(UserMessages.Get("SourcePathEmpty"), nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(UserMessages.Get("ResourceFileMissing"), sourcePath);

        locale = NormalizeLocale(locale);
        var map = LoadMapResource(locale);
        if (!map.TryGetValue(resourceKey, out var oldFileName) || string.IsNullOrWhiteSpace(oldFileName))
            throw new KeyNotFoundException(UserMessages.Get("ResourceKeyMissing", resourceKey, locale));

        var targetDir = Path.Combine(L10nRoot, locale);
        Directory.CreateDirectory(targetDir);

        var sourceExt = Path.GetExtension(sourcePath);
        var oldExt = Path.GetExtension(oldFileName);
        var isImageReplacement = ImageReplacement.IsSupported(oldFileName) && ImageReplacement.IsSupported(sourcePath);
        var targetFileName = isImageReplacement || oldExt.Equals(sourceExt, StringComparison.OrdinalIgnoreCase)
            ? oldFileName
            : MakeUniqueFileName(targetDir, Path.GetFileName(sourcePath));
        var targetPath = Path.Combine(targetDir, targetFileName);

        if (!oldFileName.Equals(targetFileName, StringComparison.OrdinalIgnoreCase))
        {
            var oldPath = Path.Combine(targetDir, oldFileName);
            if (File.Exists(oldPath))
                File.Delete(oldPath);
        }

        if (isImageReplacement)
            ImageReplacement.CopyNormalized(sourcePath, targetPath);
        else
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
        locale = NormalizeLocale(locale);
        Directory.CreateDirectory(Path.Combine(L10nRoot, locale));
        EnsureEmptyMapResource(locale);
    }

    public void DeleteLocale(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return;
        if (locale.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase)) return;

        var dir = Path.Combine(L10nRoot, NormalizeLocale(locale));
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    public IReadOnlyList<string> PruneRedundantLocaleCopies(string locale, string referenceLocale = "DEFAULT")
    {
        locale = NormalizeLocale(locale);
        referenceLocale = NormalizeLocale(referenceLocale);
        if (locale.Equals(referenceLocale, StringComparison.OrdinalIgnoreCase))
            return Array.Empty<string>();

        var localeDir = Path.Combine(L10nRoot, locale);
        var referenceDir = Path.Combine(L10nRoot, referenceLocale);
        if (!Directory.Exists(localeDir) || !Directory.Exists(referenceDir))
            return Array.Empty<string>();

        var removed = new List<string>();
        foreach (var localePath in Directory.EnumerateFiles(localeDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(localeDir, localePath);
            if (IsLocaleControlFile(relative))
                continue;

            var referencePath = Path.Combine(referenceDir, relative);
            if (!File.Exists(referencePath))
                continue;
            if (!FilesAreByteIdentical(localePath, referencePath))
                continue;

            File.Delete(localePath);
            removed.Add(relative);
        }

        RemoveEmptyDirectories(localeDir);
        return removed;
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

    public bool SetBriefingString(MissionLua mission, string missionKey, string value, string locale)
    {
        var current = mission.GetString(missionKey);
        if (current.StartsWith("DictKey_", StringComparison.OrdinalIgnoreCase))
        {
            // обновляем dictionary по ключу
            WriteToDictionary(current, locale, value);
            return false;
        }

        // A translated locale must never inject localized text into the mission Lua.
        // Doing that forces a full reserialization of `mission` and can break complex
        // third-party missions. Direct mission fields are editable only in DEFAULT.
        if (!NormalizeLocale(locale).Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
            return false;

        else
        {
            // прямое значение в mission
            if (current == value)
                return false;

            mission.SetString(missionKey, value);
            return true;
        }
    }

    public void ExportTxt(MissionLua mission, string locale, string outPath)
    {
        var lines = new[]
        {
            "format=mizedit-v2",
            $"locale={locale}",
            $"name={EscapeTxtValue(ResolveBriefingString(mission, "name", locale))}",
            $"descriptionText={EscapeTxtValue(ResolveBriefingString(mission, "descriptionText", locale))}",
            $"descriptionRedTask={EscapeTxtValue(ResolveBriefingString(mission, "descriptionRedTask", locale))}",
            $"descriptionBlueTask={EscapeTxtValue(ResolveBriefingString(mission, "descriptionBlueTask", locale))}",
        };

        File.WriteAllLines(outPath, lines);
    }

    public bool ImportTxt(MissionLua mission, string locale, string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(UserMessages.Get("TxtFileMissing"), path);

        var lines = File.ReadAllLines(path);
        var escapedFormat = lines.Any(line => line.Equals("format=mizedit-v2", StringComparison.OrdinalIgnoreCase));
        var missionChanged = false;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..];
            if (escapedFormat)
                value = UnescapeTxtValue(value);

            switch (key)
            {
                case "name":
                    missionChanged |= SetBriefingString(mission, "name", value, locale);
                    break;
                case "descriptionText":
                    missionChanged |= SetBriefingString(mission, "descriptionText", value, locale);
                    break;
                case "descriptionRedTask":
                    missionChanged |= SetBriefingString(mission, "descriptionRedTask", value, locale);
                    break;
                case "descriptionBlueTask":
                    missionChanged |= SetBriefingString(mission, "descriptionBlueTask", value, locale);
                    break;
                default:
                    break;
            }
        }

        return missionChanged;
    }

    private static string EscapeTxtValue(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static string UnescapeTxtValue(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current != '\\' || index + 1 >= value.Length)
            {
                result.Append(current);
                continue;
            }

            var escaped = value[++index];
            result.Append(escaped switch
            {
                'n' => '\n',
                'r' => '\r',
                '\\' => '\\',
                _ => escaped
            });
        }
        return result.ToString();
    }

    private string GetFromDictionary(string dictKey, string locale)
    {
        var dict = LoadDictionary(locale);
        return dict.TryGetValue(dictKey, out var val) ? val : string.Empty;
    }

    private bool WriteToDictionary(string dictKey, string locale, string value)
    {
        var dict = LoadDictionary(locale);
        var normalizedValue = value ?? string.Empty;
        if (dict.TryGetValue(dictKey, out var current) &&
            string.Equals(current, normalizedValue, StringComparison.Ordinal))
        {
            return false;
        }

        dict[dictKey] = normalizedValue;
        SaveDictionary(locale, dict);
        return true;
    }

    public string? GetDictionaryValue(string locale, string dictKey)
    {
        return GetFromDictionary(dictKey, locale);
    }

    public void UpdateDictionaryEntry(string locale, string dictKey, string value)
    {
        WriteToDictionary(dictKey, locale, value);
    }

    public IReadOnlyDictionary<string, string> GetDictionaryEntries(string locale)
    {
        return LoadDictionary(NormalizeLocale(locale));
    }

    public void UpdateDictionaryEntries(string locale, IEnumerable<KeyValuePair<string, string>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var normalizedLocale = NormalizeLocale(locale);
        var dictionary = LoadDictionary(normalizedLocale);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
                continue;

            dictionary[entry.Key] = entry.Value ?? string.Empty;
        }

        SaveDictionary(normalizedLocale, dictionary);
    }

    public void UpdateDictionaryEntriesWithDefaultFallback(string locale, IEnumerable<KeyValuePair<string, string>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var normalizedLocale = NormalizeLocale(locale);
        if (normalizedLocale.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
        {
            UpdateDictionaryEntries(normalizedLocale, entries);
            return;
        }

        var reference = LoadDictionary("DEFAULT");
        var existing = LoadDictionary(normalizedLocale);
        var merged = new Dictionary<string, string>(reference, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in existing)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
                continue;

            if (!reference.ContainsKey(entry.Key) || !string.IsNullOrWhiteSpace(entry.Value))
                merged[entry.Key] = entry.Value ?? string.Empty;
        }

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
                continue;

            merged[entry.Key] = entry.Value ?? string.Empty;
        }

        SaveDictionary(normalizedLocale, merged);
        EnsureEmptyMapResource(normalizedLocale);
    }

    private void EnsureEmptyMapResource(string locale)
    {
        locale = NormalizeLocale(locale);
        if (locale.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
            return;

        var path = Path.Combine(L10nRoot, locale, "mapResource");
        if (File.Exists(path))
            return;

        SaveMapResource(locale, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
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

            var script = new Script(CoreModules.None);
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
        catch (Exception ex)
        {
            // Если не удалось прочитать dictionary — вернём пустой словарь
            throw new InvalidDataException(UserMessages.Get("DictionaryReadFailed", path), ex);
        }

        return dict;
    }

    private void SaveDictionary(string locale, Dictionary<string, string> dict)
    {
        locale = NormalizeLocale(locale);
        Directory.CreateDirectory(Path.Combine(L10nRoot, locale));
        var path = Path.Combine(L10nRoot, locale, "dictionary");

        var table = new Table(new Script());
        foreach (var kv in dict.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            table.Set(kv.Key, DynValue.NewString(kv.Value));
        }

        var serialized = LuaTableSerializer.SerializeTable(table);
        var text = "dictionary = " + serialized;
        File.WriteAllText(path, text, LuaFileEncoding);
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

    private static bool IsLocaleControlFile(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return fileName.Equals("dictionary", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("mapResource", StringComparison.OrdinalIgnoreCase);
    }

    private static bool FilesAreByteIdentical(string leftPath, string rightPath)
    {
        var left = new FileInfo(leftPath);
        var right = new FileInfo(rightPath);
        if (left.Length != right.Length)
            return false;

        const int BufferSize = 1024 * 64;
        using var leftStream = File.OpenRead(leftPath);
        using var rightStream = File.OpenRead(rightPath);
        var leftBuffer = new byte[BufferSize];
        var rightBuffer = new byte[BufferSize];

        while (true)
        {
            var leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
            var rightRead = rightStream.Read(rightBuffer, 0, rightBuffer.Length);
            if (leftRead != rightRead)
                return false;
            if (leftRead == 0)
                return true;

            for (var i = 0; i < leftRead; i++)
            {
                if (leftBuffer[i] != rightBuffer[i])
                    return false;
            }
        }
    }

    private static void RemoveEmptyDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
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
