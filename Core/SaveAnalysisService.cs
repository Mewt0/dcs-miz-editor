using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MizEdit.Core;

/// <summary>
/// Builds a read-only preview of archive and translation changes before a .miz save.
/// </summary>
public sealed partial class SaveAnalysisService
{
    private const int HashBufferSize = 128 * 1024;

    public SaveAnalysisReport Analyze(
        string sourceMizPath,
        string workDir,
        IEnumerable<TranslationEntry> translationEntries,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? baselineTranslations = null)
    {
        ArgumentNullException.ThrowIfNull(translationEntries);
        var snapshots = translationEntries.Select(TranslationSaveSnapshot.FromEntry).ToImmutableArray();
        return Analyze(sourceMizPath, workDir, snapshots, cancellationToken, baselineTranslations);
    }

    public SaveAnalysisReport Analyze(
        string sourceMizPath,
        string workDir,
        IEnumerable<TranslationSaveSnapshot> translationEntries,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? baselineTranslations = null)
    {
        ArgumentNullException.ThrowIfNull(translationEntries);
        ValidatePaths(sourceMizPath, workDir);

        var entries = translationEntries.ToImmutableArray();
        cancellationToken.ThrowIfCancellationRequested();
        var archiveChanges = AnalyzeArchive(sourceMizPath, workDir, cancellationToken);
        var changed = SelectChanged(entries, baselineTranslations, cancellationToken);
        var issues = AnalyzeTranslations(entries, changed, cancellationToken)
            .AddRange(AnalyzeUnexpectedArchiveChanges(archiveChanges, cancellationToken));

        return new SaveAnalysisReport(
            Path.GetFullPath(sourceMizPath),
            Path.GetFullPath(workDir),
            archiveChanges,
            changed,
            issues,
            DateTimeOffset.UtcNow);
    }

    public Task<SaveAnalysisReport> AnalyzeAsync(
        string sourceMizPath,
        string workDir,
        IEnumerable<TranslationEntry> translationEntries,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? baselineTranslations = null)
    {
        ArgumentNullException.ThrowIfNull(translationEntries);
        var snapshots = translationEntries.Select(TranslationSaveSnapshot.FromEntry).ToImmutableArray();
        return AnalyzeAsync(sourceMizPath, workDir, snapshots, cancellationToken, baselineTranslations);
    }

    public Task<SaveAnalysisReport> AnalyzeAsync(
        string sourceMizPath,
        string workDir,
        IEnumerable<TranslationSaveSnapshot> translationEntries,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? baselineTranslations = null)
    {
        ArgumentNullException.ThrowIfNull(translationEntries);
        var snapshots = translationEntries.ToImmutableArray();
        var baseline = baselineTranslations is null
            ? null
            : baselineTranslations.ToImmutableDictionary(StringComparer.Ordinal);
        return Task.Run(
            () => Analyze(sourceMizPath, workDir, snapshots, cancellationToken, baseline),
            cancellationToken);
    }

    private static void ValidatePaths(string sourceMizPath, string workDir)
    {
        if (string.IsNullOrWhiteSpace(sourceMizPath))
            throw new ArgumentException("Source .miz path is required.", nameof(sourceMizPath));
        if (!File.Exists(sourceMizPath))
            throw new FileNotFoundException("Source .miz archive was not found.", sourceMizPath);
        if (string.IsNullOrWhiteSpace(workDir))
            throw new ArgumentException("Working directory is required.", nameof(workDir));
        if (!Directory.Exists(workDir))
            throw new DirectoryNotFoundException($"Working directory was not found: {workDir}");
    }

