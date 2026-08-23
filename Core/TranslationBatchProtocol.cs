namespace MizEdit.Core;

public readonly record struct TranslationBatchItemId(string DictKey, int PartIndex)
{
    public override string ToString() => TranslationBatchDocument.FormatMarker(this);
}

public sealed record TranslationBatchManifestItem(
    TranslationBatchItemId Id,
    int LegacyNumber,
    TranslationEntry Entry,
    string SourceLine,
    string TranslationLine,
    TranslationBatchItemId RepresentativeId,
    int AliasCount,
    int? ExportNumber = null)
{
    public bool IsRepresentative => Id == RepresentativeId;
}

public sealed class TranslationBatchManifest
{
    private readonly IReadOnlyList<TranslationBatchManifestItem> _items;
    private readonly IReadOnlyList<TranslationBatchManifestItem> _exportItems;

    internal TranslationBatchManifest(
        IEnumerable<TranslationBatchManifestItem> items,
        bool deduplicated,
        string fingerprint,
        string? generatedPrompt,
        string? shapeFingerprint = null)
    {
        var itemArray = items.ToArray();
        _items = Array.AsReadOnly(itemArray);
        _exportItems = Array.AsReadOnly(itemArray.Where(item => item.IsRepresentative).ToArray());
        Deduplicated = deduplicated;
        Fingerprint = fingerprint;
        ContentFingerprint = fingerprint;
        ShapeFingerprint = shapeFingerprint ?? fingerprint;
        BatchId = TranslationBatchDocument.CreateCompactBatchId(fingerprint);
        GeneratedPrompt = generatedPrompt;
    }

    public IReadOnlyList<TranslationBatchManifestItem> Items => _items;
    public IReadOnlyList<TranslationBatchManifestItem> ExportItems => _exportItems;
    public bool Deduplicated { get; }
    public string Fingerprint { get; }
    public string ContentFingerprint { get; }
    public string ShapeFingerprint { get; }
    public string BatchId { get; }
    public string? GeneratedPrompt { get; }
    public int AliasCount => _items.Count - _exportItems.Count;
}

public enum TranslationBatchExportFormat
{
    CompactM2,
    CompactNumbered,
    FullMz1,
    LegacyWithDictKeys
}

public enum TranslationBatchImportStatus
{
    Change,
    Unchanged,
    Missing,
    SkippedExisting,
    Rejected,
    SuspiciousLength
}

[Flags]
public enum TranslationBatchImportFlags
{
    None = 0,
    PossiblyUntranslated = 1,
    SuspiciousLength = 2,
    MissingBatchId = 4,
    WrongBatchId = 8,
    MarkdownEscaped = 16,
    TechnicalTextChanged = 32,
    ProtectedTermChanged = 64
}

public enum TranslationImportProblemKind
{
    EmptyInput,
    EmptyManifest,
    InvalidMarker,
    UnknownMarker,
    InvalidPartIndex,
    DuplicateMarkerConflict,
    KeyMismatch,
    AmbiguousPlainText,
    StalePlan,
    MissingBatchId,
    WrongBatchId,
    PasteTooLarge,
    SuspiciousLength,
    PossiblyUntranslated
}

public enum TranslationImportProblemSeverity
{
    Warning,
    Error,
    Fatal
}

public sealed record TranslationImportProblem(
    TranslationImportProblemKind Kind,
    TranslationImportProblemSeverity Severity,
    string Message,
    string? Marker = null);

public sealed record TranslationBatchImportItem(
    TranslationBatchItemId Id,
    int LegacyNumber,
    TranslationEntry Entry,
    string SourceText,
    string CurrentTranslation,
    string ProposedTranslation,
    TranslationBatchImportStatus Status,
    int AliasCount = 0,
    string? Detail = null,
    int? ExportNumber = null,
    TranslationBatchImportFlags Flags = TranslationBatchImportFlags.None);

public sealed class TranslationBatchImportPlan
{
    private readonly IReadOnlyList<TranslationBatchImportItem> _items;
    private readonly IReadOnlyList<TranslationImportProblem> _problems;

    internal TranslationBatchImportPlan(
        TranslationBatchManifest manifest,
        IEnumerable<TranslationBatchImportItem> items,
        IEnumerable<TranslationImportProblem> problems,
        bool overwriteExisting,
        string snapshotFingerprint,
        string snapshotShapeFingerprint,
        int foundCount)
    {
        Manifest = manifest;
        _items = Array.AsReadOnly(items.ToArray());
        _problems = Array.AsReadOnly(problems.ToArray());
        OverwriteExisting = overwriteExisting;
        SnapshotFingerprint = snapshotFingerprint;
        SnapshotShapeFingerprint = snapshotShapeFingerprint;
        FoundCount = foundCount;
    }

    internal TranslationBatchImportPlan(
        TranslationBatchManifest manifest,
        IEnumerable<TranslationBatchImportItem> items,
        IEnumerable<TranslationImportProblem> problems,
        bool overwriteExisting,
        string snapshotFingerprint,
        int foundCount)
        : this(manifest, items, problems, overwriteExisting, snapshotFingerprint, manifest.ShapeFingerprint, foundCount)
    {
    }

    public TranslationBatchManifest Manifest { get; }
    public IReadOnlyList<TranslationBatchImportItem> Items => _items;
    public IReadOnlyList<TranslationImportProblem> Problems => _problems;
    public bool OverwriteExisting { get; }
    public string SnapshotFingerprint { get; }
    public string SnapshotShapeFingerprint { get; }
    public int FoundCount { get; }
    public int ChangedCount => _items.Count(item => item.Status == TranslationBatchImportStatus.Change);
    public int MissingCount => _items.Count(item => item.Status == TranslationBatchImportStatus.Missing);
    public int UnchangedCount => _items.Count(item => item.Status == TranslationBatchImportStatus.Unchanged);
    public int SuspiciousCount => _items.Count(item => item.Status == TranslationBatchImportStatus.SuspiciousLength ||
                                                       item.Flags.HasFlag(TranslationBatchImportFlags.SuspiciousLength) ||
                                                       item.Flags.HasFlag(TranslationBatchImportFlags.PossiblyUntranslated));
    public int SkippedCount => _items.Count(item => item.Status is TranslationBatchImportStatus.Unchanged or TranslationBatchImportStatus.SkippedExisting);
    public int RejectedCount => _items.Count(item => item.Status == TranslationBatchImportStatus.Rejected) +
                                _problems.Count(problem => problem.Severity != TranslationImportProblemSeverity.Warning);
    public int AliasCount => _items.Where(item => item.Status != TranslationBatchImportStatus.Rejected).Sum(item => item.AliasCount);
    public bool HasFatalProblems => _problems.Any(problem => problem.Severity == TranslationImportProblemSeverity.Fatal);
    public bool CanApply => ChangedCount > 0 && !HasFatalProblems;
}

public sealed record TranslationCorpusMetrics(
    int TotalKeys,
    int TotalPhysicalLines,
    int VisibleKeys,
    int VisibleLines,
    int NonEmptyVisibleLines,
    int FilledVisibleLines,
    int ExcludedEmptyLines,
    int ExcludedTechnicalLines,
    int ExcludedOtherLines,
    int TranslatedLines,
    int MissingLines,
    int ChangedLines,
    int UniqueExportLines,
    int DeduplicatedAliasLines)
{
    public bool HasConsistentPhysicalLineTotals =>
        TotalPhysicalLines == VisibleLines + ExcludedEmptyLines + ExcludedTechnicalLines + ExcludedOtherLines;
}
