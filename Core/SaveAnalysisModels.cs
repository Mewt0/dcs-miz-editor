using System.Collections.Immutable;
using System.Linq;

namespace MizEdit.Core;

public enum ArchiveChangeKind
{
    Added,
    Removed,
    Modified
}

public sealed record ArchiveFileChange(
    string RelativePath,
    ArchiveChangeKind Kind,
    long? OriginalSize,
    long? CurrentSize,
    string? OriginalSha256,
    string? CurrentSha256);

public sealed record ChangedTranslation(
    string DictKey,
    string SourceText,
    string CurrentTranslation);

public enum TranslationQualitySeverity
{
    Info,
    Warning,
    Error
}

public enum TranslationQualityIssueKind
{
    PlaceholderMismatch,
    LineBreakMismatch,
    FrequencyMismatch,
    CoordinateMismatch,
    ProtectedTokenMismatch,
    EnglishLikeRemainder,
    ExtremeLengthRatio,
    InconsistentTranslation,
    TechnicalSourceModified,
    UnexpectedArchiveChange
}

public sealed record TranslationQualityIssue(
    string DictKey,
    TranslationQualityIssueKind Kind,
    TranslationQualitySeverity Severity,
    string Message,
    string SourceText,
    string Translation);

public sealed record SaveAnalysisReport(
    string SourceMizPath,
    string WorkDirectory,
    ImmutableArray<ArchiveFileChange> ArchiveChanges,
    ImmutableArray<ChangedTranslation> ChangedTranslations,
    ImmutableArray<TranslationQualityIssue> QualityIssues,
    DateTimeOffset AnalyzedAtUtc)
{
    public int AddedFileCount => ArchiveChanges.Count(item => item.Kind == ArchiveChangeKind.Added);
    public int RemovedFileCount => ArchiveChanges.Count(item => item.Kind == ArchiveChangeKind.Removed);
    public int ModifiedFileCount => ArchiveChanges.Count(item => item.Kind == ArchiveChangeKind.Modified);
    public int ErrorCount => QualityIssues.Count(item => item.Severity == TranslationQualitySeverity.Error);
    public int WarningCount => QualityIssues.Count(item => item.Severity == TranslationQualitySeverity.Warning);
    public bool HasBlockingIssues => ErrorCount > 0;
}

/// <summary>
/// Immutable input for background save analysis. Use <see cref="FromEntry"/> on the UI thread.
/// </summary>
public sealed record TranslationSaveSnapshot(
    string DictKey,
    string SourceText,
    string Translation,
    bool IsDirty)
{
    public static TranslationSaveSnapshot FromEntry(TranslationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new(entry.Key, entry.SourceText, entry.Translation, entry.IsDirty);
    }
}
