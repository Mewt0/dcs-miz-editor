using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MizEdit.Core;

public sealed record TranslationBatchLine(
    int Number,
    TranslationEntry Entry,
    int EntryLineIndex,
    string SourceLine,
    string TranslationLine,
    bool IsFirstEntryLine);

public sealed record TranslationBatchImportResult(
    int MatchedLines,
    int ChangedEntries,
    int IgnoredLines,
    string? Error = null)
{
    public bool Success => Error == null;
}

public static partial class TranslationBatchDocument
{
    private const int MaxPasteLength = 500_000;
    private const double SuspiciousLengthRatio = 3.0;
    private const char CompactNumberedMarkerGlyph = '»';

    private const string TranslationInstruction =
        "Переведи с английского на естественный русский текст миссии DCS, используя принятую авиационную терминологию.\n" +
        "Верни каждый маркер без изменений и переводи только текст после него. Не добавляй и не удаляй маркеры.\n" +
        "Короткие cockpit/checklist строки тоже переводи: '- R Throttle Lever: IDLE' -> '- Рычаг газа правого двигателя: IDLE'. L/R как сторона самолёта переводи как левый/правый.\n" +
        "Переводи человекочитаемые подписи, команды меню, radio subtitles и текст до/после ':'; не оставляй строку на английском только потому, что в ней есть ON/OFF/IDLE или двоеточие.\n" +
        "Не переводи технические строки статуса ресурсов/скриптов вроде 'mist_4_5_107.lua loaded.'; верни их без изменений.\n" +
        "Если строка похожа на Lua-логику или код — if/elseif/else/then/end/return/local/function/for/while, сравнения > < == ~= <= >=, and/or/not, присваивания =, счётчики count = count + 1, вызовы runCommand(...), trigger.action..., Unit.getByName(...) — верни всю строку без изменений.\n" +
        "Не экранируй Markdown-символы: пиши ***ENG, HDG_end и Batch ID с подчёркиваниями без обратных слэшей.\n" +
        "Не переводи и не изменяй Lua-код, переменные, пути, имена ресурсов, плейсхолдеры, форматирование, числа, единицы, частоты, координаты, позывные и названия точек.\n" +
        "Сохраняй switch states/codes и авиационные сокращения F10/AWACS/CAP/UHF/VHF/TACAN/ILS/QNH/ON/OFF/IDLE/ARM/SAFE/HÖJD/SPAK/ATT/EBK/FIR, если это позиция переключателя, режим или код.\n" +
        "Не меняй порядок кодовых авиационных зон: ANKARA FIR остаётся ANKARA FIR, не FIR ANKARA.\n" +
        "Не выдумывай единицы и факты; сомнительный термин оставь как в оригинале. Без Markdown, заголовков, пояснений и лишнего текста.";

    private const string CompactNumberedInstruction =
        "Translate the DCS mission text into natural Russian.\n" +
        "Keep the `Batch:` line unchanged.\n" +
        "Keep every leading `N»` marker unchanged.\n" +
        "Translate only the text after `N»`.\n" +
        "Translate short cockpit/checklist labels too: '- R Throttle Lever: IDLE' -> '- Рычаг газа правого двигателя: IDLE'. Treat L/R as left/right aircraft side when appropriate.\n" +
        "Translate human-readable labels, menu commands, radio subtitles, and text around ':'; do not leave a line in English only because it contains ON/OFF/IDLE or a colon.\n" +
        "Do not translate technical script/resource status lines such as 'mist_4_5_107.lua loaded.'; return them unchanged.\n" +
        "If a line looks like Lua logic or code — if/elseif/else/then/end/return/local/function/for/while, comparisons > < == ~= <= >=, and/or/not, assignments =, counters like count = count + 1, or calls like runCommand(...), trigger.action..., Unit.getByName(...) — return the entire line unchanged.\n" +
        "Do not Markdown-escape punctuation: output ***ENG, HDG_end and Batch IDs with underscores, without backslashes.\n" +
        "Do not translate or change Lua code, variables, file names, placeholders, frequencies, coordinates, callsigns, or waypoint names.\n" +
        "Preserve switch states/codes and aviation abbreviations such as F10/AWACS/CAP/UHF/VHF/TACAN/ILS/QNH/ON/OFF/IDLE/ARM/SAFE/HÖJD/SPAK/ATT/EBK/FIR when they are positions, modes, or system codes.\n" +
        "Do not reorder coded airspace names: ANKARA FIR stays ANKARA FIR, not FIR ANKARA.\n" +
        "Return only the `Batch:` line and numbered translated lines. No Markdown, no explanations.";

    public static IReadOnlyList<TranslationBatchLine> Build(IEnumerable<TranslationEntry> entries)
    {
        var result = new List<TranslationBatchLine>();
        var number = 1;

        foreach (var entry in entries)
        {
            var sourceLines = SplitLines(entry.SourceText);
            var translationLines = SplitLines(entry.Translation);
            var count = Math.Max(sourceLines.Length, translationLines.Length);
            if (count == 0)
                count = 1;

            for (var index = 0; index < count; index++)
            {
                result.Add(new TranslationBatchLine(
                    number++,
                    entry,
                    index,
                    index < sourceLines.Length ? sourceLines[index] : string.Empty,
                    index < translationLines.Length ? translationLines[index] : string.Empty,
                    index == 0));
            }
        }

        return result;
    }

