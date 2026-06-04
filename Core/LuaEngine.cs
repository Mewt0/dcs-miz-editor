using MizEdit.Core;

namespace MizEdit.Core;

public sealed class LuaEngine
{
    public MissionLua LoadMission(string missionPath)
    {
        var mission = new MissionLua();
        mission.LoadFromMissionFile(missionPath);
        return mission;
    }

    public void SaveMission(MissionLua mission, string missionPath)
    {
        mission.SaveToMissionFile(missionPath);
    }
}
