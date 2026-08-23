using System.IO;
using MoonSharp.Interpreter;

namespace MizEdit.Core;

public sealed record MissionProtectionCleanupFinding(
    string Path,
    string Kind,
    string ValueSummary,
    bool CanRemove);

public sealed record MissionProtectionCleanupMetrics(
    int TopLevelKeys,
    int UnitLikeTables,
    int TriggerSlots,
    int L10nFiles);

public sealed record MissionProtectionCleanupPlan(
    string SourceMizPath,
    IReadOnlyList<MissionProtectionCleanupFinding> Findings,
    MissionProtectionCleanupMetrics BeforeMetrics,
    bool HasRemovableFindings);

public sealed record MissionProtectionCleanupOptions(
    bool RemoveExtLoader = true,
    bool RemoveRootMissionIds = true,
    string? ExactMissionId = null);

public sealed record MissionProtectionCleanupResult(
    string SourceMizPath,
    string OutputMizPath,
    IReadOnlyList<string> RemovedPaths,
    MissionProtectionCleanupMetrics BeforeMetrics,
    MissionProtectionCleanupMetrics AfterMetrics);

/// <summary>
/// Detects and removes mission loader/protection metadata for user-owned missions.
/// The service deliberately redacts field values and saves to a separate .miz path.
/// It does not decrypt, patch executables, modify .pak.crypt files, or bypass DRM.
/// </summary>
public static class MissionProtectionCleanupService
{
    private static readonly string[] RootMissionIdKeys =
    [
        "miz_id",
        "mizId",
        "missionId",
        "missionID"
    ];

    public static MissionProtectionCleanupPlan Analyze(string mizPath)
    {
        using var archive = new MizArchive(mizPath);
        var mission = new MissionLua();
        mission.LoadFromMissionFile(archive.MissionFilePath);

        var findings = CollectFindings(mission.MissionTable);
        var metrics = CaptureMetrics(mission.MissionTable, archive.WorkDir);

        return new MissionProtectionCleanupPlan(
            Path.GetFullPath(mizPath),
            findings,
            metrics,
            findings.Any(f => f.CanRemove));
    }