    public static string FormatSource(
        IReadOnlyList<TranslationBatchLine> lines,
        bool includeKeys,
        bool includeInstruction)
    {
        var output = new StringBuilder();
        if (includeInstruction)
        {
            output.AppendLine("Переведи с английского на естественный русский текст миссии DCS, используя принятую авиационную терминологию.");
            output.AppendLine("Верни те же строки: строго сохрани каждый маркер [[номер]], порядок, число строк и [DictKey_...]; переводи только текст после них.");
            output.AppendLine("Короткие cockpit/checklist строки тоже переводи: '- R Throttle Lever: IDLE' -> '- Рычаг газа правого двигателя: IDLE'. L/R как сторона самолёта переводи как левый/правый.");
            output.AppendLine("Переводи человекочитаемые подписи, команды меню, radio subtitles и текст до/после ':'; не оставляй строку на английском только потому, что в ней есть ON/OFF/IDLE или двоеточие.");
            output.AppendLine("Не переводи технические строки статуса ресурсов/скриптов вроде 'mist_4_5_107.lua loaded.'; верни их без изменений.");
            output.AppendLine("Если строка похожа на Lua-логику или код — if/elseif/else/then/end/return/local/function/for/while, сравнения > < == ~= <= >=, and/or/not, присваивания =, счётчики count = count + 1, вызовы runCommand(...), trigger.action..., Unit.getByName(...) — верни всю строку без изменений.");
            output.AppendLine("Не экранируй Markdown-символы: пиши ***ENG, HDG_end и Batch ID с подчёркиваниями без обратных слэшей.");
            output.AppendLine("Не переводи и не изменяй Lua-код, переменные, пути, имена ресурсов, плейсхолдеры, форматирование, числа, единицы, частоты, координаты, позывные и названия точек.");
            output.AppendLine("Сохраняй switch states/codes и авиационные сокращения F10/AWACS/CAP/UHF/VHF/TACAN/ILS/QNH/ON/OFF/IDLE/ARM/SAFE/HÖJD/SPAK/ATT/EBK/FIR, если это позиция переключателя, режим или код.");
            output.AppendLine("Не меняй порядок кодовых авиационных зон: ANKARA FIR остаётся ANKARA FIR, не FIR ANKARA.");
            output.AppendLine("Не выдумывай единицы и факты; сомнительный термин оставь как в оригинале. Без Markdown, заголовков, пояснений и лишнего текста.");
            output.AppendLine("---");
        }

        foreach (var line in lines)
        {
            output.Append("[[").Append(line.Number).Append("]] ");

            // Exchange data keeps the key on every physical line so a reordered
            // multiline response can still be validated. FormatForDisplay removes
            // this metadata from the UI.
            if (includeKeys)
                output.Append('[').Append(line.Entry.Key).Append("] ");

            output.AppendLine(line.SourceLine);
        }

        return output.ToString().TrimEnd();
    }

    public static string FormatTranslation(IReadOnlyList<TranslationBatchLine> lines)
    {
        var output = new StringBuilder();
        foreach (var line in lines)
            output.Append("[[").Append(line.Number).Append("]] ").AppendLine(line.TranslationLine);
        return output.ToString().TrimEnd();
    }

    public static TranslationBatchManifest BuildManifest(
        IReadOnlyList<TranslationBatchLine> lines,
        bool deduplicate = true,
        string? generatedPrompt = null)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var representativeBySource = new Dictionary<string, TranslationBatchItemId>(StringComparer.Ordinal);
        var representativeById = new Dictionary<TranslationBatchItemId, TranslationBatchItemId>();
        foreach (var line in lines)
        {
            var id = new TranslationBatchItemId(line.Entry.Key, line.EntryLineIndex);
            var representative = id;
            if (deduplicate && line.SourceLine.Length > 0)
            {
                if (!representativeBySource.TryGetValue(line.SourceLine, out representative))
                    representativeBySource.Add(line.SourceLine, representative = id);
            }
            representativeById[id] = representative;
        }

        var aliasCounts = representativeById.Values
            .GroupBy(id => id)
            .ToDictionary(group => group.Key, group => group.Count() - 1);
        var exportNumberByRepresentative = new Dictionary<TranslationBatchItemId, int>();
        foreach (var line in lines)
        {
            var id = new TranslationBatchItemId(line.Entry.Key, line.EntryLineIndex);
            var representative = representativeById[id];
            if (representative == id && !exportNumberByRepresentative.ContainsKey(representative))
                exportNumberByRepresentative.Add(representative, exportNumberByRepresentative.Count + 1);
        }

        var items = lines.Select(line =>
        {
            var id = new TranslationBatchItemId(line.Entry.Key, line.EntryLineIndex);
            var representative = representativeById[id];
            return new TranslationBatchManifestItem(
                id,
                line.Number,
                line.Entry,
                line.SourceLine,
                line.TranslationLine,
                representative,
                aliasCounts[representative],
                representative == id ? exportNumberByRepresentative[representative] : null);
        }).ToArray();

