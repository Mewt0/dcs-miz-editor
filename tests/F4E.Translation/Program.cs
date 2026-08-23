using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using MizEdit.Core;
using MizEdit.Services;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: F4E.Translation <package.zip|mission.miz> <output.miz> [maxRows|--gemini|--sanitize] [missionNameHint]");
    return 2;
}

var sourcePath = Path.GetFullPath(args[0]);
var outputPath = Path.GetFullPath(args[1]);
var sanitizeOnly = args.Length >= 3 && args[2].Equals("--sanitize", StringComparison.OrdinalIgnoreCase);
var useGemini = args.Length >= 3 && args[2].Equals("--gemini", StringComparison.OrdinalIgnoreCase);
var reportOnly = args.Length >= 3 && args[2].Equals("--report", StringComparison.OrdinalIgnoreCase);
var agentExport = args.Length >= 3 && args[2].Equals("--agent-export", StringComparison.OrdinalIgnoreCase);
var agentImport = args.Length >= 3 && args[2].Equals("--agent-import", StringComparison.OrdinalIgnoreCase);
var localeStructureSmoke = args.Length >= 3 && args[2].Equals("--locale-structure-smoke", StringComparison.OrdinalIgnoreCase);
var maxRows = args.Length >= 3 && int.TryParse(args[2], out var parsedLimit)
    ? Math.Max(1, parsedLimit)
    : useGemini ? int.MaxValue : 25;
var missionNameHint = args.Length >= 4 ? args[3] : "First Day - Cold Start";
if (!File.Exists(sourcePath))
    throw new FileNotFoundException("Source mission package was not found.", sourcePath);