    private static ImmutableArray<ArchiveFileChange> AnalyzeArchive(
        string sourceMizPath,
        string workDir,
        CancellationToken cancellationToken)
    {
        var source = ReadArchiveSnapshot(sourceMizPath, cancellationToken);
        var current = ReadDirectorySnapshot(workDir, cancellationToken);
        var paths = source.Keys.Concat(current.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        var changes = ImmutableArray.CreateBuilder<ArchiveFileChange>();

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasSource = source.TryGetValue(path, out var oldFile);
            var hasCurrent = current.TryGetValue(path, out var newFile);
            if (!hasSource)
            {
                changes.Add(new(path, ArchiveChangeKind.Added, null, newFile!.Size, null, newFile.Sha256));
            }
            else if (!hasCurrent)
            {
                changes.Add(new(path, ArchiveChangeKind.Removed, oldFile!.Size, null, oldFile.Sha256, null));
            }
            else if (oldFile!.Size != newFile!.Size ||
                     !string.Equals(oldFile.Sha256, newFile.Sha256, StringComparison.Ordinal))
            {
                changes.Add(new(
                    path,
                    ArchiveChangeKind.Modified,
                    oldFile.Size,
                    newFile.Size,
                    oldFile.Sha256,
                    newFile.Sha256));
            }
        }

        return changes.ToImmutable();
    }

    private static Dictionary<string, FileFingerprint> ReadArchiveSnapshot(
        string sourceMizPath,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        using var file = new FileStream(sourceMizPath, FileMode.Open, FileAccess.Read, FileShare.Read, HashBufferSize, FileOptions.SequentialScan);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var path = NormalizeRelativePath(entry.FullName);
            if (path.Length == 0)
                continue;
            if (result.ContainsKey(path))
                throw new InvalidDataException($"The source archive contains duplicate path '{path}'.");

            using var stream = entry.Open();
            result.Add(path, new FileFingerprint(entry.Length, ComputeSha256(stream, cancellationToken)));
        }