    public static MissionProtectionCleanupResult ApplyToCopy(
        string sourceMizPath,
        string outputMizPath,
        MissionProtectionCleanupOptions? options = null)
    {
        options ??= new MissionProtectionCleanupOptions();

        var sourceFullPath = Path.GetFullPath(sourceMizPath);
        var outputFullPath = Path.GetFullPath(outputMizPath);
        if (string.Equals(sourceFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Protection cleanup must save to a separate .miz copy.");

        using var archive = new MizArchive(sourceFullPath);
        var mission = new MissionLua();
        mission.LoadFromMissionFile(archive.MissionFilePath);

        var beforeMetrics = CaptureMetrics(mission.MissionTable, archive.WorkDir);
        var removed = RemoveSelectedMetadata(mission.MissionTable, options);
        if (removed.Count == 0)
            throw new InvalidOperationException("No removable mission protection metadata was found.");

        mission.SaveToMissionFile(archive.MissionFilePath);

        var reloaded = new MissionLua();
        reloaded.LoadFromMissionFile(archive.MissionFilePath);
        var afterMetrics = CaptureMetrics(reloaded.MissionTable, archive.WorkDir);
        ValidateCleanup(beforeMetrics, afterMetrics, removed);

        archive.SaveAs(outputFullPath);

        return new MissionProtectionCleanupResult(
            sourceFullPath,
            outputFullPath,
            removed,
            beforeMetrics,
            afterMetrics);
    }

    private static List<MissionProtectionCleanupFinding> CollectFindings(Table missionTable)
    {
        var findings = new List<MissionProtectionCleanupFinding>();

        var extLoader = missionTable.Get("ext_loader");
        if (extLoader.Type == DataType.Table)
        {
            findings.Add(new MissionProtectionCleanupFinding(
                "mission.ext_loader",
                "external-loader-table",
                "<table>",
                CanRemove: true));

            AddNestedFinding(findings, extLoader.Table, "library", "mission.ext_loader.library", "loader-library");
            AddNestedFinding(findings, extLoader.Table, "miz_id", "mission.ext_loader.miz_id", "mission-id");
        }

        foreach (var key in RootMissionIdKeys)
        {
            var value = missionTable.Get(key);
            if (value.Type != DataType.Nil && value.Type != DataType.Void)
            {
                findings.Add(new MissionProtectionCleanupFinding(
                    $"mission.{key}",
                    "root-mission-id",
                    SummarizeValue(value),
                    CanRemove: true));
            }
        }

        return findings;
    }

    private static void AddNestedFinding(
        List<MissionProtectionCleanupFinding> findings,
        Table table,
        string key,
        string path,
        string kind)
    {
        var value = table.Get(key);
        if (value.Type == DataType.Nil || value.Type == DataType.Void)
            return;

        findings.Add(new MissionProtectionCleanupFinding(
            path,
            kind,
            SummarizeValue(value),
            CanRemove: false));
    }

    private static string SummarizeValue(DynValue value)
    {
        return value.Type switch
        {
            DataType.String => string.IsNullOrEmpty(value.String)
                ? "<empty string>"
                : $"<redacted string, length={value.String.Length}>",
            DataType.Number => "<number>",
            DataType.Boolean => "<boolean>",
            DataType.Table => "<table>",
            _ => $"<{value.Type.ToString().ToLowerInvariant()}>"
        };
    }

    private static List<string> RemoveSelectedMetadata(Table missionTable, MissionProtectionCleanupOptions options)
    {
        var removed = new List<string>();
        var exactMissionId = string.IsNullOrWhiteSpace(options.ExactMissionId)
            ? null
            : options.ExactMissionId.Trim();

        if (options.RemoveExtLoader && missionTable.Get("ext_loader").Type == DataType.Table)
        {
            var extLoader = missionTable.Get("ext_loader").Table;
            var nestedMissionId = extLoader.Get("miz_id");
            if (exactMissionId == null || ValueEquals(nestedMissionId, exactMissionId))
            {
                missionTable.Remove(DynValue.NewString("ext_loader"));
                removed.Add("mission.ext_loader");
            }
        }

        if (options.RemoveRootMissionIds)
        {
            foreach (var key in RootMissionIdKeys)
            {
                var value = missionTable.Get(key);
                if (value.Type == DataType.Nil || value.Type == DataType.Void)
                    continue;
                if (exactMissionId != null && !ValueEquals(value, exactMissionId))
                    continue;

                missionTable.Remove(DynValue.NewString(key));
                removed.Add($"mission.{key}");
            }
        }

        return removed;
    }

    private static bool ValueEquals(DynValue value, string expected)
    {
        return value.Type switch
        {
            DataType.String => string.Equals(value.String?.Trim(), expected, StringComparison.Ordinal),
            DataType.Number => string.Equals(value.Number.ToString("R", System.Globalization.CultureInfo.InvariantCulture), expected, StringComparison.Ordinal),
            _ => false
        };
    }

    private static MissionProtectionCleanupMetrics CaptureMetrics(Table missionTable, string workDir)
    {
        var visited = new HashSet<Table>();
        var unitLikeTables = 0;

        CountUnitLikeTables(missionTable, visited, ref unitLikeTables);

        return new MissionProtectionCleanupMetrics(
            missionTable.Pairs.Count(),
            unitLikeTables,
            CountTriggerSlots(missionTable),
            CountL10nFiles(workDir));
    }

    private static void CountUnitLikeTables(Table table, HashSet<Table> visited, ref int count)
    {
        if (!visited.Add(table))
            return;

        if (table.Get("unitId").Type == DataType.Number ||
            table.Get("groupId").Type == DataType.Number)
        {
            count++;
        }

        foreach (var pair in table.Pairs)
        {
            if (pair.Value.Type == DataType.Table)
                CountUnitLikeTables(pair.Value.Table, visited, ref count);
        }
    }

    private static int CountTriggerSlots(Table missionTable)
    {
        var trig = missionTable.Get("trig");
        if (trig.Type != DataType.Table)
            return 0;

        var indexes = new SortedSet<int>();
        foreach (var key in new[] { "conditions", "actions", "func", "funcStartup", "flag" })
        {
            var table = trig.Table.Get(key);
            if (table.Type != DataType.Table)
                continue;

            foreach (var pair in table.Table.Pairs)
            {
                if (pair.Key.Type == DataType.Number)
                    indexes.Add(Convert.ToInt32(pair.Key.Number));
            }
        }

        return indexes.Count;
    }

    private static int CountL10nFiles(string workDir)
    {
        var l10nRoot = Path.Combine(workDir, "l10n");
        return Directory.Exists(l10nRoot)
            ? Directory.EnumerateFiles(l10nRoot, "*", SearchOption.AllDirectories).Count()
            : 0;
    }

    private static void ValidateCleanup(
        MissionProtectionCleanupMetrics before,
        MissionProtectionCleanupMetrics after,
        IReadOnlyList<string> removed)
    {
        var expectedTopLevelDelta = removed.Count(p => p.Count(ch => ch == '.') == 1);
        if (after.TopLevelKeys != before.TopLevelKeys - expectedTopLevelDelta)
        {
            throw new InvalidOperationException(
                $"Unexpected mission top-level key delta. Before={before.TopLevelKeys}, After={after.TopLevelKeys}, RemovedTopLevel={expectedTopLevelDelta}.");
        }

        if (after.UnitLikeTables != before.UnitLikeTables)
            throw new InvalidOperationException("Unit/group table count changed during protection cleanup.");

        if (after.TriggerSlots != before.TriggerSlots)
            throw new InvalidOperationException("Trigger count changed during protection cleanup.");

        if (after.L10nFiles != before.L10nFiles)
            throw new InvalidOperationException("l10n file count changed during protection cleanup.");
    }
}