        return new TranslationBatchManifest(
            items,
            deduplicate,
            ComputeContentFingerprint(items),
            generatedPrompt ?? TranslationInstruction,
            ComputeShapeFingerprint(items));
    }

    public static string FormatMarker(TranslationBatchItemId id)
    {
        if (string.IsNullOrEmpty(id.DictKey))
            throw new ArgumentException("A batch item must have a dictionary key.", nameof(id));
        if (id.PartIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(id), "A batch item part index cannot be negative.");

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(id.DictKey))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"[[MZ1|{encoded}|{id.PartIndex}]]";
    }

    public static string FormatCompactMarker(TranslationBatchManifest manifest, TranslationBatchManifestItem item)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(item);
        if (!manifest.Items.Contains(item))
            throw new ArgumentException("The batch item does not belong to this manifest.", nameof(item));
        return $"[[M2|{manifest.BatchId}|{item.LegacyNumber}]]";
    }

    internal static string CreateCompactBatchId(string fingerprint)
    {
        var bytes = Convert.FromHexString(fingerprint);
        return Convert.ToBase64String(bytes, 0, 6)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryParseMarker(string marker, out TranslationBatchItemId id)
    {
        id = default;
        if (marker is null)
            return false;
        var match = Mz1OnlyRegex().Match(marker);
        if (!match.Success || !int.TryParse(match.Groups["part"].Value, out var partIndex) || partIndex < 0)
            return false;

        var encoded = match.Groups["key"].Value;
        try
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            var key = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(base64));
            if (key.Length == 0 || FormatMarker(new TranslationBatchItemId(key, partIndex)) != match.Value)
                return false;
            id = new TranslationBatchItemId(key, partIndex);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return false;
        }
    }

    public static string FormatSource(TranslationBatchManifest manifest, bool includeInstruction)
        => FormatSource(manifest, includeInstruction, TranslationBatchExportFormat.CompactNumbered);

    public static string FormatSource(
        TranslationBatchManifest manifest,
        bool includeInstruction,
        TranslationBatchExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return format switch
        {
            TranslationBatchExportFormat.CompactNumbered => FormatCompactNumberedSource(manifest, includeInstruction),
            TranslationBatchExportFormat.FullMz1 => FormatMz1Source(manifest, includeInstruction),
            TranslationBatchExportFormat.LegacyWithDictKeys => FormatLegacyWithDictKeys(manifest, includeInstruction),
            _ => FormatCompactM2Source(manifest, includeInstruction)
        };
    }

    private static string FormatCompactM2Source(TranslationBatchManifest manifest, bool includeInstruction)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var output = new StringBuilder();
        if (includeInstruction && !string.IsNullOrEmpty(manifest.GeneratedPrompt))
            output.AppendLine(manifest.GeneratedPrompt).AppendLine("---");
        foreach (var item in manifest.ExportItems)
            output.Append(FormatCompactMarker(manifest, item)).Append(' ').AppendLine(item.SourceLine);
        return output.ToString().TrimEnd();
    }

    private static string FormatCompactNumberedSource(TranslationBatchManifest manifest, bool includeInstruction)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var output = new StringBuilder();
        if (includeInstruction)
            output.AppendLine(CompactNumberedInstruction).AppendLine("---");
        output.Append("Batch: ").AppendLine(manifest.BatchId);
        foreach (var item in manifest.ExportItems)
        {
            var number = item.ExportNumber ?? item.LegacyNumber;
            output.Append(number.ToString(CultureInfo.InvariantCulture))
                .Append(CompactNumberedMarkerGlyph)
                .Append(' ')
                .AppendLine(item.SourceLine);
        }
        return output.ToString().TrimEnd();
    }

    private static string FormatMz1Source(TranslationBatchManifest manifest, bool includeInstruction)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var output = new StringBuilder();
        if (includeInstruction && !string.IsNullOrEmpty(manifest.GeneratedPrompt))
            output.AppendLine(manifest.GeneratedPrompt).AppendLine("---");
        foreach (var item in manifest.ExportItems)
            output.Append(FormatMarker(item.Id)).Append(' ').AppendLine(item.SourceLine);
        return output.ToString().TrimEnd();
    }

    private static string FormatLegacyWithDictKeys(TranslationBatchManifest manifest, bool includeInstruction)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var lines = manifest.Items
            .Select(item => new TranslationBatchLine(
                item.LegacyNumber,
                item.Entry,
                item.Id.PartIndex,
                item.SourceLine,
                item.TranslationLine,
                item.Id.PartIndex == 0))
            .ToArray();
        return FormatSource(lines, includeKeys: true, includeInstruction);
    }

    public static string FormatTranslation(TranslationBatchManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var output = new StringBuilder();
        foreach (var item in manifest.ExportItems)
            output.Append(FormatCompactMarker(manifest, item)).Append(' ').AppendLine(item.TranslationLine);
        return output.ToString().TrimEnd();
    }

    /// <summary>
    /// Converts the marked batch exchange format to the clean text shown in the UI.
    /// Number markers and DictKey prefixes remain part of copy/paste data, but are
    /// implementation details and should not distract a person reviewing the text.
    /// </summary>
    public static string FormatForDisplay(string text)
    {
        var displayLines = SplitLines(text);
        for (var index = 0; index < displayLines.Length; index++)
        {
            // UI-generated batches always use [[N]]. Do not treat a legitimate
            // source line such as "1. Taxi to runway" as service metadata.
            var numbered = DisplayNumberedLineRegex().Match(displayLines[index]);
            if (!numbered.Success)
                continue;

            var visibleText = numbered.Groups["text"].Value;
            var keyPrefix = KeyPrefixRegex().Match(visibleText);
            if (keyPrefix.Success)
                visibleText = visibleText[keyPrefix.Length..];

            displayLines[index] = visibleText;
        }

        return string.Join(Environment.NewLine, displayLines);
    }

    /// <summary>
    /// Formats exchange data for the two upper editors with a compact, visible
    /// line-number gutter while keeping service markers and DictKeys hidden.
    /// </summary>
    public static string FormatNumberedForDisplay(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sourceLines = SplitLines(text);
        var displayLines = new string[sourceLines.Length];
        var width = Math.Max(1, sourceLines.Length.ToString(CultureInfo.InvariantCulture).Length);
        for (var index = 0; index < sourceLines.Length; index++)
        {
            var visibleText = sourceLines[index];
            var visibleNumber = (index + 1).ToString(CultureInfo.InvariantCulture);
            var numbered = DisplayNumberedLineRegex().Match(sourceLines[index]);
            if (numbered.Success)
            {
                visibleNumber = numbered.Groups["number"].Value;
                visibleText = numbered.Groups["text"].Value;
                var keyPrefix = KeyPrefixRegex().Match(visibleText);
                if (keyPrefix.Success)
                    visibleText = visibleText[keyPrefix.Length..];
            }

            displayLines[index] = $"{visibleNumber.PadLeft(width)} │ {visibleText}";
        }
        return string.Join(Environment.NewLine, displayLines);
    }

    public static string StripDisplayLineNumbers(string text)
    {
        var lines = SplitLines(text);
        for (var index = 0; index < lines.Length; index++)
        {
            var match = DisplayLineNumberPrefixRegex().Match(lines[index]);
            if (match.Success)
                lines[index] = match.Groups["text"].Value;
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static TranslationBatchImportPlan Analyze(
        string text,
        TranslationBatchManifest manifest,
        bool overwriteExisting,
        string? targetLocale = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var problems = new List<TranslationImportProblem>();
        var planItems = new List<TranslationBatchImportItem>();
        var snapshotFingerprint = ComputeContentFingerprint(manifest.Items);
        var snapshotShapeFingerprint = ComputeShapeFingerprint(manifest.Items);

        if (manifest.Items.Count == 0)
        {
            problems.Add(new TranslationImportProblem(
                TranslationImportProblemKind.EmptyManifest,
                TranslationImportProblemSeverity.Fatal,
                "The current translation batch is empty."));
            return new TranslationBatchImportPlan(manifest, planItems, problems, overwriteExisting, snapshotFingerprint, snapshotShapeFingerprint, 0);
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            problems.Add(new TranslationImportProblem(
                TranslationImportProblemKind.EmptyInput,
                TranslationImportProblemSeverity.Fatal,
                "Clipboard does not contain translation text."));
            return new TranslationBatchImportPlan(manifest, planItems, problems, overwriteExisting, snapshotFingerprint, snapshotShapeFingerprint, 0);
        }
        if (text.Length > MaxPasteLength)
        {
            problems.Add(new TranslationImportProblem(
                TranslationImportProblemKind.PasteTooLarge,
                TranslationImportProblemSeverity.Fatal,
                "The pasted text is too large. It looks like the wrong block was copied."));
            return new TranslationBatchImportPlan(manifest, planItems, problems, overwriteExisting, snapshotFingerprint, snapshotShapeFingerprint, 0);
        }

        var cleaned = CleanupResponse(text, manifest.GeneratedPrompt);
        var compactHeader = FindCompactBatchHeader(cleaned);
        var compactNumberedSeen = CompactNumberedLineRegex().IsMatch(cleaned);
        if (compactNumberedSeen || compactHeader is not null)
        {
            if (compactHeader is null)
            {
                problems.Add(new TranslationImportProblem(
                    TranslationImportProblemKind.MissingBatchId,
                    TranslationImportProblemSeverity.Warning,
                    "Compact numbered response does not contain a Batch header. Review the preview carefully."));
            }
            else if (!compactHeader.Equals(manifest.BatchId, StringComparison.Ordinal))
            {
                problems.Add(new TranslationImportProblem(
                    TranslationImportProblemKind.WrongBatchId,
                    TranslationImportProblemSeverity.Fatal,
                    $"Compact numbered response belongs to batch '{compactHeader}', but current batch is '{manifest.BatchId}'.",
                    compactHeader));
            }
        }

        var parsedBlocks = ParseBlocks(cleaned, problems, out var markerSeen);
        var byId = manifest.Items.ToDictionary(item => item.Id);
        var byLegacyNumber = manifest.Items.ToDictionary(item => item.LegacyNumber);
        var byExportNumber = manifest.ExportItems
            .Where(item => item.ExportNumber is not null)
            .ToDictionary(item => item.ExportNumber!.Value);
        var assignments = new Dictionary<TranslationBatchItemId, (string Value, string RawValue, string Marker)>();
        var conflicts = new HashSet<TranslationBatchItemId>();
        var foundCount = 0;

        foreach (var block in parsedBlocks)
        {
            TranslationBatchManifestItem? suppliedItem = null;
            if (block.Id is { } stableId)
            {
                if (!byId.TryGetValue(stableId, out suppliedItem))
                {
                    var kind = manifest.Items.Any(item => item.Id.DictKey.Equals(stableId.DictKey, StringComparison.Ordinal))
                        ? TranslationImportProblemKind.InvalidPartIndex
                        : TranslationImportProblemKind.UnknownMarker;
                    problems.Add(new TranslationImportProblem(kind, TranslationImportProblemSeverity.Error,
                        $"Marker {block.Marker} does not identify a line in the copied batch.", block.Marker));
                    continue;
                }
            }
            else if (block.BatchId is { } batchId)
            {
                if (!batchId.Equals(manifest.BatchId, StringComparison.Ordinal))
                {
                    problems.Add(new TranslationImportProblem(
                        TranslationImportProblemKind.UnknownMarker,
                        TranslationImportProblemSeverity.Error,
                        $"Compact marker {block.Marker} belongs to another copied batch.",
                        block.Marker));
                    continue;
                }
                if (block.LegacyNumber is not { } compactNumber ||
                    !byLegacyNumber.TryGetValue(compactNumber, out suppliedItem))
                {
                    problems.Add(new TranslationImportProblem(
                        TranslationImportProblemKind.UnknownMarker,
                        TranslationImportProblemSeverity.Error,
                        $"Compact marker {block.Marker} is not present in the copied batch.",
                        block.Marker));
                    continue;
                }
            }
            else if (block.ExportNumber is { } exportNumber)
            {
                if (!byExportNumber.TryGetValue(exportNumber, out suppliedItem))
                {
                    problems.Add(new TranslationImportProblem(
                        TranslationImportProblemKind.UnknownMarker,
                        TranslationImportProblemSeverity.Error,
                        $"Compact numbered marker {block.Marker} is not present in the copied batch.",
                        block.Marker));
                    continue;
                }
            }
            else if (block.LegacyNumber is { } number && !byLegacyNumber.TryGetValue(number, out suppliedItem))
            {
                problems.Add(new TranslationImportProblem(
                    TranslationImportProblemKind.UnknownMarker,
                    TranslationImportProblemSeverity.Error,
                    $"Legacy marker {block.Marker} is not present in the copied batch.",
                    block.Marker));
                continue;
            }

            if (suppliedItem is null)
                continue;

            var translated = UnescapeCommonMarkdownPunctuation(block.Value);
            var keyPrefix = KeyPrefixRegex().Match(translated);
            if (keyPrefix.Success)
            {
                var suppliedKey = keyPrefix.Groups["key"].Value;
                if (!suppliedKey.Equals(suppliedItem.Entry.Key, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add(new TranslationImportProblem(
                        TranslationImportProblemKind.KeyMismatch,
                        TranslationImportProblemSeverity.Error,
                        $"Marker {block.Marker} contains dictionary key '{suppliedKey}', but '{suppliedItem.Entry.Key}' was expected.",
                        block.Marker));
                    continue;
                }
                translated = translated[keyPrefix.Length..];
            }

            var representativeId = suppliedItem.RepresentativeId;
            if (conflicts.Contains(representativeId))
                continue;
            if (assignments.TryGetValue(representativeId, out var previous))
            {
                if (previous.Value.Equals(translated, StringComparison.Ordinal))
                    continue;

                assignments.Remove(representativeId);
                conflicts.Add(representativeId);
                problems.Add(new TranslationImportProblem(
                    TranslationImportProblemKind.DuplicateMarkerConflict,
                    TranslationImportProblemSeverity.Error,
                    $"Marker {block.Marker} conflicts with an earlier value for the same translation line.",
                    block.Marker));
                continue;
            }

                assignments.Add(representativeId, (translated, block.Value, block.Marker));
            foundCount++;
        }

        if (!markerSeen)
        {
            var plainLines = SplitLines(cleaned);
            var targets = manifest.Items;
            if (plainLines.Length != targets.Count)
            {
                problems.Add(new TranslationImportProblem(
                    TranslationImportProblemKind.AmbiguousPlainText,
                    TranslationImportProblemSeverity.Fatal,
                    $"Plain text contains {plainLines.Length} physical lines, but the copied batch contains {targets.Count}."));
            }
            else
            {
                for (var index = 0; index < targets.Count; index++)
                {
                    var rawLine = plainLines[index];
                    assignments[targets[index].Id] = (UnescapeCommonMarkdownPunctuation(rawLine), rawLine, $"line {index + 1}");
                }
                foundCount = targets.Count;
            }
        }

        if (markerSeen)
            foundCount = assignments.Count;

        foreach (var assignment in assignments)
        {
            var targets = markerSeen
                ? manifest.Items.Where(item => item.RepresentativeId == assignment.Key || item.Id == assignment.Key).ToArray()
                : manifest.Items.Where(item => item.Id == assignment.Key).ToArray();
            if (targets.Length == 0)
                continue;
            for (var index = 0; index < targets.Length; index++)
            {
                var target = targets[index];
                var currentLines = SplitLines(target.Entry.Translation);
                var current = target.Id.PartIndex < currentLines.Length ? currentLines[target.Id.PartIndex] : string.Empty;
                var status = current.Equals(assignment.Value.Value, StringComparison.Ordinal)
                    ? TranslationBatchImportStatus.Unchanged
                    : !overwriteExisting && !string.IsNullOrWhiteSpace(current)
                        ? TranslationBatchImportStatus.SkippedExisting
                        : TranslationBatchImportStatus.Change;
                var flags = TranslationBatchImportFlags.None;
                var detail = assignment.Value.Marker;
                if (status == TranslationBatchImportStatus.Change && IsSuspiciouslyLong(target.SourceLine, assignment.Value.Value))
                {
                    flags |= TranslationBatchImportFlags.SuspiciousLength;
                    detail = string.IsNullOrWhiteSpace(detail)
                        ? "Suspiciously long translation."
                        : detail + "; suspiciously long translation";
                }
                if (IsPossiblyUntranslated(target.SourceLine, assignment.Value.Value, targetLocale))
                {
                    flags |= TranslationBatchImportFlags.PossiblyUntranslated;
                    detail = string.IsNullOrWhiteSpace(detail)
                        ? "Possibly untranslated."
                        : detail + "; possibly untranslated";
                }
                if (ContainsCommonMarkdownEscapes(assignment.Value.RawValue))
                {
                    flags |= TranslationBatchImportFlags.MarkdownEscaped;
                    detail = AppendDetail(detail, "Markdown escapes were cleaned");
                }
                if (IsProtectedTechnicalTextChanged(target.SourceLine, assignment.Value.Value))
                {
                    flags |= TranslationBatchImportFlags.TechnicalTextChanged;
                    detail = AppendDetail(detail, "rejected: technical/source-like text changed");
                    if (status == TranslationBatchImportStatus.Change)
                        status = TranslationBatchImportStatus.Rejected;
                }
                if (ProtectedTermsChanged(target.SourceLine, assignment.Value.Value))
                {
                    flags |= TranslationBatchImportFlags.ProtectedTermChanged;
                    detail = AppendDetail(detail, "protected aviation/code term changed");
                }
                planItems.Add(new TranslationBatchImportItem(
                    target.Id,
                    target.LegacyNumber,
                    target.Entry,
                    target.SourceLine,
                    current,
                    assignment.Value.Value,
                    status,
                    index == 0 ? targets.Length - 1 : 0,
                    detail,
                    target.ExportNumber,
                    flags));
            }
        }

        if (markerSeen)
        {
            var assignedRepresentatives = assignments.Keys.ToHashSet();
            foreach (var missing in manifest.ExportItems.Where(item => !assignedRepresentatives.Contains(item.RepresentativeId)))
            {
                var currentLines = SplitLines(missing.Entry.Translation);
                var current = missing.Id.PartIndex < currentLines.Length ? currentLines[missing.Id.PartIndex] : string.Empty;
                planItems.Add(new TranslationBatchImportItem(
                    missing.Id,
                    missing.LegacyNumber,
                    missing.Entry,
                    missing.SourceLine,
                    current,
                    current,
                    TranslationBatchImportStatus.Missing,
                    missing.AliasCount,
                    "No marker was returned for this line; the existing translation will be preserved.",
                    missing.ExportNumber));
            }
        }

        return new TranslationBatchImportPlan(
            manifest,
            planItems.OrderBy(item => item.LegacyNumber),
            problems,
            overwriteExisting,
            snapshotFingerprint,
            snapshotShapeFingerprint,
            foundCount);
    }

    public static TranslationBatchImportResult Apply(TranslationBatchImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.HasFatalProblems)
            return new TranslationBatchImportResult(plan.FoundCount, 0, plan.RejectedCount, "The import plan contains a fatal problem.");
        if (!ComputeContentFingerprint(plan.Manifest.Items).Equals(plan.SnapshotFingerprint, StringComparison.Ordinal) ||
            !ComputeShapeFingerprint(plan.Manifest.Items).Equals(plan.SnapshotShapeFingerprint, StringComparison.Ordinal))
            return new TranslationBatchImportResult(plan.FoundCount, 0, plan.RejectedCount,
                "The mission or translations changed after preview. Analyze the clipboard again.");

        var changedEntries = 0;
        foreach (var group in plan.Items
                     .Where(item => item.Status == TranslationBatchImportStatus.Change)
                     .GroupBy(item => item.Entry))
        {
            var entry = group.Key;
            var existingLines = SplitLines(entry.Translation).ToList();
            var requiredCount = group.Max(item => item.Id.PartIndex) + 1;
            while (existingLines.Count < requiredCount)
                existingLines.Add(string.Empty);
            foreach (var item in group)
                existingLines[item.Id.PartIndex] = item.ProposedTranslation;
            entry.Translation = JoinLines(existingLines, entry.Translation, entry.SourceText);
            changedEntries++;
        }

        return new TranslationBatchImportResult(plan.FoundCount, changedEntries, plan.RejectedCount);
    }

    public static TranslationBatchImportResult Apply(
        string text,
        IReadOnlyList<TranslationBatchLine> batchLines,
        bool overwriteExisting)
    {
        var manifest = BuildManifest(batchLines, deduplicate: false);
        return Apply(Analyze(text, manifest, overwriteExisting));
    }

    private static TranslationBatchImportResult ApplyLegacy(
        string text,
        IReadOnlyList<TranslationBatchLine> batchLines,
        bool overwriteExisting)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TranslationBatchImportResult(0, 0, 0, "Clipboard does not contain translation text.");
        if (batchLines.Count == 0)
            return new TranslationBatchImportResult(0, 0, 0, "The current filter contains no translation lines.");

        var byNumber = batchLines.ToDictionary(line => line.Number);
        var imported = new Dictionary<int, string>();
        var ignored = 0;

        foreach (var rawLine in SplitLines(text))
        {
            var match = NumberedLineRegex().Match(rawLine);
            if (!match.Success)
            {
                if (!string.IsNullOrWhiteSpace(rawLine) &&
                    rawLine is not "---" &&
                    !rawLine.StartsWith("```", StringComparison.Ordinal))
                {
                    ignored++;
                }
                continue;
            }

            if (!int.TryParse(match.Groups["number"].Value, out var number) || !byNumber.TryGetValue(number, out var expectedLine))
            {
                ignored++;
                continue;
            }

            var translated = UnescapeCommonMarkdownPunctuation(match.Groups["text"].Value);
            var keyPrefix = KeyPrefixRegex().Match(translated);
            if (keyPrefix.Success)
            {
                var suppliedKey = keyPrefix.Groups["key"].Value;
                if (!suppliedKey.Equals(expectedLine.Entry.Key, StringComparison.OrdinalIgnoreCase))
                {
                    ignored++;
                    continue;
                }
                translated = translated[keyPrefix.Length..];
            }
            imported[number] = translated;
        }

        if (imported.Count == 0)
        {
            // The editor deliberately hides [[number]] and DictKey prefixes. When it
            // supplies plain text, keep its physical line layout (including empty
            // lines) so multiline entries stay aligned with their batch lines.
            var simpleLines = new List<string>();
            foreach (var rawLine in SplitLines(text))
            {
                var m = NumberedLineRegex().Match(rawLine);
                if (m.Success)
                    continue;
                if (rawLine is "---")
                    continue;
                if (rawLine.StartsWith("```", StringComparison.Ordinal))
                    continue;

                // Strip optional key prefix like [DictKey_...] if present.
                var keyPref = KeyPrefixRegex().Match(rawLine);
                var lineText = keyPref.Success ? rawLine[keyPref.Length..] : rawLine;
                simpleLines.Add(lineText);
            }

            if (simpleLines.Count == 0)
            {
                return new TranslationBatchImportResult(
                    0,
                    0,
                    ignored,
                    "No [[number]] markers were recognized. Copy the text with markers or paste plain lines to map sequentially.");
            }

            // Map sequential plain lines to the batch lines in order.
            var orderedNumbers = batchLines.Select(l => l.Number).ToList();
            for (var i = 0; i < Math.Min(simpleLines.Count, orderedNumbers.Count); i++)
                imported[orderedNumbers[i]] = simpleLines[i];
        }

        var changedEntries = 0;
        foreach (var group in batchLines.GroupBy(line => line.Entry))
        {
            var entry = group.Key;
            var existingLines = SplitLines(entry.Translation).ToList();
            var requiredCount = group.Max(line => line.EntryLineIndex) + 1;
            while (existingLines.Count < requiredCount)
                existingLines.Add(string.Empty);

            var changed = false;
            foreach (var line in group)
            {
                if (!imported.TryGetValue(line.Number, out var value))
                    continue;
                if (!overwriteExisting && !string.IsNullOrWhiteSpace(existingLines[line.EntryLineIndex]))
                    continue;
                if (existingLines[line.EntryLineIndex] == value)
                    continue;

                existingLines[line.EntryLineIndex] = value;
                changed = true;
            }

            if (!changed)
                continue;

            entry.Translation = JoinLines(existingLines, entry.Translation, entry.SourceText);
            changedEntries++;
        }

        return new TranslationBatchImportResult(imported.Count, changedEntries, ignored);
    }

    private static List<ParsedBlock> ParseBlocks(
        string text,
        ICollection<TranslationImportProblem> problems,
        out bool markerSeen)
    {
        var result = new List<ParsedBlock>();
        ParsedBlockBuilder? current = null;
        markerSeen = false;
        var preferExportNumbers = FindCompactBatchHeader(text) is not null || CompactNumberedLineRegex().IsMatch(text);

        foreach (var line in SplitLines(text))
        {
            if (FenceRegex().IsMatch(line) || BatchHeaderRegex().IsMatch(line))
                continue;

            var compactNumbered = CompactNumberedLineRegex().Match(line);
            if (compactNumbered.Success)
            {
                markerSeen = true;
                if (current is not null)
                    result.Add(current.Build());

                if (!int.TryParse(compactNumbered.Groups["number"].Value, out var exportNumber) || exportNumber <= 0)
                {
                    problems.Add(new TranslationImportProblem(
                        TranslationImportProblemKind.InvalidMarker,
                        TranslationImportProblemSeverity.Error,
                        $"Marker '{compactNumbered.Value}' is not a valid compact numbered identifier.",
                        compactNumbered.Value));
                    current = null;
                    continue;
                }

                current = new ParsedBlockBuilder(
                    compactNumbered.Groups["marker"].Value,
                    null,
                    null,
                    null,
                    exportNumber,
                    compactNumbered.Groups["text"].Value);
                continue;
            }

            var match = BlockMarkerRegex().Match(line);
            if (match.Success)
            {
                markerSeen = true;
                if (current is not null)
                    result.Add(current.Build());

                TranslationBatchItemId? id = null;
                string? batchId = null;
                int? legacyNumber = null;
                int? exportNumber = null;
                var marker = match.Groups["marker"].Value;
                if (marker.StartsWith("[[MZ1|", StringComparison.Ordinal))
                {
                    if (!TryParseMarker(marker, out var parsedId))
                    {
                        problems.Add(new TranslationImportProblem(
                            TranslationImportProblemKind.InvalidMarker,
                            TranslationImportProblemSeverity.Error,
                            $"Marker '{marker}' is not a valid MZ1 identifier.",
                            marker));
                        current = null;
                        continue;
                    }
                    id = parsedId;
                }
                else if (marker.StartsWith("[[M2|", StringComparison.Ordinal))
                {
                    batchId = match.Groups["batch"].Value;
                    if (!int.TryParse(match.Groups["number"].Value, out var compactNumber) || compactNumber <= 0)
                    {
                        problems.Add(new TranslationImportProblem(
                            TranslationImportProblemKind.InvalidMarker,
                            TranslationImportProblemSeverity.Error,
                            $"Marker '{marker}' is not a valid M2 identifier.",
                            marker));
                        current = null;
                        continue;
                    }
                    legacyNumber = compactNumber;
                }
                else if (int.TryParse(match.Groups["number"].Value, out var number))
                {
                    if (preferExportNumbers && !marker.StartsWith("[[", StringComparison.Ordinal) && !marker.StartsWith("🔹", StringComparison.Ordinal))
                        exportNumber = number;
                    else
                        legacyNumber = number;
                }

                current = new ParsedBlockBuilder(marker, id, batchId, legacyNumber, exportNumber, match.Groups["text"].Value);
                continue;
            }

            if (line.Contains("[[MZ1|", StringComparison.Ordinal) ||
                line.Contains("[[M2|", StringComparison.Ordinal))
            {
                markerSeen = true;
                if (current is not null)
                {
                    result.Add(current.Build());
                    current = null;
                }
                problems.Add(new TranslationImportProblem(
                    TranslationImportProblemKind.InvalidMarker,
                    TranslationImportProblemSeverity.Error,
                    "A malformed translation marker was rejected.",
                    line.Trim()));
                continue;
            }

            current?.Append(line);
        }

        if (current is not null)
            result.Add(current.Build());
        return result;
    }

    private static string CleanupResponse(string value, string? generatedPrompt)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (!string.IsNullOrEmpty(generatedPrompt))
        {
            var prompt = generatedPrompt.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var promptIndex = normalized.IndexOf(prompt, StringComparison.Ordinal);
            if (promptIndex >= 0)
            {
                normalized = normalized.Remove(promptIndex, prompt.Length).Trim();
                if (normalized.StartsWith("---", StringComparison.Ordinal))
                    normalized = normalized[3..].TrimStart('\n', ' ', '\t');
            }
        }

        var lines = normalized.Split('\n').ToList();
        if (lines.Count >= 2 && FenceRegex().IsMatch(lines[0]) && FenceRegex().IsMatch(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
            lines.RemoveAt(0);
        }
        return string.Join('\n', lines);
    }

    private static string? FindCompactBatchHeader(string text)
    {
        foreach (var line in SplitLines(text))
        {
            var match = BatchHeaderRegex().Match(UnescapeCommonMarkdownPunctuation(line));
            if (match.Success)
                return match.Groups["id"].Value;
        }
        return null;
    }

    private static bool IsSuspiciouslyLong(string source, string translation)
        => translation.Length > Math.Max(40, (int)(source.Length * SuspiciousLengthRatio));

    private static bool IsPossiblyUntranslated(string source, string translation, string? targetLocale)
    {
        if (!ExpectsCyrillic(targetLocale) || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translation))
            return false;
        if (ContainsCyrillic(translation))
            return false;
        if (source.Trim().Equals(translation.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        var latinLetters = translation.Count(static c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        return latinLetters >= 8 && translation.Any(char.IsWhiteSpace);
    }

    private static string AppendDetail(string? detail, string addition)
        => string.IsNullOrWhiteSpace(detail) ? addition : detail + "; " + addition;

    private static bool ContainsCommonMarkdownEscapes(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        for (var index = 0; index + 1 < value.Length; index++)
        {
            if (value[index] == '\\' && IsCommonMarkdownEscapedPunctuation(value[index + 1]))
                return true;
        }
        return false;
    }

    private static bool IsProtectedTechnicalTextChanged(string source, string translation)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;
        if (!LooksProtectedTechnical(source))
            return false;
        return !source.Trim().Equals(translation.Trim(), StringComparison.Ordinal);
    }

    private static bool LooksProtectedTechnical(string source)
    {
        var trimmed = source.Trim();
        if (ResourceStatusLineRegex().IsMatch(trimmed))
            return true;
        if (AllCapsIdentifierRegex().IsMatch(trimmed))
            return true;
        if (trimmed.Contains('\\', StringComparison.Ordinal) || trimmed.Contains("%USERPROFILE%", StringComparison.OrdinalIgnoreCase))
            return true;
        if (LuaLogicLineRegex().IsMatch(trimmed))
            return true;
        if ((trimmed.StartsWith("if ", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("elseif ", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("while ", StringComparison.OrdinalIgnoreCase)) &&
            LuaComparisonOrBooleanRegex().IsMatch(trimmed))
            return true;
        if (GenericCodeCallRegex().IsMatch(trimmed) &&
            (trimmed.Contains('.', StringComparison.Ordinal) ||
             trimmed.Contains(':', StringComparison.Ordinal) ||
             trimmed.Contains('=', StringComparison.Ordinal) ||
             trimmed.Contains('"', StringComparison.Ordinal) ||
             trimmed.Contains('\'', StringComparison.Ordinal)))
            return true;

        var hasBlockStructure = trimmed.Contains('\n', StringComparison.Ordinal) || trimmed.Contains('\r', StringComparison.Ordinal);
        var hasAssignment = trimmed.Contains('=', StringComparison.Ordinal);
        return hasBlockStructure && hasAssignment && LuaControlFlowLineRegex().IsMatch(trimmed);
    }

    private static bool ProtectedTermsChanged(string source, string translation)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;

        foreach (Match match in ProtectedCodeTokenRegex().Matches(source))
        {
            var token = match.Value;
            if (!translation.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (Match match in FirPhraseRegex().Matches(source))
        {
            var phrase = match.Value;
            if (!translation.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool ExpectsCyrillic(string? targetLocale)
        => !string.IsNullOrWhiteSpace(targetLocale) &&
           (targetLocale.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ||
            targetLocale.StartsWith("be", StringComparison.OrdinalIgnoreCase) ||
            targetLocale.StartsWith("uk", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsCyrillic(string value)
        => value.Any(static c => c is >= '\u0400' and <= '\u04FF');

    private static string ComputeContentFingerprint(IReadOnlyList<TranslationBatchManifestItem> items)
    {
        var canonical = new StringBuilder();
        AppendFingerprintValue(canonical, CultureInfo.CurrentUICulture.Name);
        foreach (var item in items)
        {
            AppendFingerprintValue(canonical, item.Id.DictKey);
            canonical.Append(item.Id.PartIndex).Append(':');
            AppendFingerprintValue(canonical, item.SourceLine);
            AppendFingerprintValue(canonical, item.Entry.SourceText);
            AppendFingerprintValue(canonical, item.Entry.Translation);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static string ComputeShapeFingerprint(IReadOnlyList<TranslationBatchManifestItem> items)
    {
        var canonical = new StringBuilder();
        AppendFingerprintValue(canonical, CultureInfo.CurrentUICulture.Name);
        foreach (var item in items.OrderBy(item => item.ExportNumber ?? int.MaxValue).ThenBy(item => item.LegacyNumber))
        {
            canonical.Append(item.ExportNumber ?? 0).Append(':');
            AppendFingerprintValue(canonical, item.Id.DictKey);
            canonical.Append(item.Id.PartIndex).Append(':');
            AppendFingerprintValue(canonical, item.RepresentativeId.DictKey);
            canonical.Append(item.RepresentativeId.PartIndex).Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendFingerprintValue(StringBuilder builder, string value)
        => builder.Append(value.Length).Append(':').Append(value).Append(';');

    private static string[] SplitLines(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static string JoinLines(IReadOnlyList<string> lines, string translation, string source)
    {
        var sample = translation.Length > 0 ? translation : source;
        var separator = sample.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return string.Join(separator, lines);
    }

    private static string UnescapeCommonMarkdownPunctuation(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('\\', StringComparison.Ordinal))
            return value;

        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current == '\\' &&
                index + 1 < value.Length &&
                IsCommonMarkdownEscapedPunctuation(value[index + 1]))
            {
                result.Append(value[++index]);
                continue;
            }

            result.Append(current);
        }

        return result.ToString();
    }

    private static bool IsCommonMarkdownEscapedPunctuation(char value)
        => value is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '(' or ')' or '#' or '+' or '-' or '.' or '!' or '>' or '|';

    [GeneratedRegex(@"^\s*(?:[-+*>]\s*)?(?:\*\*|__)?(?:\[\[\s*(?<number>\d+)\s*\]\]|🔹\s*(?<number>\d+)\s*🔹|(?<number>\d+)\s*(?:\.(?!\d)|\)))(?:\*\*|__)?\s*(?<text>.*)$")]
    private static partial Regex NumberedLineRegex();

    [GeneratedRegex(@"^\s*(?:(?:[-+*>]|#{1,6})\s*)?(?:\*\*|__|`)?(?<marker>\[\[M2\|(?<batch>[A-Za-z0-9_-]{8})\|(?<number>\d+)\]\]|\[\[MZ1\|[A-Za-z0-9_-]+\|\d+\]\]|\[\[\s*(?<number>\d+)\s*\]\]|🔹\s*(?<number>\d+)\s*🔹|(?<number>\d+)\s*(?:\.(?!\d)|\)|:))(?:\*\*|__|`)?\s*(?<text>.*)$")]
    private static partial Regex BlockMarkerRegex();

    [GeneratedRegex(@"^\s*(?:[-+*>]\s*)?(?:\*\*|__|`)?(?<marker>(?<number>\d+)\s?\u00BB)(?:\*\*|__|`)?\s?(?<text>.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CompactNumberedLineRegex();

    [GeneratedRegex(@"^\s*(?:Batch|BatchId|Batch ID):\s*(?<id>[A-Za-z0-9_-]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BatchHeaderRegex();

    [GeneratedRegex(@"^\[\[MZ1\|(?<key>[A-Za-z0-9_-]+)\|(?<part>\d+)\]\]$")]
    private static partial Regex Mz1OnlyRegex();

    [GeneratedRegex(@"^\s*(?:```|~~~)[^`~]*\s*$")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^\s*(?:\[\[M2\|[A-Za-z0-9_-]{8}\|(?<number>\d+)\]\]|\[\[MZ1\|[A-Za-z0-9_-]+\|(?<number>\d+)\]\]|\[\[\s*(?<number>\d+)\s*\]\])\s*(?<text>.*)$")]
    private static partial Regex DisplayNumberedLineRegex();

    [GeneratedRegex(@"^\s*\d+\s*│\s?(?<text>.*)$")]
    private static partial Regex DisplayLineNumberPrefixRegex();

    [GeneratedRegex(@"^\s*(?:\*\*|__)?\[(?<key>(?:DictKey|dictionary)[^\]]*)\](?:\*\*|__)?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex KeyPrefixRegex();

    [GeneratedRegex(@"^\s*[\w./\\-]+\.(?:lua|miz|ogg|wav|mp3|png|jpe?g|bmp)\s+(?:loaded|loading|started|initialized|failed|error|missing|not\s+found)\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResourceStatusLineRegex();

    [GeneratedRegex(@"^[A-Z0-9]+(?:_[A-Z0-9]+){1,}$", RegexOptions.CultureInvariant)]
    private static partial Regex AllCapsIdentifierRegex();

    [GeneratedRegex(@"\b(?:if|else|elseif|then|end|return|local|function|for|while|repeat|until|do|break)\b", RegexOptions.CultureInvariant)]
    private static partial Regex LuaControlFlowLineRegex();

    [GeneratedRegex(@"^\s*(?:(?:if|elseif|while)\s+.+\s+then|else\s*$|end\s*$|return\b.+|local\s+\w+|for\s+\w+\s*=.+\s+do|(?:\w+\.)*\w+\s*=\s*.+|(?:\w+\.)*\w+\s*(?:\+=|-=)\s*.+|(?:\w+\.)*\w+\s*[+\-*/%]=?\s*\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LuaLogicLineRegex();

    [GeneratedRegex(@"(?:==|~=|<=|>=|<|>|\band\b|\bor\b|\bnot\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LuaComparisonOrBooleanRegex();

    [GeneratedRegex(@"^\s*[A-Za-z_]\w*(?:[.:][A-Za-z_]\w*)*\s*\([^)]*(?:[A-Za-z_]\w*|['""]|[-+]?\d)[^)]*\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex GenericCodeCallRegex();

    [GeneratedRegex(@"\b(?:F10|AWACS|CAP|UHF|VHF|TACAN|ILS|QNH|QNE|ON|OFF|IDLE|ARM|SAFE|EXT|EXTEND|RETRACT|NORM|HÖJD|SPAK|ATT|EBK|MSL|KIAS|FIR)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProtectedCodeTokenRegex();

    [GeneratedRegex(@"\b[A-Z][A-Z0-9-]+\s+FIR\b", RegexOptions.CultureInvariant)]
    private static partial Regex FirPhraseRegex();

    private sealed record ParsedBlock(
        string Marker,
        TranslationBatchItemId? Id,
        string? BatchId,
        int? LegacyNumber,
        int? ExportNumber,
        string Value);

    private sealed class ParsedBlockBuilder(
        string marker,
        TranslationBatchItemId? id,
        string? batchId,
        int? legacyNumber,
        int? exportNumber,
        string firstLine)
    {
        private readonly List<string> _lines = [firstLine];

        public void Append(string line) => _lines.Add(line);

        public ParsedBlock Build()
        {
            var value = string.Join(" ", _lines.Select(line => line.Trim()).Where(line => line.Length > 0));
            return new ParsedBlock(marker, id, batchId, legacyNumber, exportNumber, value);
        }
    }
}