        return result;
    }

    private static Dictionary<string, FileFingerprint> ReadDirectorySnapshot(
        string workDir,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workDir);
        var result = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        foreach (var filePath in Directory.EnumerateFiles(root, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = NormalizeRelativePath(Path.GetRelativePath(root, filePath));
            var info = new FileInfo(filePath);
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, HashBufferSize, FileOptions.SequentialScan);
            result.Add(path, new FileFingerprint(info.Length, ComputeSha256(stream, cancellationToken)));
        }

        return result;
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".."))
            throw new InvalidDataException($"Unsafe relative archive path '{path}'.");
        return string.Join('/', parts);
    }

    private static string ComputeSha256(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[HashBufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static ImmutableArray<ChangedTranslation> SelectChanged(
        ImmutableArray<TranslationSaveSnapshot> entries,
        IReadOnlyDictionary<string, string>? baselineTranslations,
        CancellationToken cancellationToken)
    {
        var result = ImmutableArray.CreateBuilder<ChangedTranslation>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var changed = baselineTranslations is null
                ? entry.IsDirty
                : !baselineTranslations.TryGetValue(entry.DictKey, out var baseline) ||
                  !string.Equals(baseline ?? string.Empty, entry.Translation ?? string.Empty, StringComparison.Ordinal);
            if (changed)
                result.Add(new(entry.DictKey, entry.SourceText ?? string.Empty, entry.Translation ?? string.Empty));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<TranslationQualityIssue> AnalyzeTranslations(
        ImmutableArray<TranslationSaveSnapshot> allEntries,
        ImmutableArray<ChangedTranslation> changedEntries,
        CancellationToken cancellationToken)
    {
        var issues = ImmutableArray.CreateBuilder<TranslationQualityIssue>();
        var changedKeys = changedEntries.Select(item => item.DictKey).ToHashSet(StringComparer.Ordinal);

        foreach (var entry in changedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnalyzeTranslation(entry, issues);
        }

        foreach (var group in allEntries
                     .Where(item => !string.IsNullOrWhiteSpace(item.SourceText) && !IsTechnical(item.SourceText))
                     .GroupBy(item => item.SourceText, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Count() < 2)
                continue;
            var translations = group
                .Select(item => item.Translation?.Trim() ?? string.Empty)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            if (translations.Length < 2)
                continue;

            foreach (var entry in group.Where(item => changedKeys.Contains(item.DictKey)))
            {
                AddIssue(
                    issues,
                    entry.DictKey,
                    TranslationQualityIssueKind.InconsistentTranslation,
                    TranslationQualitySeverity.Warning,
                    "Одинаковый оригинал имеет разные варианты перевода.",
                    entry.SourceText,
                    entry.Translation);
            }
        }

        return issues
            .DistinctBy(issue => (issue.DictKey, issue.Kind))
            .OrderByDescending(issue => issue.Severity)
            .ThenBy(issue => issue.DictKey, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static ImmutableArray<TranslationQualityIssue> AnalyzeUnexpectedArchiveChanges(
        ImmutableArray<ArchiveFileChange> changes,
        CancellationToken cancellationToken)
    {
        var issues = ImmutableArray.CreateBuilder<TranslationQualityIssue>();
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var suspiciousAddedFile = change.Kind == ArchiveChangeKind.Added &&
                                      (change.RelativePath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                                       change.RelativePath.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
                                       change.RelativePath.EndsWith(".orig", StringComparison.OrdinalIgnoreCase) ||
                                       change.RelativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                       change.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                                       change.RelativePath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ||
                                       change.RelativePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
                                       change.RelativePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
            if (change.Kind != ArchiveChangeKind.Removed && !suspiciousAddedFile)
                continue;

            var message = change.Kind == ArchiveChangeKind.Removed
                ? "Файл будет удалён из архива миссии. Проверьте, что это ожидаемое изменение."
                : "В архив добавляется служебный или исполняемый файл. Проверьте, что он действительно нужен миссии.";
            AddIssue(
                issues,
                change.RelativePath,
                TranslationQualityIssueKind.UnexpectedArchiveChange,
                TranslationQualitySeverity.Warning,
                message,
                change.Kind.ToString(),
                change.RelativePath);
        }
        return issues.ToImmutable();
    }

    private static void AnalyzeTranslation(
        ChangedTranslation entry,
        ImmutableArray<TranslationQualityIssue>.Builder issues)
    {
        var source = entry.SourceText ?? string.Empty;
        var translation = entry.CurrentTranslation ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source))
            return;

        if (IsTechnical(source))
        {
            if (!string.IsNullOrWhiteSpace(translation) &&
                !string.Equals(source, translation, StringComparison.Ordinal))
            {
                AddIssue(issues, entry.DictKey, TranslationQualityIssueKind.TechnicalSourceModified,
                    TranslationQualitySeverity.Error, "Lua/техническая строка изменена. Оставьте пустой перевод или точную копию оригинала.", source, translation);
            }
            return;
        }

        CompareTokens(PlaceholderRegex(), source, translation, entry, issues,
            TranslationQualityIssueKind.PlaceholderMismatch, TranslationQualitySeverity.Error,
            "Набор плейсхолдеров (%s, %d, {0}) не совпадает с оригиналом.");
        CompareTokens(LineBreakRegex(), source, translation, entry, issues,
            TranslationQualityIssueKind.LineBreakMismatch, TranslationQualitySeverity.Error,
            "Количество переводов строк (\\n или реальные переносы) не совпадает.");
        CompareTokens(FrequencyRegex(), source, translation, entry, issues,
            TranslationQualityIssueKind.FrequencyMismatch, TranslationQualitySeverity.Warning,
            "Частота DCS изменилась или потерялась.", NormalizeFrequencyToken);
        CompareTokens(CoordinateRegex(), source, translation, entry, issues,
            TranslationQualityIssueKind.CoordinateMismatch, TranslationQualitySeverity.Error,
            "Координаты изменились или потерялись.", NormalizeNumericToken);
        CompareTokens(ProtectedTokenRegex(), source, translation, entry, issues,
            TranslationQualityIssueKind.ProtectedTokenMismatch, TranslationQualitySeverity.Error,
            "Имя ресурса, URL или программный токен изменён.");

        var sourceLength = CountMeaningfulCharacters(source);
        var translationLength = CountMeaningfulCharacters(translation);
        if (sourceLength >= 20 && translationLength > 0)
        {
            var ratio = translationLength / (double)sourceLength;
            if (ratio < 0.20 || ratio > 4.50)
            {
                AddIssue(issues, entry.DictKey, TranslationQualityIssueKind.ExtremeLengthRatio,
                    TranslationQualitySeverity.Warning,
                    $"Подозрительное соотношение длины перевода к оригиналу: {ratio:0.00}.", source, translation);
            }
        }

        if (LooksEnglishLikeLeftover(source, translation))
        {
            AddIssue(issues, entry.DictKey, TranslationQualityIssueKind.EnglishLikeRemainder,
                TranslationQualitySeverity.Warning, "Перевод похож на оставшийся английский оригинал.", source, translation);
        }
    }

    private static void CompareTokens(
        Regex regex,
        string source,
        string translation,
        ChangedTranslation entry,
        ImmutableArray<TranslationQualityIssue>.Builder issues,
        TranslationQualityIssueKind kind,
        TranslationQualitySeverity severity,
        string message,
        Func<string, string>? normalize = null)
    {
        normalize ??= static token => token;
        var sourceTokens = regex.Matches(source).Select(match => normalize(match.Value)).Order(StringComparer.Ordinal).ToArray();
        var targetTokens = regex.Matches(translation).Select(match => normalize(match.Value)).Order(StringComparer.Ordinal).ToArray();
        if (!sourceTokens.SequenceEqual(targetTokens, StringComparer.Ordinal))
            AddIssue(issues, entry.DictKey, kind, severity, message, source, translation);
    }

    private static bool IsTechnical(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;
        var trimmed = source.Trim();
        if (ResourceStatusRegex().IsMatch(trimmed) || FrameworkStatusRegex().IsMatch(trimmed))
            return true;
        if (AllCapsIdentifierRegex().IsMatch(trimmed))
            return true;
        var hasCall = source.Contains('(') && source.Contains(')') && (LuaApiRegex().IsMatch(source) || GenericCodeCallRegex().IsMatch(trimmed));
        if (LuaLogicLineRegex().IsMatch(trimmed))
            return true;
        if ((trimmed.StartsWith("if ", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("elseif ", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("while ", StringComparison.OrdinalIgnoreCase)) &&
            LuaComparisonOrBooleanRegex().IsMatch(trimmed))
            return true;
        var hasScriptBlock = (source.Contains('\n') || source.Contains('\r')) &&
                              source.Contains('=') && LuaControlFlowRegex().IsMatch(source);
        return hasCall || hasScriptBlock;
    }

    private static bool LooksEnglishLikeLeftover(string source, string translation)
    {
        if (string.IsNullOrWhiteSpace(translation) || !EnglishWordRegex().IsMatch(source))
            return false;
        var sourceLetters = source.Count(char.IsLetter);
        if (sourceLetters < 16)
            return false;
        if (string.Equals(CollapseWhitespace(source), CollapseWhitespace(translation), StringComparison.OrdinalIgnoreCase))
            return true;
        if (CyrillicRegex().IsMatch(translation))
            return false;
        var words = EnglishWordRegex().Matches(translation);
        return words.Count >= 4 && words.Sum(match => match.Length) >= 18;
    }

    private static string CollapseWhitespace(string value) => WhitespaceRegex().Replace(value.Trim(), " ");
    private static int CountMeaningfulCharacters(string value) => value.Count(character => !char.IsWhiteSpace(character));
    private static string NormalizeNumericToken(string token) => WhitespaceRegex().Replace(token, string.Empty).Replace(',', '.').ToUpperInvariant();
    private static string NormalizeFrequencyToken(string token)
    {
        var numeric = DecimalNumberRegex().Match(token).Value;
        return numeric.Replace(',', '.');
    }

    private static void AddIssue(
        ImmutableArray<TranslationQualityIssue>.Builder issues,
        string key,
        TranslationQualityIssueKind kind,
        TranslationQualitySeverity severity,
        string message,
        string source,
        string translation)
        => issues.Add(new(key, kind, severity, message, source, translation));

    private sealed record FileFingerprint(long Size, string Sha256);

    [GeneratedRegex(@"%(?!%)(?:[1-9]\d*\$)?[-+#0 ']*\d*(?:\.\d+)?[bcdeEfgGiosuxXpn]|\{\d+(?:,[^}:{}]+)?(?::[^{}]+)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\\[nr]|\r\n|\r|\n", RegexOptions.CultureInvariant)]
    private static partial Regex LineBreakRegex();

    [GeneratedRegex(@"(?<!\w)\d{2,3}(?:[.,]\d{1,3})?\s*(?:MHz|МГц)(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FrequencyRegex();

    [GeneratedRegex(@"(?<!\w)(?:[NS]\s*\d{1,3}(?:[°\s]\s*\d{1,2}(?:['\s]\s*\d{1,2}(?:[.,]\d+)?""?)?)?|\d{1,3}(?:[.,]\d+)?°\s*[NS]|[EW]\s*\d{1,3}(?:[°\s]\s*\d{1,2}(?:['\s]\s*\d{1,2}(?:[.,]\d+)?""?)?)?|\d{1,3}(?:[.,]\d+)?°\s*[EW]|[A-Z]{2}\s*\d{2}\s*\d{3,5}\s*\d{3,5})(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CoordinateRegex();

    [GeneratedRegex(@"(?:https?://\S+|\b[A-Za-z0-9_./\\-]+\.(?:ogg|wav|mp3|png|jpe?g|dds|lua|miz|edm|ttf|json|xml)\b|\b(?:trigger\.action|missionCommands|coalition|Group|Unit|world|timer)\.[A-Za-z_]\w*(?=\s*\())", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProtectedTokenRegex();

    [GeneratedRegex(@"\b(?:trigger\.action|missionCommands|coalition|Group|Unit|world|timer)\.[A-Za-z_]\w*\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex LuaApiRegex();

    [GeneratedRegex(@"\b(?:function|local|if|then|elseif|else|end|for|while|repeat|until|return)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LuaControlFlowRegex();

    [GeneratedRegex(@"^\s*[\w./\\-]+\.(?:lua|miz|ogg|wav|mp3|png|jpe?g|bmp)\s+(?:loaded|loading|started|initialized|failed|error|missing|not\s+found)\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResourceStatusRegex();

    [GeneratedRegex(@"^\s*[A-Za-z][A-Za-z0-9 _.-]*\s+(?:framework|script|module|library)\s+(?:loaded|loading|started|initialized|failed|error|missing|not\s+found)\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FrameworkStatusRegex();

    [GeneratedRegex(@"^[A-Z0-9]+(?:_[A-Z0-9]+){1,}$", RegexOptions.CultureInvariant)]
    private static partial Regex AllCapsIdentifierRegex();

    [GeneratedRegex(@"^\s*(?:(?:if|elseif|while)\s+.+\s+then|else\s*$|end\s*$|return\b.+|local\s+\w+|for\s+\w+\s*=.+\s+do|(?:\w+\.)*\w+\s*=\s*.+|(?:\w+\.)*\w+\s*(?:\+=|-=)\s*.+|(?:\w+\.)*\w+\s*[+\-*/%]=?\s*\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LuaLogicLineRegex();

    [GeneratedRegex(@"(?:==|~=|<=|>=|<|>|\band\b|\bor\b|\bnot\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LuaComparisonOrBooleanRegex();

    [GeneratedRegex(@"^\s*[A-Za-z_]\w*(?:[.:][A-Za-z_]\w*)*\s*\([^)]*(?:[A-Za-z_]\w*|['""]|[-+]?\d)[^)]*\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex GenericCodeCallRegex();

    [GeneratedRegex(@"\b[A-Za-z]{3,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishWordRegex();

    [GeneratedRegex(@"[\p{IsCyrillic}]", RegexOptions.CultureInvariant)]
    private static partial Regex CyrillicRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalNumberRegex();
}
