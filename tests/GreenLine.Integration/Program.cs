using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MizEdit.Core;
using MizEdit.Services;
using NAudio.Vorbis;
using NAudio.Wave;
using System.Windows.Media.Imaging;

var briefingLocaleSmokeOnly = args.Length == 1 && args[0].Equals("--briefing-locale-smoke", StringComparison.OrdinalIgnoreCase);
if (briefingLocaleSmokeOnly)
{
    ValidateLocalizedBriefingDoesNotRewriteMission();
    return 0;
}

var localeSmokeOnly = args.Length == 1 && args[0].Equals("--locale-smoke", StringComparison.OrdinalIgnoreCase);
var sourcePackage = args.Length == 1 && !localeSmokeOnly
    ? Path.GetFullPath(args[0])
    : Path.Combine(Environment.CurrentDirectory, "_FA-18C_Operation_Green_Line-RU_briefing_v2.zip");
if (!localeSmokeOnly && !File.Exists(sourcePackage))
    throw new FileNotFoundException("Green Line regression ZIP was not found. Pass its path as the only argument.", sourcePackage);
ValidateSessionState();
ValidateTranslationBatchDocument();
ValidateProtectedTokenIntegrity();
ValidateLuaTranslationGuard();
await ValidateTranslationQueueAsync();
await ValidateTranslationCheckpointAsync();
await ValidateTranslationWorkspaceViewModelAsync();
await ValidateSaveAnalysisAsync();
ValidateRuLocaleDoesNotCloneDefaultResources();
ValidateLocalizedBriefingDoesNotRewriteMission();
if (localeSmokeOnly)
    return 0;
var testRoot = Path.Combine(Path.GetTempPath(), "mizedit-green-line-" + Guid.NewGuid().ToString("N"));
var inputRoot = Path.Combine(testRoot, "input");
var outputRoot = Path.Combine(testRoot, "output");
Directory.CreateDirectory(inputRoot);
Directory.CreateDirectory(outputRoot);

var missionFiles = new List<string>();
if (Path.GetExtension(sourcePackage).Equals(".miz", StringComparison.OrdinalIgnoreCase))
{
    missionFiles.Add(sourcePackage);
}
else
{
    using var package = ZipFile.OpenRead(sourcePackage);
    foreach (var entry in package.Entries.Where(entry => entry.FullName.EndsWith(".miz", StringComparison.OrdinalIgnoreCase)))
    {
        var destination = Path.Combine(inputRoot, Path.GetFileName(entry.FullName));
        entry.ExtractToFile(destination);
        missionFiles.Add(destination);
    }
}

if (missionFiles.Count == 0)
    throw new InvalidDataException("The package contains no .miz missions.");

var service = new MissionService();
var failures = new List<string>();
var totalLocales = 0;
var totalDictionaryEntries = 0;
var totalMappedResources = 0;
var totalAudioFiles = 0;
var totalImageFiles = 0;
var totalScriptFiles = 0;
var totalTriggers = 0;
var totalRadioMessages = 0;
var totalDanglingResources = 0;
var totalDecodedAudioFiles = 0;
var totalDecodedImageFiles = 0;

