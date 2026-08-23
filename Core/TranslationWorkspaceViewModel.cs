using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MizEdit.Core;

/// <summary>
/// Immutable copy of every workspace filter. Construct this object on the UI
/// thread; the workspace builder never reads controls or collection views.
/// </summary>
public sealed record TranslationWorkspaceFilterOptions(
    bool OnlyMissing = false,
    bool OnlyChanged = false,
    bool SkipEmptySource = true,
    bool HideTechnical = true,
    bool IncludeActionRadioText = true,
    bool IncludeActionText = true,
    bool IncludeDescription = true,
    bool IncludeSubtitle = true,
    bool IncludeSortie = true,
    bool IncludeName = true,
    bool IncludeOther = true,
    string SearchText = "",
    bool Deduplicate = true,
    string? GeneratedPrompt = null);

/// <summary>
/// An immutable, UI-thread-captured input. It is safe to pass to Task.Run.
/// </summary>
public sealed class TranslationWorkspaceBuildInput
{
    private readonly ReadOnlyCollection<TranslationWorkspaceEntrySnapshot> _entries;

    internal TranslationWorkspaceBuildInput(
        long generation,
        TranslationWorkspaceFilterOptions options,
        string uiCultureName,
        TranslationWorkspaceEntrySnapshot[] entries)
    {
        Generation = generation;
        Options = options;
        UiCultureName = uiCultureName;
        _entries = Array.AsReadOnly(entries);
    }

    public long Generation { get; }
    public TranslationWorkspaceFilterOptions Options { get; }
    public string UiCultureName { get; }
    public IReadOnlyList<TranslationWorkspaceEntrySnapshot> Entries => _entries;
}

/// <summary>
/// Immutable values captured from a TranslationEntry. Source/translation line
/// collections are read-only and can be consumed on a worker thread.
/// </summary>
public sealed class TranslationWorkspaceEntrySnapshot
{
    private readonly ReadOnlyCollection<string> _sourceLines;
    private readonly ReadOnlyCollection<string> _translationLines;

    internal TranslationWorkspaceEntrySnapshot(
        TranslationEntry entry,
        string key,
        string sourceText,
        string translation,
        bool isDirty,
        bool isTechnical,
        bool isBackendPlaceholder,
        string[] sourceLines,
        string[] translationLines)
    {
        Entry = entry;
        Key = key;
        SourceText = sourceText;
        Translation = translation;
        IsDirty = isDirty;
        IsTechnical = isTechnical;
        IsBackendPlaceholder = isBackendPlaceholder;
        _sourceLines = Array.AsReadOnly(sourceLines);
        _translationLines = Array.AsReadOnly(translationLines);
    }

    public TranslationEntry Entry { get; }
    public string Key { get; }
    public string SourceText { get; }
    public string Translation { get; }
    public bool IsDirty { get; }
    public bool IsTechnical { get; }
    public bool IsBackendPlaceholder { get; }
    public IReadOnlyList<string> SourceLines => _sourceLines;
    public IReadOnlyList<string> TranslationLines => _translationLines;
}

/// <summary>
/// One atomic workspace calculation. Consumers should call IsCurrent before
/// publishing it, because a newer generation may have completed meanwhile.
/// </summary>
public sealed class TranslationWorkspaceSnapshot
{
    private readonly ReadOnlyCollection<TranslationEntry> _visibleEntries;
    private readonly ReadOnlyCollection<TranslationBatchLine> _batchLines;

    internal TranslationWorkspaceSnapshot(
        long generation,
        TranslationWorkspaceFilterOptions options,
        TranslationEntry[] visibleEntries,
        TranslationBatchLine[] batchLines,
        TranslationCorpusMetrics metrics,
        TranslationBatchManifest manifest)
    {
        Generation = generation;
        Options = options;
        _visibleEntries = Array.AsReadOnly(visibleEntries);
        _batchLines = Array.AsReadOnly(batchLines);
        Metrics = metrics;
        Manifest = manifest;
    }

    public long Generation { get; }
    public TranslationWorkspaceFilterOptions Options { get; }
    public IReadOnlyList<TranslationEntry> VisibleEntries => _visibleEntries;
    public IReadOnlyList<TranslationBatchLine> BatchLines => _batchLines;
    public TranslationCorpusMetrics Metrics { get; }
    public TranslationBatchManifest Manifest { get; }
}

