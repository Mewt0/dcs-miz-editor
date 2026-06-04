using System;
using System.Collections.Generic;
using System.IO;

namespace MizEdit.Services;

public sealed class BatchService
{
    private readonly MissionService _missionService;

    public BatchService(MissionService missionService)
    {
        _missionService = missionService;
    }

    public IEnumerable<string> BatchImportTxt(string folder, string locale)
    {
        var results = new List<string>();
        if (!Directory.Exists(folder)) return new[] { $"Folder not found: {folder}" };

        foreach (var mizPath in Directory.GetFiles(folder, "*.miz"))
        {
            var txtPath = Path.ChangeExtension(mizPath, ".txt");
            if (!File.Exists(txtPath))
            {
                results.Add($"Skip (txt missing): {Path.GetFileName(mizPath)}");
                continue;
            }

            try
            {
                using var session = _missionService.LoadMission(mizPath);
                _missionService.ImportTxt(session, locale, txtPath);
                _missionService.Save(session);
                results.Add($"Imported + saved: {Path.GetFileName(mizPath)}");
            }
            catch (Exception ex)
            {
                results.Add($"Error {Path.GetFileName(mizPath)}: {ex.Message}");
            }
        }

        return results;
    }

    public IEnumerable<string> BatchSaveAsTxt(string folder, string locale)
    {
        var results = new List<string>();
        if (!Directory.Exists(folder)) return new[] { $"Folder not found: {folder}" };

        foreach (var mizPath in Directory.GetFiles(folder, "*.miz"))
        {
            var txtPath = Path.ChangeExtension(mizPath, ".txt");
            try
            {
                using var session = _missionService.LoadMission(mizPath);
                _missionService.ExportTxt(session, locale, txtPath);
                results.Add($"Exported: {Path.GetFileName(txtPath)}");
            }
            catch (Exception ex)
            {
                results.Add($"Error {Path.GetFileName(mizPath)}: {ex.Message}");
            }
        }

        return results;
    }

    public IEnumerable<string> BatchStateAnalyze(string folder)
    {
        var analyzer = new StateAnalyzer();
        var results = new List<string>();
        if (!Directory.Exists(folder)) return new[] { $"Folder not found: {folder}" };

        foreach (var mizPath in Directory.GetFiles(folder, "*.miz"))
        {
            try
            {
                results.Add(analyzer.Analyze(mizPath));
            }
            catch (Exception ex)
            {
                results.Add($"Error {Path.GetFileName(mizPath)}: {ex.Message}");
            }
        }

        return results;
    }
}