foreach (var missionPath in missionFiles.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
{
    var missionName = Path.GetFileName(missionPath);
    Console.WriteLine($"TEST {missionName}");
    try
    {
        var originalSnapshot = SnapshotArchive(missionPath);
        var physical = CountPhysicalResources(originalSnapshot.Keys);
        totalAudioFiles += physical.Audio;
        totalImageFiles += physical.Images;
        totalScriptFiles += physical.Scripts;

        MissionMetrics before;
        using (var session = service.LoadMission(missionPath))
        {
            ValidateKnownF4ECorpusMetrics(session, missionName);
            totalDecodedAudioFiles += ValidateAudioFiles(session.Archive.WorkDir);
            totalDecodedImageFiles += ValidateImageFiles(session.Archive.WorkDir);
            var locales = session.Localization.GetLocales();
            totalLocales += locales.Count;
            var unresolved = new List<string>();
            var dictionaries = 0;
            var mappedResources = 0;

            foreach (var locale in locales)
            {
                dictionaries += session.Localization.GetDictionaryEntries(locale).Count;
                var map = session.Localization.LoadMapResource(locale);
                mappedResources += map.Count;
                foreach (var key in map.Keys)
                {
                    var resolved = session.Localization.ResolveResourceFile(locale, key);
                    if (resolved is null || !File.Exists(resolved))
                        unresolved.Add($"{locale}:{key}");
                }
            }

            if (unresolved.Count > 0)
            {
                totalDanglingResources += unresolved.Count;
                Console.WriteLine($"WARN source contains {unresolved.Count} dangling mapResource entry/entries; first: {unresolved[0]}");
            }

            totalDictionaryEntries += dictionaries;
            totalMappedResources += mappedResources;
            before = ReadMetrics(session);
            totalTriggers += before.Triggers;
            totalRadioMessages += before.RadioMessages;

            var outputPath = Path.Combine(outputRoot, missionName);
            service.SaveAsMiz(session, outputPath);
        }

        var savedPath = Path.Combine(outputRoot, missionName);
        var savedSnapshot = SnapshotArchive(savedPath);
        AssertPreservedEntries(originalSnapshot, savedSnapshot);

        using var reopened = service.LoadMission(savedPath);
        var after = ReadMetrics(reopened);
        if (before != after)
            throw new InvalidDataException($"Mission metrics changed after round-trip. Before={before}; After={after}");

        Console.WriteLine($"PASS {missionName}: locales={before.Locales}, dict={before.DictionaryEntries}, map={before.MappedResources}, triggers={before.Triggers}, radio={before.RadioMessages}");
    }
    catch (Exception ex)
    {
        failures.Add($"{missionName}: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine($"FAIL {missionName}: {ex.Message}");
    }
}

static void ValidateKnownF4ECorpusMetrics(MissionSession session, string missionName)
{
    if (!missionName.Equals("F-4E_TR_Caucasus_04_PILOT_VISUAL_LANDING.miz", StringComparison.OrdinalIgnoreCase))
        return;

    var entries = session.Localization.GetDictionaryEntries("DEFAULT")
        .Select(pair => new TranslationEntry(pair.Key, pair.Value, string.Empty))
        .ToArray();
    var allLines = TranslationBatchDocument.Build(entries);
    var excludedEmpty = 0;
    var excludedTechnical = 0;
    var visible = new List<TranslationBatchLine>();
    foreach (var group in allLines.GroupBy(line => line.Entry))
    {
        if (string.IsNullOrWhiteSpace(group.Key.SourceText))
            excludedEmpty += group.Count();
        else if (OllamaTranslationService.LooksLikeLuaScript(group.Key.SourceText))
            excludedTechnical += group.Count();
        else
            visible.AddRange(group);
    }

    var manifest = TranslationBatchDocument.BuildManifest(visible, deduplicate: true);
    var metrics = new TranslationCorpusMetrics(
        entries.Length,
        allLines.Count,
        visible.Select(line => line.Entry).Distinct().Count(),
        visible.Count,
        visible.Count(line => !string.IsNullOrWhiteSpace(line.SourceLine)),
        0,
        excludedEmpty,
        excludedTechnical,
        0,
        0,
        allLines.Count,
        0,
        manifest.ExportItems.Count,
        manifest.AliasCount);
    if (!metrics.HasConsistentPhysicalLineTotals ||
        metrics.TotalPhysicalLines != 3261 || metrics.VisibleLines != 2558 ||
        metrics.ExcludedEmptyLines != 645 || metrics.ExcludedTechnicalLines != 58)
    {
        throw new InvalidOperationException(
            $"Unexpected F-4E corpus metrics: total={metrics.TotalPhysicalLines}, visible={metrics.VisibleLines}, empty={metrics.ExcludedEmptyLines}, technical={metrics.ExcludedTechnicalLines}.");
    }

    Console.WriteLine(
        $"PASS F-4E corpus metrics: {metrics.TotalPhysicalLines} = {metrics.VisibleLines} visible + {metrics.ExcludedEmptyLines} empty + {metrics.ExcludedTechnicalLines} technical");
}

try
{
    RunMutationScenario(service, missionFiles[0], testRoot);
    Console.WriteLine("PASS backend mutation scenario: locale, dictionary, audio, picture, script, trigger, replace, remove, save, reopen");
}
catch (Exception ex)
{
    failures.Add($"Backend mutation scenario: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine($"FAIL backend mutation scenario: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine($"SUMMARY missions={missionFiles.Count}, failures={failures.Count}, locales={totalLocales}, dictionary={totalDictionaryEntries}, mapResources={totalMappedResources}, danglingSourceResources={totalDanglingResources}, audio={totalAudioFiles}, decodedAudio={totalDecodedAudioFiles}, images={totalImageFiles}, decodedImages={totalDecodedImageFiles}, scripts={totalScriptFiles}, triggers={totalTriggers}, radio={totalRadioMessages}");
foreach (var failure in failures)
    Console.WriteLine("ERROR " + failure);

try { Directory.Delete(testRoot, recursive: true); } catch { }
return failures.Count == 0 ? 0 : 1;

static void ValidateProtectedTokenIntegrity()
{
    var fragments = new[] { "251.0", "audio/message.ogg" };
    var valid = OllamaTranslationService.RestoreProtectedFragments(
        "Свяжитесь на __MIZEDIT_TOKEN_0000__ и включите __MIZEDIT_TOKEN_0001__",
        fragments);
    if (valid != "Свяжитесь на 251.0 и включите audio/message.ogg")
        throw new InvalidOperationException("Protected tokens were not restored exactly.");

    AssertProtectedTokenFailure(
        "Переставлено __MIZEDIT_TOKEN_0001__ затем __MIZEDIT_TOKEN_0000__",
        fragments,
        "reordered");
    AssertProtectedTokenFailure(
        "Повтор __MIZEDIT_TOKEN_0000__ и __MIZEDIT_TOKEN_0000__",
        fragments,
        "duplicated");
    AssertProtectedTokenFailure(
        "Потерян __MIZEDIT_TOKEN_0000__",
        fragments,
        "missing");
    AssertProtectedTokenFailure(
        "Подмена __MIZEDIT_TOKEN_9999__ и __MIZEDIT_TOKEN_0001__",
        fragments,
        "unknown");

    Console.WriteLine("PASS protected token integrity: exact order, count, identity");
}

static async Task ValidateTranslationWorkspaceViewModelAsync()
{
    var entries = Enumerable.Range(0, 10_000)
        .Select(index => new TranslationEntry(
            $"DictKey_ActionText_{index}",
            index % 10 == 0 ? "Repeated source" : $"Source line {index}",
            index % 3 == 0 ? $"Перевод {index}" : string.Empty))
        .ToArray();
    var model = new TranslationWorkspaceViewModel(text => text.StartsWith("return ", StringComparison.Ordinal));
    var options = new TranslationWorkspaceFilterOptions(Deduplicate: true);
    var firstInput = model.Capture(entries, options);
    var first = await model.BuildSnapshotAsync(firstInput);
    if (!model.IsCurrent(first) || !first.Metrics.HasConsistentPhysicalLineTotals)
        throw new InvalidDataException("Translation workspace produced a stale or inconsistent initial snapshot.");
    if (first.Metrics.TotalKeys != entries.Length || first.Metrics.DeduplicatedAliasLines != 999)
        throw new InvalidDataException("Translation workspace did not preserve 10k keys or deduplicate exact originals.");
    if (!first.Manifest.ExportItems.Select(item => item.ExportNumber).SequenceEqual(
            Enumerable.Range(1, first.Manifest.ExportItems.Count).Select(number => (int?)number)))
    {
        throw new InvalidDataException("Translation workspace manifest did not assign dense CompactNumbered export numbers.");
    }

    var secondInput = model.Capture(entries, options with { SearchText = "Source line 9999" });
    var second = await model.BuildSnapshotAsync(secondInput);
    if (model.IsCurrent(first) || !model.IsCurrent(second) || second.VisibleEntries.Count != 1)
        throw new InvalidDataException("Translation workspace generation/search filtering is not stable.");

    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try
    {
        await model.BuildSnapshotAsync(secondInput, cancelled.Token);
        throw new InvalidDataException("Translation workspace ignored cancellation.");
    }
    catch (OperationCanceledException)
    {
    }

    var backendEntry = new TranslationEntry(
        "DictKey_ActionComment_360",
        "DictKey_ActionComment_360",
        string.Empty);
    var backendInput = model.Capture(
        new[] { backendEntry },
        options with { HideTechnical = false, SearchText = string.Empty });
    var backendSnapshot = await model.BuildSnapshotAsync(backendInput);
    if (backendSnapshot.VisibleEntries.Count != 0 || backendSnapshot.BatchLines.Count != 0 ||
        backendSnapshot.Metrics.ExcludedTechnicalLines != 1)
    {
        throw new InvalidDataException("Self-referencing DictKey backend metadata leaked into the user workspace or export.");
    }

    Console.WriteLine("PASS translation workspace: 10k-key background snapshot, exact deduplication, stale guard, cancellation, hidden backend placeholders");
}

static async Task ValidateSaveAnalysisAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "mizedit-save-analysis-" + Guid.NewGuid().ToString("N"));
    var sourceRoot = Path.Combine(root, "source");
    var workRoot = Path.Combine(root, "work");
    var sourceMiz = Path.Combine(root, "source.miz");
    Directory.CreateDirectory(sourceRoot);
    Directory.CreateDirectory(Path.Combine(sourceRoot, "l10n", "DEFAULT"));
    File.WriteAllText(Path.Combine(sourceRoot, "mission"), "original mission");
    File.WriteAllText(Path.Combine(sourceRoot, "removed.txt"), "removed resource");
    File.WriteAllText(Path.Combine(sourceRoot, "l10n", "DEFAULT", "dictionary"), "dictionary");
    ZipFile.CreateFromDirectory(sourceRoot, sourceMiz);
    CopyDirectory(sourceRoot, workRoot);

    File.WriteAllText(Path.Combine(workRoot, "mission"), "modified mission");
    File.Delete(Path.Combine(workRoot, "removed.txt"));
    File.WriteAllText(Path.Combine(workRoot, "added.ogg"), "added resource");

    var entries = new[]
    {
        new TranslationSaveSnapshot("DictKey_ActionText_1", "Contact %s on 251.0 MHz", "Связь установлена", true),
        new TranslationSaveSnapshot("DictKey_ActionText_2", "Repeated source", "Первый вариант", true),
        new TranslationSaveSnapshot("DictKey_ActionText_3", "Repeated source", "Второй вариант", false),
        new TranslationSaveSnapshot("DictKey_ActionText_4", "if missioncode < 10 then\n    return true;\nelse\n    return false;\nend", string.Empty, true),
        new TranslationSaveSnapshot("DictKey_ActionText_5", "if count > 5 then", "если count > 5 then", true),
        new TranslationSaveSnapshot("DictKey_ActionText_6", "MIST framework loaded.", "MIST framework loaded.", true)
    };
    var baseline = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DictKey_ActionText_1"] = "Old",
        ["DictKey_ActionText_2"] = "Old",
        ["DictKey_ActionText_3"] = "Второй вариант"
    };

    try
    {
        var report = await new SaveAnalysisService().AnalyzeAsync(
            sourceMiz, workRoot, entries, baselineTranslations: baseline);
        if (report.AddedFileCount != 1 || report.RemovedFileCount != 1 || report.ModifiedFileCount != 1)
            throw new InvalidDataException("Save analysis archive diff did not classify added/removed/modified files.");
        if (report.ChangedTranslations.Length != 2 || !report.HasBlockingIssues)
            throw new InvalidDataException("Save analysis did not detect baseline translation changes and blocking QA issues.");
        if (!report.QualityIssues.Any(issue => issue.Kind == TranslationQualityIssueKind.PlaceholderMismatch) ||
            !report.QualityIssues.Any(issue => issue.Kind == TranslationQualityIssueKind.FrequencyMismatch) ||
            !report.QualityIssues.Any(issue => issue.Kind == TranslationQualityIssueKind.InconsistentTranslation) ||
            !report.QualityIssues.Any(issue => issue.Kind == TranslationQualityIssueKind.UnexpectedArchiveChange) ||
            !report.QualityIssues.Any(issue => issue.DictKey == "DictKey_ActionText_5" &&
                                               issue.Kind == TranslationQualityIssueKind.TechnicalSourceModified) ||
            report.QualityIssues.Any(issue => issue.DictKey == "DictKey_ActionText_4") ||
            report.QualityIssues.Any(issue => issue.DictKey == "DictKey_ActionText_6"))
        {
            throw new InvalidDataException("Save analysis quality validator missed placeholders, frequencies, inconsistent duplicates, technical-code guards, or unexpected archive changes.");
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await new SaveAnalysisService().AnalyzeAsync(sourceMiz, workRoot, entries, cancelled.Token, baseline);
            throw new InvalidDataException("Save analysis ignored cancellation.");
        }
        catch (OperationCanceledException)
        {
        }
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    Console.WriteLine("PASS save analysis: archive diff, translation baseline, QA blockers, background cancellation");
}

static void ValidateRuLocaleDoesNotCloneDefaultResources()
{
    var root = Path.Combine(Path.GetTempPath(), "mizedit-locale-clone-" + Guid.NewGuid().ToString("N"));
    var sourceRoot = Path.Combine(root, "source");
    var fixtureRoot = Path.Combine(root, "fixtures");
    var sourceMiz = Path.Combine(root, "source.miz");
    var outputMiz = Path.Combine(root, "output.miz");
    Directory.CreateDirectory(Path.Combine(sourceRoot, "l10n", "DEFAULT"));
    Directory.CreateDirectory(fixtureRoot);

    File.WriteAllText(Path.Combine(sourceRoot, "mission"), "mission = { name = \"DictKey_Name\" }");
    File.WriteAllText(
        Path.Combine(sourceRoot, "l10n", "DEFAULT", "dictionary"),
        "dictionary = { DictKey_Name = \"Default name\", DictKey_Line = \"Default line\" }");
    File.WriteAllText(
        Path.Combine(sourceRoot, "l10n", "DEFAULT", "mapResource"),
        "mapResource = { ResKey_Snd_Default = \"default.ogg\", ResKey_Pic_Default = \"default.png\", ResKey_Script_Default = \"default.lua\" }");
    File.WriteAllText(Path.Combine(sourceRoot, "l10n", "DEFAULT", "default.ogg"), "default audio");
    File.WriteAllText(Path.Combine(sourceRoot, "l10n", "DEFAULT", "default.wav"), "default wave");
    File.WriteAllText(Path.Combine(sourceRoot, "l10n", "DEFAULT", "default.png"), "default image");
    File.WriteAllText(Path.Combine(sourceRoot, "l10n", "DEFAULT", "default.jpg"), "default jpeg");
    File.WriteAllText(Path.Combine(sourceRoot, "l10n", "DEFAULT", "default.lua"), "return 'default'");
    ZipFile.CreateFromDirectory(sourceRoot, sourceMiz);

    var explicitAudio = Path.Combine(fixtureRoot, "ru-only.ogg");
    File.WriteAllText(explicitAudio, "ru audio");

    try
    {
        var originalSnapshot = SnapshotArchive(sourceMiz);
        var service = new MissionService();
        using (var session = service.LoadMission(sourceMiz))
        {
            session.Localization.AddLocale("RU");
            session.Localization.UpdateDictionaryEntriesWithDefaultFallback(
                "RU",
                new[] { new KeyValuePair<string, string>("DictKey_Line", "Русская строка") });
            var added = session.Localization.AddResourceFile("RU", explicitAudio, LocalizationEngine.ResourceKind.Audio);
            if (string.IsNullOrWhiteSpace(added.Key))
                throw new InvalidDataException("Explicit RU resource was not registered in mapResource.");
            service.SaveAsMiz(session, outputMiz);
        }

        var savedSnapshot = SnapshotArchive(outputMiz);
        foreach (var preservedEntry in new[] { "mission", "l10n/DEFAULT/dictionary", "l10n/DEFAULT/mapResource" })
        {
            if (!originalSnapshot.TryGetValue(preservedEntry, out var originalHash) ||
                !savedSnapshot.TryGetValue(preservedEntry, out var savedHash) ||
                !string.Equals(originalHash, savedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{preservedEntry} changed while saving an RU translation locale.");
            }
        }

        using var archive = ZipFile.OpenRead(outputMiz);
        var entries = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ruEntries = entries
            .Where(name => name.StartsWith("l10n/RU/", StringComparison.OrdinalIgnoreCase))
            .Select(name => name["l10n/RU/".Length..])
            .ToArray();

        if (!entries.Contains("l10n/DEFAULT/default.ogg") ||
            !entries.Contains("l10n/DEFAULT/default.png") ||
            !entries.Contains("l10n/DEFAULT/default.lua"))
        {
            throw new InvalidDataException("DEFAULT resource inventory changed during RU locale save.");
        }

        if (!ruEntries.Contains("dictionary", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("RU dictionary was not saved.");
        if (!ruEntries.Contains("mapResource", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("RU mapResource for the explicit resource was not saved.");
        if (!ruEntries.Contains("ru-only.ogg", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Explicit RU resource was not saved.");

        AssertArchiveEntryHasNoUtf8Bom(archive, "l10n/RU/dictionary");
        AssertArchiveEntryHasNoUtf8Bom(archive, "l10n/RU/mapResource");

        var inherited = ruEntries
            .Where(name => !name.Equals("dictionary", StringComparison.OrdinalIgnoreCase) &&
                           !name.Equals("mapResource", StringComparison.OrdinalIgnoreCase) &&
                           !name.Equals("ru-only.ogg", StringComparison.OrdinalIgnoreCase))
            .Where(name => Path.GetExtension(name).Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(name).Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(name).Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(name).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(name).Equals(".lua", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (inherited.Length > 0)
            throw new InvalidDataException("RU locale inherited DEFAULT resource files: " + string.Join(", ", inherited));

        using var reopened = service.LoadMission(outputMiz);
        var defaultDictionary = reopened.Localization.GetDictionaryEntries("DEFAULT");
        var ruDictionary = reopened.Localization.GetDictionaryEntries("RU");
        if (ruDictionary.Count != defaultDictionary.Count)
            throw new InvalidDataException($"RU dictionary is not complete: {ruDictionary.Count}/{defaultDictionary.Count}.");
        if (ruDictionary["DictKey_Name"] != "Default name")
            throw new InvalidDataException("RU dictionary did not inherit unchanged DEFAULT text.");
        if (ruDictionary["DictKey_Line"] != "Русская строка")
            throw new InvalidDataException("RU dictionary did not apply the translated override.");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    Console.WriteLine("PASS RU locale save: full dictionary fallback, untouched DEFAULT, explicit resources only");
}

static void ValidateLocalizedBriefingDoesNotRewriteMission()
{
    var root = Path.Combine(Path.GetTempPath(), "mizedit-briefing-locale-" + Guid.NewGuid().ToString("N"));
    var sourceRoot = Path.Combine(root, "source");
    var sourceMiz = Path.Combine(root, "source.miz");
    var outputMiz = Path.Combine(root, "output.miz");
    var defaultRoot = Path.Combine(sourceRoot, "l10n", "DEFAULT");
    Directory.CreateDirectory(defaultRoot);

    File.WriteAllText(
        Path.Combine(sourceRoot, "mission"),
        "mission = { sortie = \"DictKey_sortie_5\", descriptionText = \"DictKey_descriptionText_1\" }");
    File.WriteAllText(
        Path.Combine(defaultRoot, "dictionary"),
        "dictionary = { DictKey_sortie_5 = \"Visual landing\", DictKey_descriptionText_1 = \"Training mission\" }");
    File.WriteAllText(Path.Combine(defaultRoot, "mapResource"), "mapResource = {}");
    ZipFile.CreateFromDirectory(sourceRoot, sourceMiz);

    try
    {
        var originalSnapshot = SnapshotArchive(sourceMiz);
        var service = new MissionService();
        using (var session = service.LoadMission(sourceMiz))
        {
            session.Localization.AddLocale("RU");

            // Mirrors the F-4E save path: the UI title falls back from an absent
            // mission.name field to the existing mission.sortie DictKey.
            var missionChanged = false;
            missionChanged |= session.Localization.SetBriefingString(session.Mission, "name", "Визуальная посадка", "RU");
            missionChanged |= session.Localization.SetBriefingString(session.Mission, "sortie", "Визуальная посадка", "RU");
            missionChanged |= session.Localization.SetBriefingString(session.Mission, "descriptionText", "Учебная миссия", "RU");
            missionChanged |= session.Localization.SetBriefingString(session.Mission, "descriptionRedTask", "", "RU");
            missionChanged |= session.Localization.SetBriefingString(session.Mission, "descriptionBlueTask", "", "RU");
            if (missionChanged)
                session.MarkMissionDirty();

            if (session.IsMissionDirty)
                throw new InvalidDataException("A translated briefing marked mission Lua as dirty.");
            if (!string.IsNullOrEmpty(session.Mission.GetString("name")))
                throw new InvalidDataException("A translated briefing created the missing mission.name field.");

            service.SaveAsMiz(session, outputMiz);
        }

        var savedSnapshot = SnapshotArchive(outputMiz);
        if (!originalSnapshot.TryGetValue("mission", out var originalMissionHash) ||
            !savedSnapshot.TryGetValue("mission", out var savedMissionHash) ||
            !string.Equals(originalMissionHash, savedMissionHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("mission changed while saving localized briefing text.");
        }

        using (var archive = ZipFile.OpenRead(outputMiz))
        {
            AssertArchiveEntryHasNoUtf8Bom(archive, "l10n/RU/dictionary");
            AssertArchiveEntryHasNoUtf8Bom(archive, "l10n/RU/mapResource");
        }

        using var reopened = service.LoadMission(outputMiz);
        var ru = reopened.Localization.GetDictionaryEntries("RU");
        if (ru["DictKey_sortie_5"] != "Визуальная посадка" ||
            ru["DictKey_descriptionText_1"] != "Учебная миссия")
        {
            throw new InvalidDataException("Localized briefing dictionary values were not saved.");
        }
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    Console.WriteLine("PASS localized briefing save: absent fields preserved, mission Lua untouched");
}

static void AssertArchiveEntryHasNoUtf8Bom(ZipArchive archive, string entryName)
{
    var entry = archive.GetEntry(entryName)
        ?? throw new InvalidDataException($"Archive entry is missing: {entryName}");
    using var stream = entry.Open();
    var prefix = new byte[3];
    var read = stream.Read(prefix, 0, prefix.Length);
    if (read == 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
        throw new InvalidDataException($"DCS Lua file contains an unsupported UTF-8 BOM: {entryName}");
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(destination, Path.GetRelativePath(source, file));
        File.Copy(file, target);
    }
}

static void ValidateTranslationBatchDocument()
{
    ValidateMiz1BlockImport();
    ValidateCompactNumberedBatchImport();
    ValidateCompactNumberedLargePreviewSignals();
    ValidateCompactNumberedRejectedMarkers();
    ValidateLegacyBatchImport();
    ValidateRejectedBatchMarkers();
    ValidateDeduplicatedBatchImport();
    ValidateStaleBatchPlan();
    ValidateTranslationCorpusMetricsInvariant();
    ValidateNumberedDisplay();
    Console.WriteLine("PASS translation batch document: compact M2, MZ1/legacy input, rejection, deduplication, stale-plan guard, numbered UI");
}

static void ValidateCompactNumberedBatchImport()
{
    var first = new TranslationEntry("compact-1", "Alpha", string.Empty);
    var second = new TranslationEntry("compact-2", "Bravo", string.Empty);
    var manifest = TranslationBatchDocument.BuildManifest(
        TranslationBatchDocument.Build(new[] { first, second }),
        deduplicate: false);
    var export = TranslationBatchDocument.FormatSource(manifest, includeInstruction: true, TranslationBatchExportFormat.CompactNumbered);
    if (!export.Contains($"Batch: {manifest.BatchId}", StringComparison.Ordinal) ||
        !export.Contains("1» Alpha", StringComparison.Ordinal) ||
        !export.Contains("R Throttle Lever", StringComparison.Ordinal) ||
        !export.Contains("mist_4_5_107.lua loaded", StringComparison.Ordinal) ||
        export.Contains("[[M2|", StringComparison.Ordinal) ||
        export.Contains("[[MZ1|", StringComparison.Ordinal))
    {
        throw new InvalidDataException("CompactNumbered export did not produce clean Batch + N» text.");
    }

    var plan = TranslationBatchDocument.Analyze(
        $"Batch: {manifest.BatchId}\n1» Альфа\n2» Браво",
        manifest,
        overwriteExisting: true);
    if (plan.FoundCount != 2 || plan.ChangedCount != 2 || plan.MissingCount != 0 || !plan.CanApply)
        throw new InvalidDataException("CompactNumbered analysis did not parse a complete ideal response.");

    var result = TranslationBatchDocument.Apply(plan);
    if (!result.Success || first.Translation != "Альфа" || second.Translation != "Браво")
        throw new InvalidDataException("CompactNumbered import did not apply to the expected entries.");

    var technical = new TranslationEntry("compact-technical", "mist_4_5_107.lua loaded.", string.Empty);
    var technicalManifest = TranslationBatchDocument.BuildManifest(
        TranslationBatchDocument.Build(new[] { technical }),
        deduplicate: false);
    var escapedPlan = TranslationBatchDocument.Analyze(
        $"Batch: {technicalManifest.BatchId.Replace("_", "\\_", StringComparison.Ordinal)}\n1» mist\\_4\\_5\\_107.lua loaded.",
        technicalManifest,
        overwriteExisting: true);
    if (escapedPlan.HasFatalProblems || escapedPlan.Problems.Any(problem => problem.Kind == TranslationImportProblemKind.WrongBatchId))
        throw new InvalidDataException("Markdown-escaped Batch ID was rejected.");
    if (!escapedPlan.Items.Any(item => item.Flags.HasFlag(TranslationBatchImportFlags.MarkdownEscaped)))
        throw new InvalidDataException("Markdown-escaped translation text was not flagged in preview.");
    var escapedResult = TranslationBatchDocument.Apply(escapedPlan);
    if (!escapedResult.Success || technical.Translation != "mist_4_5_107.lua loaded.")
        throw new InvalidDataException("Markdown-escaped underscores leaked into imported technical text.");

    var fir = new TranslationEntry("compact-fir", "You have entered ANKARA FIR!", string.Empty);
    var firManifest = TranslationBatchDocument.BuildManifest(TranslationBatchDocument.Build(new[] { fir }), deduplicate: false);
    var firPlan = TranslationBatchDocument.Analyze(
        $"Batch: {firManifest.BatchId}\n1» Вы вошли в FIR ANKARA!",
        firManifest,
        overwriteExisting: true);
    if (!firPlan.Items.Any(item => item.Flags.HasFlag(TranslationBatchImportFlags.ProtectedTermChanged)))
        throw new InvalidDataException("Reordered ANKARA FIR was not flagged as a protected term change.");

    var code = new TranslationEntry("compact-code", "if (HDG_end < (HDG_start + 5)) then", string.Empty);
    var codeManifest = TranslationBatchDocument.BuildManifest(TranslationBatchDocument.Build(new[] { code }), deduplicate: false);
    var codePlan = TranslationBatchDocument.Analyze(
        $"Batch: {codeManifest.BatchId}\n1» если (HDG\\_end < (HDG\\_start + 5)) then",
        codeManifest,
        overwriteExisting: true);
    if (!codePlan.Items.Any(item => item.Flags.HasFlag(TranslationBatchImportFlags.TechnicalTextChanged) &&
                                    item.Flags.HasFlag(TranslationBatchImportFlags.MarkdownEscaped) &&
                                    item.Status == TranslationBatchImportStatus.Rejected))
        throw new InvalidDataException("Changed/escaped Lua-like condition was not flagged in preview.");
}

static void ValidateCompactNumberedLargePreviewSignals()
{
    var entries = Enumerable.Range(1, 1_000)
        .Select(number => new TranslationEntry(
            $"compact-large-{number}",
            $"Source {number}",
            number == 500 ? "RU 500" : string.Empty))
        .ToArray();
    var manifest = TranslationBatchDocument.BuildManifest(TranslationBatchDocument.Build(entries), deduplicate: false);

    var missingResponse = string.Join("\n",
        new[] { $"Batch: {manifest.BatchId}" }.Concat(
            Enumerable.Range(1, 1_000)
                .Where(number => number != 500)
                .Select(number => $"{number}» RU {number}")));
    var missingPlan = TranslationBatchDocument.Analyze(missingResponse, manifest, overwriteExisting: true);
    if (missingPlan.FoundCount != 999 || missingPlan.MissingCount != 1 ||
        missingPlan.Items.Single(item => item.Status == TranslationBatchImportStatus.Missing).ExportNumber != 500)
    {
        throw new InvalidDataException("CompactNumbered preview did not surface a missing #500 row.");
    }

    var unchangedResponse = string.Join("\n",
        new[] { $"Batch: {manifest.BatchId}" }.Concat(
            Enumerable.Range(1, 1_000).Select(number => $"{number}» RU {number}")));
    var unchangedPlan = TranslationBatchDocument.Analyze(unchangedResponse, manifest, overwriteExisting: true);
    if (unchangedPlan.FoundCount != 1_000 || unchangedPlan.UnchangedCount != 1 ||
        unchangedPlan.Items.Single(item => item.Status == TranslationBatchImportStatus.Unchanged).ExportNumber != 500)
    {
        throw new InvalidDataException("CompactNumbered preview did not surface an unchanged #500 row.");
    }

    var suspiciousPlan = TranslationBatchDocument.Analyze(
        $"Batch: {manifest.BatchId}\n1» {new string('X', 200)}",
        manifest,
        overwriteExisting: true);
    if (suspiciousPlan.SuspiciousCount == 0 ||
        !suspiciousPlan.Items.Any(item => item.Flags.HasFlag(TranslationBatchImportFlags.SuspiciousLength)))
    {
        throw new InvalidDataException("CompactNumbered preview did not flag suspiciously long joined text.");
    }
}

static void ValidateCompactNumberedRejectedMarkers()
{
    var entry = new TranslationEntry("compact-reject-1", "Contact 127.5 departure", string.Empty);
    var manifest = TranslationBatchDocument.BuildManifest(TranslationBatchDocument.Build(new[] { entry }), deduplicate: false);

    var decimalPlan = TranslationBatchDocument.Analyze("127.5 CONTACT DEPARTURE", manifest, overwriteExisting: true);
    if (decimalPlan.Problems.Any(problem => problem.Kind == TranslationImportProblemKind.UnknownMarker) ||
        decimalPlan.FoundCount != 1 ||
        decimalPlan.Items[0].ProposedTranslation != "127.5 CONTACT DEPARTURE")
    {
        throw new InvalidDataException("Decimal text was misread as a legacy numbered marker.");
    }

    var conflictPlan = TranslationBatchDocument.Analyze(
        $"Batch: {manifest.BatchId}\n1» Первый\n1» Второй",
        manifest,
        overwriteExisting: true);
    if (!conflictPlan.Problems.Any(problem => problem.Kind == TranslationImportProblemKind.DuplicateMarkerConflict))
        throw new InvalidDataException("CompactNumbered duplicate number conflict was not rejected.");

    var sameDuplicatePlan = TranslationBatchDocument.Analyze(
        $"Batch: {manifest.BatchId}\n1» Один\n1» Один",
        manifest,
        overwriteExisting: true);
    if (sameDuplicatePlan.Problems.Any(problem => problem.Kind == TranslationImportProblemKind.DuplicateMarkerConflict) ||
        sameDuplicatePlan.ChangedCount != 1)
    {
        throw new InvalidDataException("CompactNumbered identical duplicate number was not accepted once.");
    }

    var wrongBatchPlan = TranslationBatchDocument.Analyze("Batch: otherId\n1» Один", manifest, overwriteExisting: true);
    if (!wrongBatchPlan.HasFatalProblems || !wrongBatchPlan.Problems.Any(problem => problem.Kind == TranslationImportProblemKind.WrongBatchId))
        throw new InvalidDataException("CompactNumbered wrong Batch id did not block apply.");

    var missingBatchPlan = TranslationBatchDocument.Analyze("1» Один", manifest, overwriteExisting: true);
    if (missingBatchPlan.HasFatalProblems || !missingBatchPlan.Problems.Any(problem => problem.Kind == TranslationImportProblemKind.MissingBatchId))
        throw new InvalidDataException("CompactNumbered missing Batch id did not produce a non-fatal warning.");

    var untranslatedPlan = TranslationBatchDocument.Analyze(
        $"Batch: {manifest.BatchId}\n1» Contact departure",
        manifest,
        overwriteExisting: true,
        targetLocale: "RU");
    if (!untranslatedPlan.Items.Any(item => item.Flags.HasFlag(TranslationBatchImportFlags.PossiblyUntranslated)))
        throw new InvalidDataException("CompactNumbered Russian import did not flag a likely untranslated English line.");
}

static void ValidateNumberedDisplay()
{
    var display = TranslationBatchDocument.FormatNumberedForDisplay("[[1]] Alpha\n[[2]] Bravo");
    if (!display.Contains("1 │ Alpha", StringComparison.Ordinal) ||
        !display.Contains("2 │ Bravo", StringComparison.Ordinal) ||
        display.Contains("[[", StringComparison.Ordinal))
    {
        throw new InvalidDataException("The upper workspace panels did not render clean visible line numbers.");
    }

    var stripped = TranslationBatchDocument.StripDisplayLineNumbers(display);
    if (stripped != $"Alpha{Environment.NewLine}Bravo")
        throw new InvalidDataException("Visible line numbers leaked back into manually edited translations.");
}

static void ValidateTranslationCorpusMetricsInvariant()
{
    var consistent = new TranslationCorpusMetrics(
        TotalKeys: 4,
        TotalPhysicalLines: 10,
        VisibleKeys: 2,
        VisibleLines: 5,
        NonEmptyVisibleLines: 4,
        FilledVisibleLines: 2,
        ExcludedEmptyLines: 2,
        ExcludedTechnicalLines: 1,
        ExcludedOtherLines: 2,
        TranslatedLines: 2,
        MissingLines: 2,
        ChangedLines: 1,
        UniqueExportLines: 3,
        DeduplicatedAliasLines: 1);
    if (!consistent.HasConsistentPhysicalLineTotals)
        throw new InvalidDataException("A valid corpus metrics partition failed its physical-line invariant.");

    var inconsistent = consistent with { ExcludedOtherLines = 1 };
    if (inconsistent.HasConsistentPhysicalLineTotals)
        throw new InvalidDataException("Corpus metrics accepted overlapping or missing physical-line buckets.");
}

static void ValidateMiz1BlockImport()
{
    var first = new TranslationEntry("DictKey_ActionText_1", "Alpha\nBravo", string.Empty);
    var second = new TranslationEntry("DictKey_subtitle_2", "Test", "Existing");
    var manifest = TranslationBatchDocument.BuildManifest(
        TranslationBatchDocument.Build(new[] { first, second }),
        deduplicate: false,
        generatedPrompt: "Translate only the marked text.");

    if (manifest.Items.Count != 3 || manifest.ExportItems.Count != 3 || manifest.AliasCount != 0)
        throw new InvalidDataException("MZ1 manifest did not preserve every physical line.");

    var firstMarker = TranslationBatchDocument.FormatCompactMarker(manifest, manifest.ExportItems[0]);
    var secondMarker = TranslationBatchDocument.FormatCompactMarker(manifest, manifest.ExportItems[1]);
    var export = TranslationBatchDocument.FormatSource(
        manifest,
        includeInstruction: true,
        TranslationBatchExportFormat.CompactM2);
    if (!export.Contains(firstMarker, StringComparison.Ordinal) ||
        export.Contains("[DictKey_ActionText_1]", StringComparison.Ordinal) ||
        export.Contains("[[MZ1|", StringComparison.Ordinal) ||
        firstMarker.Length >= TranslationBatchDocument.FormatMarker(manifest.ExportItems[0].Id).Length)
    {
        throw new InvalidDataException("Compact export exposed a DictKey, emitted MZ1, or failed to shorten its marker.");
    }

    var response = $"{manifest.GeneratedPrompt}\n---\n```text\n- **{secondMarker}** Вторая\nстрока ответа\n{firstMarker} Первая\n```";
    var plan = TranslationBatchDocument.Analyze(response, manifest, overwriteExisting: true);
    if (first.Translation.Length != 0 || second.Translation != "Existing")
        throw new InvalidDataException("Analyze mutated translations before confirmation.");
    if (plan.FoundCount != 2 || plan.ChangedCount != 2 || plan.MissingCount != 1 || !plan.CanApply ||
        plan.Items.Any(item => item.Id == new TranslationBatchItemId(second.Key, 0) &&
                               item.Status != TranslationBatchImportStatus.Missing))
        throw new InvalidDataException("Partial/reordered M2 analysis did not isolate the supplied blocks.");
    if (plan.Items.Single(item => item.Id == new TranslationBatchItemId(first.Key, 1)).ProposedTranslation != "Вторая строка ответа")
        throw new InvalidDataException("A wrapped translation body was not joined into its M2 block.");

    var applied = TranslationBatchDocument.Apply(plan);
    if (!applied.Success || first.Translation != "Первая\nВторая строка ответа" || second.Translation != "Existing")
        throw new InvalidDataException("Partial/reordered M2 blocks were not applied to their stable targets.");
}

static void ValidateLegacyBatchImport()
{
    var entries = new[]
    {
        new TranslationEntry("legacy-1", "One", string.Empty),
        new TranslationEntry("legacy-2", "Two", string.Empty),
        new TranslationEntry("legacy-3", "Three", string.Empty),
        new TranslationEntry("legacy-4", "Four", string.Empty)
    };
    var manifest = TranslationBatchDocument.BuildManifest(TranslationBatchDocument.Build(entries), deduplicate: false);
    var plan = TranslationBatchDocument.Analyze("[[1]] Один\n🔹2🔹 Два\n3. Три\n4) Четыре", manifest, overwriteExisting: true);
    if (plan.FoundCount != 4 || plan.ChangedCount != 4 || plan.Problems.Any(problem => problem.Severity != TranslationImportProblemSeverity.Warning))
        throw new InvalidDataException("One of the supported legacy marker formats was rejected.");

    var result = TranslationBatchDocument.Apply(plan);
    if (!result.Success || !entries.Select(entry => entry.Translation).SequenceEqual(new[] { "Один", "Два", "Три", "Четыре" }))
        throw new InvalidDataException("Legacy markers did not map through the saved manifest numbering.");
}

static void ValidateRejectedBatchMarkers()
{
    var entry = new TranslationEntry("known-key", "Known", string.Empty);
    var manifest = TranslationBatchDocument.BuildManifest(TranslationBatchDocument.Build(new[] { entry }), deduplicate: false);
    var marker = TranslationBatchDocument.FormatMarker(new TranslationBatchItemId(entry.Key, 0));
    var unknown = TranslationBatchDocument.FormatMarker(new TranslationBatchItemId("unknown-key", 0));
    var badPart = TranslationBatchDocument.FormatMarker(new TranslationBatchItemId(entry.Key, 99));
    var plan = TranslationBatchDocument.Analyze(
        $"[[MZ1|%%%|0]] Broken\n[[M2|bad|1]] Broken compact\n[[M2|AAAAAAAA|1]] Wrong batch\n{unknown} Unknown\n{badPart} Bad part\n{marker} First\n{marker} Conflicting",
        manifest,
        overwriteExisting: true);

    var kinds = plan.Problems.Select(problem => problem.Kind).ToHashSet();
    if (!kinds.Contains(TranslationImportProblemKind.InvalidMarker) ||
        !kinds.Contains(TranslationImportProblemKind.UnknownMarker) ||
        !kinds.Contains(TranslationImportProblemKind.InvalidPartIndex) ||
        !kinds.Contains(TranslationImportProblemKind.DuplicateMarkerConflict))
        throw new InvalidDataException("Malformed, unknown, out-of-range, or conflicting MZ1 markers were not classified.");
    if (!plan.Problems.Any(problem => problem.Message.Contains("another copied batch", StringComparison.Ordinal)))
        throw new InvalidDataException("An M2 marker from another copied batch was not rejected.");
    if (entry.Translation.Length != 0)
        throw new InvalidDataException("Rejected marker analysis mutated the entry.");
}

static void ValidateDeduplicatedBatchImport()
{
    var existing = new TranslationEntry("duplicate-existing", "Same source", "Старый");
    var missing = new TranslationEntry("duplicate-missing", "Same source", string.Empty);
    var manifest = TranslationBatchDocument.BuildManifest(TranslationBatchDocument.Build(new[] { existing, missing }), deduplicate: true);
    if (manifest.ExportItems.Count != 1 || manifest.Items.Count != 2 || manifest.AliasCount != 1)
        throw new InvalidDataException("Ordinal duplicate source lines were not collapsed into one representative.");

    var marker = TranslationBatchDocument.FormatMarker(manifest.ExportItems[0].Id);
    var keepPlan = TranslationBatchDocument.Analyze($"{marker} Новый", manifest, overwriteExisting: false);
    if (existing.Translation != "Старый" || missing.Translation.Length != 0)
        throw new InvalidDataException("Deduplicated Analyze mutated aliases before confirmation.");
    var keepResult = TranslationBatchDocument.Apply(keepPlan);
    if (!keepResult.Success || existing.Translation != "Старый" || missing.Translation != "Новый")
        throw new InvalidDataException("Dedup fan-out did not respect overwriteExisting=false per alias.");

    var overwriteManifest = TranslationBatchDocument.BuildManifest(TranslationBatchDocument.Build(new[] { existing, missing }), deduplicate: true);
    var overwriteMarker = TranslationBatchDocument.FormatMarker(overwriteManifest.ExportItems[0].Id);
    var overwritePlan = TranslationBatchDocument.Analyze($"{overwriteMarker} Замена", overwriteManifest, overwriteExisting: true);
    var overwriteResult = TranslationBatchDocument.Apply(overwritePlan);
    if (!overwriteResult.Success || existing.Translation != "Замена" || missing.Translation != "Замена")
        throw new InvalidDataException("Dedup fan-out did not update every alias with overwriteExisting=true.");
}

static void ValidateStaleBatchPlan()
{
    var entry = new TranslationEntry("stale-key", "Source", string.Empty);
    var manifest = TranslationBatchDocument.BuildManifest(TranslationBatchDocument.Build(new[] { entry }), deduplicate: false);
    var marker = TranslationBatchDocument.FormatMarker(manifest.ExportItems[0].Id);
    var plan = TranslationBatchDocument.Analyze($"{marker} Planned", manifest, overwriteExisting: true);
    entry.Translation = "Changed after preview";

    var result = TranslationBatchDocument.Apply(plan);
    if (result.Success || entry.Translation != "Changed after preview")
        throw new InvalidDataException("A stale import plan was applied after the translation snapshot changed.");
}

static void AssertProtectedTokenFailure(
    string translated,
    IReadOnlyList<string> fragments,
    string scenario)
{
    try
    {
        OllamaTranslationService.RestoreProtectedFragments(translated, fragments);
    }
    catch (ProtectedTokenException)
    {
        return;
    }

    throw new InvalidOperationException($"Protected token validation accepted a {scenario} marker sequence.");
}

static void ValidateLuaTranslationGuard()
{
    var dcsBlock = "if Unit.getByName('FORD 51') then\nlocal pos = Unit.getByName('FORD 51'):getPoint()\ntrigger.action.outText('Ready', 10)\nend";
    var oneLineApiCall = "trigger.action.radioTransmission('AUDIO/test.ogg', origin, radio.modulation.AM)";
    var oneLineStaticObjectCall = "StaticObject.getByName('HMV4'):destroy()";
    var resourceStatusLine = "mist_4_5_107.lua loaded.";
    var cockpitChecklistLine = "- R Throttle Lever: IDLE";
    var genericLuaBlock = "local message = 'Ready'\nif enabled then\nreturn message\nend";
    var oneLineIf = "if count > 5 then";
    var counterUpdate = "count = count + 1";
    var runCommand = "runCommand(\"gear_down\")";
    var naturalText = "At the end of the runway, contact Tower and report ready.";
    var dcsTermsInText = "Unit 1 will trigger the next message after landing.";

    if (!OllamaTranslationService.LooksLikeLuaScript(dcsBlock) ||
        !OllamaTranslationService.LooksLikeLuaScript(oneLineApiCall) ||
        !OllamaTranslationService.LooksLikeLuaScript(oneLineStaticObjectCall) ||
        !OllamaTranslationService.LooksLikeLuaScript(resourceStatusLine) ||
        !OllamaTranslationService.LooksLikeLuaScript(genericLuaBlock) ||
        !OllamaTranslationService.LooksLikeLuaScript(oneLineIf) ||
        !OllamaTranslationService.LooksLikeLuaScript(counterUpdate) ||
        !OllamaTranslationService.LooksLikeLuaScript(runCommand) ||
        OllamaTranslationService.LooksLikeLuaScript(cockpitChecklistLine) ||
        OllamaTranslationService.LooksLikeLuaScript(naturalText) ||
        OllamaTranslationService.LooksLikeLuaScript(dcsTermsInText))
    {
        throw new InvalidOperationException("Lua translation guard classified code or natural text incorrectly.");
    }

    Console.WriteLine("PASS Lua translation guard: DCS API and Lua blocks are not sent to the model");
}

static async Task ValidateTranslationQueueAsync()
{
    var provider = new QueueTestProvider();
    var runner = new TranslationQueueRunner(provider);
    var entries = new[]
    {
        new TranslationEntry("ok", "Hello pilot", ""),
        new TranslationEntry("existing", "Already done", "Готово"),
        new TranslationEntry("token", "TOKEN_ERROR", ""),
        new TranslationEntry("timeout", "TIMEOUT", ""),
        new TranslationEntry("after-error", "Continue", "")
    };

    await runner.RunAsync(entries, overwriteExisting: false);
    if (runner.State.Status != TranslationQueueStatus.Completed ||
        runner.State.Succeeded != 2 || runner.State.Skipped != 1 || runner.State.Errors.Count != 2)
    {
        throw new InvalidOperationException("Translation queue did not continue through skips and per-row errors.");
    }
    if (entries[0].Translation != "RU: Hello pilot" || entries[4].Translation != "RU: Continue")
        throw new InvalidOperationException("Translation queue did not apply successful translations.");
    if (!entries[0].Undo() || entries[0].Translation.Length != 0 || entries[0].WasAiTranslated)
        throw new InvalidOperationException("Translation undo did not restore the pre-AI value.");
    if (runner.State.Errors.All(error => error.Kind != TranslationErrorKind.ProtectedTokenMismatch) ||
        runner.State.Errors.All(error => error.Kind != TranslationErrorKind.Transient))
    {
        throw new InvalidOperationException("Translation queue did not classify retryable errors correctly.");
    }

    var cancellingRunner = new TranslationQueueRunner(provider);
    var slowEntries = new[]
    {
        new TranslationEntry("slow", "SLOW", ""),
        new TranslationEntry("never", "Never reached", "")
    };
    var runTask = cancellingRunner.RunAsync(slowEntries, overwriteExisting: true);
    await Task.Delay(30);
    cancellingRunner.Cancel();
    await runTask;
    if (cancellingRunner.State.Status != TranslationQueueStatus.Cancelled ||
        cancellingRunner.State.Processed != 0 || !string.IsNullOrEmpty(slowEntries[1].Translation))
    {
        throw new InvalidOperationException("Translation queue cancellation did not stop pending work cleanly.");
    }

    Console.WriteLine("PASS translation queue: success, skip, classified errors, continue, cancellation");
}

static async Task ValidateTranslationCheckpointAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "mizedit-checkpoint-test-" + Guid.NewGuid().ToString("N"));
    var service = new TranslationCheckpointService(root);
    const string mission = @"C:\fixtures\mission.miz";
    const string locale = "RU";
    var source = new[]
    {
        new TranslationEntry("one", "One", ""),
        new TranslationEntry("two", "Two", "")
    };
    source[0].ApplyAiTranslation("Один");
    await service.SaveAsync(mission, locale, source);

    var restoredEntries = new[]
    {
        new TranslationEntry("one", "One", ""),
        new TranslationEntry("two", "Two", "Ручной")
    };
    var restored = service.RestoreMissing(mission, locale, restoredEntries);
    if (restored != 1 || restoredEntries[0].Translation != "Один" || restoredEntries[1].Translation != "Ручной")
        throw new InvalidOperationException("Translation checkpoint did not restore only missing compatible rows.");

    var changedSource = new[] { new TranslationEntry("one", "Changed source", "") };
    if (service.RestoreMissing(mission, locale, changedSource) != 0)
        throw new InvalidOperationException("Translation checkpoint ignored a source hash mismatch.");

    service.Delete(mission, locale);
    try { Directory.Delete(root, recursive: true); } catch { }
    Console.WriteLine("PASS translation checkpoint: atomic save, restore missing, source hash guard, delete");
}

static void RunMutationScenario(MissionService service, string sourceMission, string testRoot)
{
    const string locale = "INTEGRATION_TEST";
    var fixtureRoot = Path.Combine(testRoot, "fixtures");
    Directory.CreateDirectory(fixtureRoot);
    var audio1 = Path.Combine(fixtureRoot, "integration.ogg");
    var audio2 = Path.Combine(fixtureRoot, "integration-replaced.ogg");
    var picture = Path.Combine(fixtureRoot, "integration.png");
    var script = Path.Combine(fixtureRoot, "integration.lua");
    var removable = Path.Combine(fixtureRoot, "remove-me.txt");
    File.WriteAllText(audio1, "OGG integration fixture 1");
    File.WriteAllText(audio2, "OGG integration fixture 2");
    File.WriteAllBytes(picture, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
    File.WriteAllText(script, "return 'integration'");
    File.WriteAllText(removable, "remove me");

    var output = Path.Combine(testRoot, "mutation-roundtrip.miz");
    string audioKey;
    string pictureKey;
    string scriptKey;
    using (var session = service.LoadMission(sourceMission))
    {
        session.Localization.AddLocale(locale);
        session.Localization.AddLocale("DELETE_ME");
        session.Localization.DeleteLocale("DELETE_ME");
        if (session.Localization.GetLocales().Contains("DELETE_ME", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Locale delete failed.");

        session.Localization.UpdateDictionaryEntries(
            locale,
            new[] { new KeyValuePair<string, string>("DictKey_IntegrationTest", "РўРµСЃС‚РѕРІС‹Р№ РїРµСЂРµРІРѕРґ") });

        var audio = session.Localization.AddResourceFile(locale, audio1, LocalizationEngine.ResourceKind.Audio);
        var image = session.Localization.AddResourceFile(locale, picture, LocalizationEngine.ResourceKind.Picture);
        var lua = session.Localization.AddResourceFile(locale, script, LocalizationEngine.ResourceKind.Script);
        var disposable = session.Localization.AddResourceFile(locale, removable, LocalizationEngine.ResourceKind.Generic);
        audioKey = audio.Key;
        pictureKey = image.Key;
        scriptKey = lua.Key;

        session.Localization.ReplaceResourceFile(locale, audioKey, audio2);
        if (!session.Localization.RemoveResource(locale, disposable.Key, deletePhysicalFile: true))
            throw new InvalidOperationException("Resource removal failed.");

        session.Mission.AddBriefingPicture(pictureKey);
        session.Mission.AddTriggerPicture(pictureKey);
        session.Mission.AddSimpleTrigger(
            "MizEdit integration trigger",
            "return true",
            "a_do_script([[return]])",
            missionStart: false);
        service.SaveAsMiz(session, output);
    }

    using var reopened = service.LoadMission(output);
    if (!reopened.Localization.GetLocales().Contains(locale, StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException("Added locale did not survive save/reopen.");
    if (!reopened.Localization.GetDictionaryEntries(locale).TryGetValue("DictKey_IntegrationTest", out var translated) || translated != "РўРµСЃС‚РѕРІС‹Р№ РїРµСЂРµРІРѕРґ")
        throw new InvalidOperationException("Dictionary update did not survive save/reopen.");

    var map = reopened.Localization.LoadMapResource(locale);
    foreach (var key in new[] { audioKey, pictureKey, scriptKey })
    {
        if (!map.ContainsKey(key) || reopened.Localization.ResolveResourceFile(locale, key) is not { } resolved || !File.Exists(resolved))
            throw new InvalidOperationException($"Added resource did not survive save/reopen: {key}");
    }
    ValidateLocaleContainsOnlyExplicitResources(
        reopened.Archive.WorkDir,
        locale,
        new[] { "dictionary", "mapResource" }
            .Concat(map.Values)
            .ToHashSet(StringComparer.OrdinalIgnoreCase));
    var audioPath = reopened.Localization.ResolveResourceFile(locale, audioKey)!;
    if (File.ReadAllText(audioPath) != "OGG integration fixture 2")
        throw new InvalidOperationException("Resource replacement did not survive save/reopen.");
    if (!reopened.Mission.GetPictureFileNames().Contains(pictureKey, StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException("Briefing picture reference did not survive save/reopen.");
    if (!reopened.Mission.GetTriggerPictures().Contains(pictureKey, StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException("Trigger picture reference did not survive save/reopen.");
    if (!reopened.Mission.GetTriggers().Any(trigger => trigger.Contains("a_do_script", StringComparison.Ordinal)))
        throw new InvalidOperationException("Added trigger did not survive save/reopen.");

    RunBatchScenario(service, output, testRoot, locale);
}

static void ValidateLocaleContainsOnlyExplicitResources(string workDir, string locale, ISet<string> allowedFileNames)
{
    var localeDirectory = Path.Combine(workDir, "l10n", locale);
    var unexpected = Directory.EnumerateFiles(localeDirectory, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(localeDirectory, path))
        .Where(relative => !allowedFileNames.Contains(relative))
        .OrderBy(relative => relative, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (unexpected.Length > 0)
        throw new InvalidOperationException(
            $"Locale {locale} contains non-explicit resources copied from DEFAULT: {string.Join(", ", unexpected)}");
}

static void RunBatchScenario(MissionService service, string sourceMission, string testRoot, string locale)
{
    var batchRoot = Path.Combine(testRoot, "batch");
    Directory.CreateDirectory(batchRoot);
    var batchMission = Path.Combine(batchRoot, "batch.miz");
    File.Copy(sourceMission, batchMission);

    var batch = new BatchService(service);
    var exportResults = batch.BatchSaveAsTxt(batchRoot, locale).ToList();
    if (exportResults.Count != 1 || exportResults.Any(result => result.StartsWith("Error", StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException("Batch TXT export failed: " + string.Join(" | ", exportResults));

    var txtPath = Path.ChangeExtension(batchMission, ".txt");
    var lines = File.ReadAllLines(txtPath).ToList();
    if (!lines.Contains("format=mizedit-v2", StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException("Batch TXT export did not use the multiline-safe format.");
    var nameIndex = lines.FindIndex(line => line.StartsWith("name=", StringComparison.Ordinal));
    if (nameIndex < 0)
        throw new InvalidOperationException("Batch TXT export has no mission name.");
    lines[nameIndex] = "name=Batch integration name";
    File.WriteAllLines(txtPath, lines);

    var importResults = batch.BatchImportTxt(batchRoot, locale).ToList();
    if (importResults.Count != 1 || importResults.Any(result => result.StartsWith("Error", StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException("Batch TXT import failed: " + string.Join(" | ", importResults));
    var analysisResults = batch.BatchStateAnalyze(batchRoot).ToList();
    if (analysisResults.Count != 1 || analysisResults.Any(result => result.StartsWith("Error", StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException("Batch state analysis failed: " + string.Join(" | ", analysisResults));

    using var reopened = service.LoadMission(batchMission);
    var name = reopened.Localization.ResolveBriefingString(reopened.Mission, "name", locale);
    if (name != "Batch integration name")
        throw new InvalidOperationException("Batch TXT import did not update the localized mission name.");
}

static Dictionary<string, string> SnapshotArchive(string path)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    using var archive = ZipFile.OpenRead(path);
    foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
    {
        var normalizedName = entry.FullName.Replace('\\', '/');
        using var stream = entry.Open();
        result.Add(normalizedName, Convert.ToHexString(SHA256.HashData(stream)));
    }
    return result;
}

static (int Audio, int Images, int Scripts) CountPhysicalResources(IEnumerable<string> names)
{
    var audio = 0;
    var images = 0;
    var scripts = 0;
    foreach (var name in names)
    {
        switch (Path.GetExtension(name).ToLowerInvariant())
        {
            case ".ogg":
            case ".wav":
            case ".mp3": audio++; break;
            case ".png":
            case ".jpg":
            case ".jpeg":
            case ".bmp": images++; break;
            case ".lua": scripts++; break;
        }
    }
    return (audio, images, scripts);
}

static int ValidateAudioFiles(string workDir)
{
    var validated = 0;
    foreach (var file in Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories))
    {
        WaveStream? reader = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".ogg" => new VorbisWaveReader(file),
            ".wav" => new WaveFileReader(file),
            ".mp3" => new Mp3FileReader(file),
            _ => null
        };
        if (reader is null)
            continue;

        using (reader)
        {
            if (reader.WaveFormat.SampleRate <= 0 || reader.WaveFormat.Channels <= 0)
                throw new InvalidDataException($"Invalid audio format: {file}");
            var blockAlign = Math.Max(1, reader.WaveFormat.BlockAlign);
            var requested = Math.Min(8192, Math.Max(blockAlign, reader.WaveFormat.AverageBytesPerSecond / 10));
            var buffer = new byte[Math.Max(blockAlign, requested / blockAlign * blockAlign)];
            _ = reader.Read(buffer, 0, buffer.Length);
        }
        validated++;
    }
    return validated;
}

static int ValidateImageFiles(string workDir)
{
    var validated = 0;
    foreach (var file in Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories))
    {
        var extension = Path.GetExtension(file).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
            continue;

        using var stream = File.OpenRead(file);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0 || decoder.Frames[0].PixelWidth <= 0 || decoder.Frames[0].PixelHeight <= 0)
            throw new InvalidDataException($"Invalid image: {file}");
        validated++;
    }
    return validated;
}

static void AssertPreservedEntries(
    IReadOnlyDictionary<string, string> original,
    IReadOnlyDictionary<string, string> saved)
{
    var originalNames = original.Keys.ToHashSet(StringComparer.Ordinal);
    var savedNames = saved.Keys.ToHashSet(StringComparer.Ordinal);
    if (!originalNames.SetEquals(savedNames))
    {
        var missing = originalNames.Except(savedNames).Take(3);
        var added = savedNames.Except(originalNames).Take(3);
        throw new InvalidDataException($"Archive inventory changed. Missing=[{string.Join(", ", missing)}], added=[{string.Join(", ", added)}]");
    }

    foreach (var name in originalNames.Where(name => !name.Equals("mission", StringComparison.OrdinalIgnoreCase)))
    {
        if (original[name] != saved[name])
            throw new InvalidDataException($"Unedited archive entry changed: {name}");
    }
}

static void ValidateSessionState()
{
    var state = new SessionState();
    if (state.IsDirty || state.SaveState != SaveState.Idle)
        throw new InvalidDataException("SessionState must start clean and idle.");

    state.MarkDirty();
    if (!state.IsDirty || state.SaveState != SaveState.Idle)
        throw new InvalidDataException("MarkDirty did not mark the session dirty.");

    state.MarkSaving();
    state.MarkDirty();
    if (!state.IsDirty || state.SaveState != SaveState.Saving)
        throw new InvalidDataException("Dirty notifications must not interrupt an active save.");

    state.MarkError();
    if (!state.IsDirty || state.SaveState != SaveState.Error)
        throw new InvalidDataException("A save error must keep the session dirty.");

    state.MarkSaved();
    if (state.IsDirty || state.SaveState != SaveState.Saved)
        throw new InvalidDataException("MarkSaved did not clear dirty state.");

    state.Reset();
    if (state.IsDirty || state.SaveState != SaveState.Idle)
        throw new InvalidDataException("Reset did not restore a clean idle state.");
}

static MissionMetrics ReadMetrics(MissionSession session)
{
    var locales = session.Localization.GetLocales();
    return new MissionMetrics(
        locales.Count,
        locales.Sum(locale => session.Localization.GetDictionaryEntries(locale).Count),
        locales.Sum(locale => session.Localization.LoadMapResource(locale).Count),
        session.Mission.MissionTable.Pairs.Count(),
        session.Mission.GetPictureFileNames().Count,
        session.Mission.GetAudioFileNames().Count,
        session.Mission.GetTriggers().Count,
        session.Mission.GetRadioTransmissions().Count,
        session.Mission.GetTriggerPictures().Count);
}

internal readonly record struct MissionMetrics(
    int Locales,
    int DictionaryEntries,
    int MappedResources,
    int TopLevelFields,
    int BriefingPictures,
    int MissionAudioReferences,
    int Triggers,
    int RadioMessages,
    int TriggerPictures);

internal sealed class QueueTestProvider : ITranslationProvider
{
    public async Task<string> TranslateAsync(string sourceText, CancellationToken cancellationToken = default)
    {
        if (sourceText == "TOKEN_ERROR")
            throw new ProtectedTokenException("Protected token changed.");
        if (sourceText == "TIMEOUT")
            throw new TimeoutException("Test timeout.");
        if (sourceText == "SLOW")
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        return "RU: " + sourceText;
    }
}