/// <summary>
/// Performs the expensive filtering, physical-line expansion, metrics and
/// manifest construction away from the WPF dispatcher. Capture must be called
/// on the owning/UI thread; BuildSnapshotAsync only uses captured values.
/// </summary>
public sealed class TranslationWorkspaceViewModel
{
    private const string DefaultTranslationPrompt =
        "Переведи с английского на естественный русский текст миссии DCS, используя принятую авиационную терминологию.\n" +
        "Верни каждый маркер без изменений и переводи только текст после него. Не добавляй и не удаляй маркеры.\n" +
        "Не переводи и не изменяй Lua-код, переменные, пути, имена ресурсов, плейсхолдеры, форматирование, числа, единицы, частоты, координаты, позывные и названия точек.\n" +
        "Сохраняй F10/AWACS/CAP/UHF/VHF/TACAN/ILS/QNH и обозначения каналов, если нет однозначного русского эквивалента.\n" +
        "Не выдумывай единицы и факты; сомнительный термин оставь как в оригинале. Без Markdown, заголовков, пояснений и лишнего текста.";

    private readonly object _cacheGate = new();
    private readonly Dictionary<TranslationEntry, CachedSource> _sourceCache =
        new(ReferenceEqualityComparer.Instance);
    private readonly Func<string, bool> _technicalClassifier;
    private long _generation;

    public TranslationWorkspaceViewModel(Func<string, bool>? technicalClassifier = null)
    {
        _technicalClassifier = technicalClassifier ?? (_ => false);
    }

    public long CurrentGeneration => Interlocked.Read(ref _generation);

    /// <summary>
    /// Reads TranslationEntry values synchronously. Call this on the UI thread.
    /// Capturing a new request advances the generation and makes older results stale.
    /// </summary>
    public TranslationWorkspaceBuildInput Capture(
        IEnumerable<TranslationEntry> entries,
        TranslationWorkspaceFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);

