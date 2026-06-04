using System.Collections.Generic;

namespace MizEdit.Core;

public sealed class MissionModel
{
    public MissionModel(MissionLua mission, LocalizationEngine localization)
    {
        Mission = mission;
        Localization = localization;
    }

    public MissionLua Mission { get; }
    public LocalizationEngine Localization { get; }

    public string CurrentLocale { get; set; } = "DEFAULT";

    // Placeholders for future sections
    public List<string> Pictures { get; } = new();
    public List<string> Audio { get; } = new();
    public List<string> Triggers { get; } = new();
    public List<string> Radio { get; } = new();
    public List<string> TriggerPictures { get; } = new();
    public string Script { get; set; } = string.Empty;
}
