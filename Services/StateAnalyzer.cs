using System.Collections.Generic;
using System.IO;
using MizEdit.Core;

namespace MizEdit.Services;

public sealed class StateAnalyzer
{
    private readonly MissionService _missionService = new();

    public string Analyze(string missionPath)
    {
        using var session = _missionService.LoadMission(missionPath);

        var missing = new List<string>();
        var mission = session.Mission;

        CheckEmpty(mission, "name", missing, "Name");
        CheckEmpty(mission, "descriptionText", missing, "Description");
        CheckEmpty(mission, "descriptionRedTask", missing, "RedTask");
        CheckEmpty(mission, "descriptionBlueTask", missing, "BlueTask");

        if (missing.Count == 0)
            return $"OK: {Path.GetFileName(missionPath)}";

        return $"{Path.GetFileName(missionPath)} -> missing/empty: {string.Join(", ", missing)}";
    }

    private static void CheckEmpty(MissionLua mission, string key, List<string> bucket, string label)
    {
        var val = mission.GetString(key);
        if (string.IsNullOrWhiteSpace(val))
            bucket.Add(label);
    }
}