        lock (_cacheGate)
        {
            var generation = Interlocked.Increment(ref _generation);
            var captured = entries.Select(CaptureEntry).ToArray();
            return new TranslationWorkspaceBuildInput(
                generation,
                options with { SearchText = options.SearchText?.Trim() ?? string.Empty },
                CultureInfo.CurrentUICulture.Name,
                captured);
        }
    }

    public Task<TranslationWorkspaceSnapshot> BuildSnapshotAsync(
        TranslationWorkspaceBuildInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Task.Run(() => BuildSnapshot(input, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Convenience API whose synchronous capture happens before Task.Run.
    /// Call this method itself from the UI thread.
    /// </summary>
    public Task<TranslationWorkspaceSnapshot> CaptureAndBuildSnapshotAsync(
        IEnumerable<TranslationEntry> entries,
        TranslationWorkspaceFilterOptions options,
        CancellationToken cancellationToken = default)
        => BuildSnapshotAsync(Capture(entries, options), cancellationToken);

    public bool IsCurrent(TranslationWorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Generation == CurrentGeneration;
    }

    public long InvalidateChangedEntry(TranslationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_cacheGate)
        {
            _sourceCache.Remove(entry);
            return Interlocked.Increment(ref _generation);
        }
    }

    public long InvalidateAll()
    {
        lock (_cacheGate)
        {
            _sourceCache.Clear();
            return Interlocked.Increment(ref _generation);
        }
    }

    public static bool IsBackendPlaceholder(string key, string sourceText)
        => key.StartsWith("DictKey_", StringComparison.OrdinalIgnoreCase) &&
           sourceText.Trim().Equals(key, StringComparison.OrdinalIgnoreCase);

    private TranslationWorkspaceEntrySnapshot CaptureEntry(TranslationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var sourceText = entry.SourceText ?? string.Empty;
        if (!_sourceCache.TryGetValue(entry, out var cached) ||
            !string.Equals(cached.SourceText, sourceText, StringComparison.Ordinal))
        {
            cached = new CachedSource(
                sourceText,
                SplitLines(sourceText),
                _technicalClassifier(sourceText),
                IsBackendPlaceholder(entry.Key, sourceText));
            _sourceCache[entry] = cached;
        }

        var translation = entry.Translation ?? string.Empty;
        return new TranslationWorkspaceEntrySnapshot(
            entry,
            entry.Key,
            sourceText,
            translation,
            entry.IsDirty,
            cached.IsTechnical,
            cached.IsBackendPlaceholder,
            cached.SourceLines,
            SplitLines(translation));
    }

    private static TranslationWorkspaceSnapshot BuildSnapshot(
        TranslationWorkspaceBuildInput input,
        CancellationToken cancellationToken)
    {
        var visibleEntries = new List<TranslationEntry>(input.Entries.Count);
        var visibleLines = new List<TranslationBatchLine>();
        var allPhysicalLines = 0;
        var translatedLines = 0;
        var changedLines = 0;
        var excludedEmpty = 0;
        var excludedTechnical = 0;
        var excludedOther = 0;
        var number = 1;
        var culture = GetCulture(input.UiCultureName);

        foreach (var entry in input.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var physicalCount = Math.Max(entry.SourceLines.Count, entry.TranslationLines.Count);
            if (physicalCount == 0)
                physicalCount = 1;
            allPhysicalLines += physicalCount;
            if (entry.IsDirty)
                changedLines += physicalCount;

            for (var index = 0; index < physicalCount; index++)
            {
                if (index < entry.TranslationLines.Count &&
                    !string.IsNullOrWhiteSpace(entry.TranslationLines[index]))
                {
                    translatedLines++;
                }
            }

            // Mutually exclusive classification preserves the corpus invariant.
            if (input.Options.SkipEmptySource && string.IsNullOrWhiteSpace(entry.SourceText))
            {
                excludedEmpty += physicalCount;
                continue;
            }
            // A source value that only repeats its own DictKey is backend metadata,
            // never human-readable mission text. Keep it out of the UI and export
            // even when the user chooses to show other technical content.
            if (entry.IsBackendPlaceholder)
            {
                excludedTechnical += physicalCount;
                continue;
            }
            if (input.Options.HideTechnical && entry.IsTechnical)
            {
                excludedTechnical += physicalCount;
                continue;
            }
            if (!MatchesOtherFilters(entry, input.Options, culture))
            {
                excludedOther += physicalCount;
                continue;
            }

            visibleEntries.Add(entry.Entry);
            for (var index = 0; index < physicalCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                visibleLines.Add(new TranslationBatchLine(
                    number++,
                    entry.Entry,
                    index,
                    index < entry.SourceLines.Count ? entry.SourceLines[index] : string.Empty,
                    index < entry.TranslationLines.Count ? entry.TranslationLines[index] : string.Empty,
                    index == 0));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var lines = visibleLines.ToArray();
        var manifest = BuildManifest(lines, input, input.Options.Deduplicate, cancellationToken);
        var metrics = new TranslationCorpusMetrics(
            TotalKeys: input.Entries.Count,
            TotalPhysicalLines: allPhysicalLines,
            VisibleKeys: visibleEntries.Count,
            VisibleLines: lines.Length,
            NonEmptyVisibleLines: lines.Count(line => !string.IsNullOrWhiteSpace(line.SourceLine)),
            FilledVisibleLines: lines.Count(line => !string.IsNullOrWhiteSpace(line.TranslationLine)),
            ExcludedEmptyLines: excludedEmpty,
            ExcludedTechnicalLines: excludedTechnical,
            ExcludedOtherLines: excludedOther,
            TranslatedLines: translatedLines,
            MissingLines: allPhysicalLines - translatedLines,
            ChangedLines: changedLines,
            UniqueExportLines: manifest.ExportItems.Count,
            DeduplicatedAliasLines: manifest.AliasCount);

        return new TranslationWorkspaceSnapshot(
            input.Generation,
            input.Options,
            visibleEntries.ToArray(),
            lines,
            metrics,
            manifest);
    }

    private static bool MatchesOtherFilters(
        TranslationWorkspaceEntrySnapshot entry,
        TranslationWorkspaceFilterOptions options,
        CultureInfo culture)
    {
        if (options.OnlyMissing && !string.IsNullOrWhiteSpace(entry.Translation))
            return false;
        if (options.OnlyChanged && !entry.IsDirty)
            return false;

        var key = entry.Key;
        var actionRadio = key.Contains("ActionRadioText", StringComparison.OrdinalIgnoreCase);
        var actionText = !actionRadio && key.Contains("ActionText", StringComparison.OrdinalIgnoreCase);
        var description = key.Contains("description", StringComparison.OrdinalIgnoreCase);
        var subtitle = key.Contains("subtitle", StringComparison.OrdinalIgnoreCase);
        var sortie = key.Contains("sortie", StringComparison.OrdinalIgnoreCase);
        var name = key.Contains("name", StringComparison.OrdinalIgnoreCase);
        var known = actionRadio || actionText || description || subtitle || sortie || name;

        if ((actionRadio && !options.IncludeActionRadioText) ||
            (actionText && !options.IncludeActionText) ||
            (description && !options.IncludeDescription) ||
            (subtitle && !options.IncludeSubtitle) ||
            (sortie && !options.IncludeSortie) ||
            (name && !options.IncludeName) ||
            (!known && !options.IncludeOther))
        {
            return false;
        }

        var query = options.SearchText;
        return query.Length == 0 ||
               Contains(entry.Key, query, culture) ||
               Contains(entry.SourceText, query, culture) ||
               Contains(entry.Translation, query, culture);
    }

    private static bool Contains(string value, string query, CultureInfo culture)
        => culture.CompareInfo.IndexOf(value, query, CompareOptions.IgnoreCase) >= 0;

    private static CultureInfo GetCulture(string name)
    {
        try
        {
            return string.IsNullOrEmpty(name) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private static TranslationBatchManifest BuildManifest(
        IReadOnlyList<TranslationBatchLine> lines,
        TranslationWorkspaceBuildInput input,
        bool deduplicate,
        CancellationToken cancellationToken)
    {
        IEqualityComparer<TranslationEntry> entryComparer = ReferenceEqualityComparer.Instance;
        var captureByEntry = input.Entries.ToDictionary(item => item.Entry, entryComparer);
        var representativeBySource = new Dictionary<string, TranslationBatchItemId>(StringComparer.Ordinal);
        var representativeById = new Dictionary<TranslationBatchItemId, TranslationBatchItemId>();
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captured = captureByEntry[line.Entry];
            var id = new TranslationBatchItemId(captured.Key, line.EntryLineIndex);
            var representative = id;
            if (deduplicate && line.SourceLine.Length > 0 &&
                !representativeBySource.TryGetValue(line.SourceLine, out representative))
            {
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
            cancellationToken.ThrowIfCancellationRequested();
            var captured = captureByEntry[line.Entry];
            var id = new TranslationBatchItemId(captured.Key, line.EntryLineIndex);
            var representative = representativeById[id];
            if (representative == id && !exportNumberByRepresentative.ContainsKey(representative))
                exportNumberByRepresentative.Add(representative, exportNumberByRepresentative.Count + 1);
        }

        var items = new List<TranslationBatchManifestItem>(lines.Count);
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captured = captureByEntry[line.Entry];
            var id = new TranslationBatchItemId(captured.Key, line.EntryLineIndex);
            var representative = representativeById[id];
            items.Add(new TranslationBatchManifestItem(
                id,
                line.Number,
                line.Entry,
                line.SourceLine,
                line.TranslationLine,
                representative,
                aliasCounts[representative],
                representative == id ? exportNumberByRepresentative[representative] : null));
        }

        var fingerprint = ComputeFingerprint(items, captureByEntry, input.UiCultureName);
        return new TranslationBatchManifest(
            items,
            deduplicate,
            fingerprint,
            input.Options.GeneratedPrompt ?? DefaultTranslationPrompt);
    }

    private static string ComputeFingerprint(
        IReadOnlyList<TranslationBatchManifestItem> items,
        IReadOnlyDictionary<TranslationEntry, TranslationWorkspaceEntrySnapshot> captures,
        string uiCultureName)
    {
        var canonical = new StringBuilder();
        AppendFingerprintValue(canonical, uiCultureName);
        foreach (var item in items)
        {
            var captured = captures[item.Entry];
            AppendFingerprintValue(canonical, item.Id.DictKey);
            canonical.Append(item.Id.PartIndex).Append(':');
            AppendFingerprintValue(canonical, item.SourceLine);
            AppendFingerprintValue(canonical, captured.SourceText);
            AppendFingerprintValue(canonical, captured.Translation);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendFingerprintValue(StringBuilder builder, string value)
        => builder.Append(value.Length).Append(':').Append(value).Append(';');

    private static string[] SplitLines(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private sealed record CachedSource(
        string SourceText,
        string[] SourceLines,
        bool IsTechnical,
        bool IsBackendPlaceholder);
}