var temporaryRoot = Path.Combine(Path.GetTempPath(), "mizedit-f4e-translate-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);
try
{
    var missionPath = sourcePath;
    if (!Path.GetExtension(sourcePath).Equals(".miz", StringComparison.OrdinalIgnoreCase))
    {
        using var package = ZipFile.OpenRead(sourcePath);
        var missionEntry = package.Entries
            .Where(entry => entry.FullName.EndsWith(".miz", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(entry => entry.FullName.Contains(missionNameHint, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"No .miz entry matching '{missionNameHint}' was found.");
        missionPath = Path.Combine(temporaryRoot, Path.GetFileName(missionEntry.FullName));
        missionEntry.ExtractToFile(missionPath);
    }

    var missionService = new MissionService();
    using var translator = new OllamaTranslationService();
    using var session = missionService.LoadMission(missionPath);
    var source = session.Localization.GetDictionaryEntries("DEFAULT");
    if (localeStructureSmoke)
    {
        var originalMission = ReadArchiveEntryBytes(missionPath, "mission");
        var originalDefaultDictionary = ReadArchiveEntryBytes(missionPath, "l10n/DEFAULT/dictionary");
        var originalDefaultMapResource = ReadArchiveEntryBytesIfExists(missionPath, "l10n/DEFAULT/mapResource");
        var changedPair = source
            .Where(pair => LooksTranslatable(pair.Value) &&
                           !OllamaTranslationService.LooksLikeLuaScript(pair.Value))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(changedPair.Key))
            throw new InvalidDataException("No safe translatable dictionary entry was found for the locale structure smoke.");

        session.Localization.AddLocale("RU");
        session.Localization.UpdateDictionaryEntriesWithDefaultFallback(
            "RU",
            new[] { new KeyValuePair<string, string>(changedPair.Key, "[RU smoke] " + changedPair.Value) });
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        missionService.SaveAsMiz(session, outputPath);

        AssertBytesEqual("mission", originalMission, ReadArchiveEntryBytes(outputPath, "mission"));
        AssertBytesEqual("l10n/DEFAULT/dictionary", originalDefaultDictionary, ReadArchiveEntryBytes(outputPath, "l10n/DEFAULT/dictionary"));
        AssertBytesEqual("l10n/DEFAULT/mapResource", originalDefaultMapResource, ReadArchiveEntryBytesIfExists(outputPath, "l10n/DEFAULT/mapResource"));

        using var reopened = missionService.LoadMission(outputPath);
        var outputDefault = reopened.Localization.GetDictionaryEntries("DEFAULT");
        var outputRu = reopened.Localization.GetDictionaryEntries("RU");
        if (outputDefault.Count != source.Count || source.Any(pair => !outputDefault.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw new InvalidDataException("DEFAULT dictionary changed semantically after RU locale save.");
        if (source.Any(pair => !outputRu.ContainsKey(pair.Key)))
            throw new InvalidDataException("RU dictionary does not contain all DEFAULT keys.");
        if (outputRu[changedPair.Key] != "[RU smoke] " + changedPair.Value)
            throw new InvalidDataException("RU changed entry was not written.");

        var fallbackPair = source.First(pair => !pair.Key.Equals(changedPair.Key, StringComparison.OrdinalIgnoreCase));
        if (outputRu[fallbackPair.Key] != fallbackPair.Value)
            throw new InvalidDataException("RU dictionary did not fallback-fill an unchanged DEFAULT entry.");
        if (reopened.Localization.LoadMapResource("RU").Count != 0)
            throw new InvalidDataException("RU mapResource should be empty when no localized resources are added.");

        Console.WriteLine($"LOCALE_STRUCTURE_SMOKE_OK sourceKeys={source.Count}, ruKeys={outputRu.Count}, changedKey={changedPair.Key}, output={outputPath}");
        return 0;
    }
if (agentExport)
{
    var exportDirectory = outputPath;
    Directory.CreateDirectory(exportDirectory);
    var chunkSize = args.Length >= 4 && int.TryParse(args[3], out var parsedChunkSize)
        ? Math.Clamp(parsedChunkSize, 5, 80)
        : 80;
    var exportItems = source
        .Where(pair => LooksTranslatable(pair.Value) &&
                       !OllamaTranslationService.LooksLikeLuaScript(pair.Value))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select((pair, index) => new AgentTranslationItem(index + 1, pair.Key, pair.Value, string.Empty))
            .ToArray();
        var options = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };
    for (var start = 0; start < exportItems.Length; start += chunkSize)
        {
            var chunkNumber = start / chunkSize + 1;
            var chunk = exportItems.Skip(start).Take(chunkSize).ToArray();
            var chunkPath = Path.Combine(exportDirectory, $"chunk-{chunkNumber:D3}.jsonl");
            var lines = chunk.Select(item => JsonSerializer.Serialize(item, options));
            await File.WriteAllLinesAsync(chunkPath, lines, new UTF8Encoding(false));
        }
        var manifest = new
        {
            Mission = Path.GetFileName(missionPath),
            Total = exportItems.Length,
            ChunkSize = chunkSize,
            Chunks = (int)Math.Ceiling(exportItems.Length / (double)chunkSize),
            Format = "jsonl: { number, key, source, translation }"
        };
        await File.WriteAllTextAsync(
            Path.Combine(exportDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            new UTF8Encoding(false));
        Console.WriteLine($"AGENT_EXPORT mission={Path.GetFileName(missionPath)}, rows={exportItems.Length}, chunks={manifest.Chunks}, output={exportDirectory}");
        return 0;
    }

    if (agentImport)
    {
        if (args.Length < 4)
            throw new InvalidOperationException("Pass a translation JSONL directory or file after --agent-import.");

        var translationSource = Path.GetFullPath(args[3]);
        session.Localization.AddLocale("RU");
        session.Localization.UpdateDictionaryEntries("RU", source);

        var translationFiles = Directory.Exists(translationSource)
            ? Directory.GetFiles(translationSource, "*.jsonl", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
            : new[] { translationSource };
        var importOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        var translations = new List<KeyValuePair<string, string>>();
        var skipped = 0;
        foreach (var translationFile in translationFiles)
        {
            foreach (var line in await File.ReadAllLinesAsync(translationFile, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var item = JsonSerializer.Deserialize<AgentTranslationItem>(line, importOptions);
                if (item is null || string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Translation))
                {
                    skipped++;
                    continue;
                }
                translations.Add(new KeyValuePair<string, string>(item.Key, item.Translation));
            }
        }

        session.Localization.UpdateDictionaryEntries("RU", translations);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        missionService.SaveAsMiz(session, outputPath);
        Console.WriteLine($"AGENT_IMPORT translated={translations.Count}, skipped={skipped}, output={outputPath}");
        return 0;
    }

    if (reportOnly)
    {
        var target = session.Localization.GetDictionaryEntries("RU");
        var changed = source
            .Where(pair => target.TryGetValue(pair.Key, out var translated) && translated != pair.Value)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var report = new StringBuilder();
        report.AppendLine("MizEdit — Ollama translation sample");
        report.AppendLine($"Mission: {Path.GetFileName(missionPath)}");
        report.AppendLine($"Changed dictionary entries: {changed.Length}");
        report.AppendLine();
        foreach (var pair in changed)
        {
            report.AppendLine(new string('=', 80));
            report.AppendLine($"KEY: {pair.Key}");
            report.AppendLine("ORIGINAL:");
            report.AppendLine(pair.Value);
            report.AppendLine();
            report.AppendLine("OLLAMA RU:");
            report.AppendLine(target[pair.Key]);
            report.AppendLine();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, report.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"REPORT entries={changed.Length}, output={outputPath}");
        return 0;
    }

    session.Localization.AddLocale("RU");
    if (sanitizeOnly)
    {
        var target = session.Localization.GetDictionaryEntries("RU");
        var repairs = source
            .Where(pair => OllamaTranslationService.LooksLikeLuaScript(pair.Value))
            .Where(pair => target.TryGetValue(pair.Key, out var translated) && translated != pair.Value)
            .ToArray();
        session.Localization.UpdateDictionaryEntries("RU", repairs);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        missionService.SaveAsMiz(session, outputPath);
        Console.WriteLine($"SANITIZED restoredLuaEntries={repairs.Length}, output={outputPath}");
        return 0;
    }

    session.Localization.UpdateDictionaryEntries("RU", source);

    var targets = source
        .Where(pair => LooksTranslatable(pair.Value) &&
                       !OllamaTranslationService.LooksLikeLuaScript(pair.Value))
        .OrderByDescending(pair => pair.Value.Length)
        .Take(maxRows)
        .Select(pair => new TranslationEntry(pair.Key, pair.Value, string.Empty))
        .ToArray();

    if (useGemini)
    {
        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Set GEMINI_API_KEY before using --gemini.");

        Console.WriteLine($"TRANSLATE mission={Path.GetFileName(missionPath)}, candidates={targets.Length}, model=gemini-3.5-flash-lite");
        using var gemini = new GeminiBatchTranslationService(apiKey);
        var progress = new Progress<(int Completed, int Total)>(value =>
            Console.WriteLine($"PROGRESS {value.Completed}/{value.Total}"));
        var result = await gemini.TranslateAllAsync(
            targets.Select(entry => new KeyValuePair<string, string>(entry.Key, entry.SourceText)).ToArray(),
            progress);
        var completedByGemini = result.Translations
            .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value))
            .ToArray();
        session.Localization.UpdateDictionaryEntries("RU", completedByGemini);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        missionService.SaveAsMiz(session, outputPath);
        Console.WriteLine($"DONE translated={completedByGemini.Length}, errors={result.Failures.Count}, output={outputPath}");
        foreach (var failure in result.Failures)
            Console.WriteLine($"ERROR key={failure.Key}, message={failure.Message}");
        return result.Failures.Count == 0 ? 0 : 1;
    }

    Console.WriteLine($"TRANSLATE mission={Path.GetFileName(missionPath)}, candidates={targets.Length}, model={OllamaTranslationService.DefaultModel}");
    var runner = new TranslationQueueRunner(translator);
    runner.State.PropertyChanged += (_, eventArgs) =>
    {
        if (eventArgs.PropertyName == nameof(TranslationQueueState.Processed))
            Console.WriteLine($"PROGRESS {runner.State.Processed}/{runner.State.Total}");
    };
    await runner.RunAsync(targets, overwriteExisting: true);

    var completed = targets
        .Where(entry => entry.WorkStatus == TranslationWorkStatus.Completed)
        .Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Translation))
        .ToArray();
    session.Localization.UpdateDictionaryEntries("RU", completed);
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    missionService.SaveAsMiz(session, outputPath);

    Console.WriteLine($"DONE translated={completed.Length}, skipped={runner.State.Skipped}, errors={runner.State.Errors.Count}, output={outputPath}");
    foreach (var error in runner.State.Errors)
        Console.WriteLine($"ERROR key={error.Key}, kind={error.Kind}, message={error.Message}");
    return runner.State.Errors.Count == 0 ? 0 : 1;
}
finally
{
    try { Directory.Delete(temporaryRoot, recursive: true); } catch { }
}

static bool LooksTranslatable(string value)
{
    if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, "[A-Za-z]{2}"))
        return false;

    var trimmed = value.Trim();
    return !Regex.IsMatch(trimmed, @"^[A-Z0-9_.:/+\-]+$");
}

static byte[] ReadArchiveEntryBytes(string archivePath, string entryName)
{
    var bytes = ReadArchiveEntryBytesIfExists(archivePath, entryName);
    if (bytes is null)
        throw new InvalidDataException($"Archive entry was not found: {entryName}");

    return bytes;
}

static byte[]? ReadArchiveEntryBytesIfExists(string archivePath, string entryName)
{
    using var archive = ZipFile.OpenRead(archivePath);
    var entry = archive.GetEntry(entryName);
    if (entry is null)
        return null;

    using var stream = entry.Open();
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    return memory.ToArray();
}

static void AssertBytesEqual(string entryName, byte[]? expected, byte[]? actual)
{
    if (expected is null && actual is null)
        return;
    if (expected is null || actual is null || !expected.SequenceEqual(actual))
        throw new InvalidDataException($"{entryName} changed unexpectedly.");
}

public sealed record AgentTranslationItem(
    int Number,
    string Key,
    string Source,
    string Translation);
