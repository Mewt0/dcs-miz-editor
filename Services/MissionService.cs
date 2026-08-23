using System;
using MizEdit.Core;

namespace MizEdit.Services;

public sealed class MissionService
{
    private readonly LuaEngine _luaEngine = new();

    public MissionSession LoadMission(string mizPath)
    {
        var archive = new MizArchive(mizPath);
        var mission = _luaEngine.LoadMission(archive.MissionFilePath);
        var localization = new LocalizationEngine(archive.WorkDir);
        return new MissionSession(mizPath, archive, mission, localization);
    }

    public void Save(MissionSession session)
    {
        SaveMissionIfDirty(session);
        session.Archive.SaveAs(session.SourcePath);
        session.MarkMissionSaved();
    }

    public void SaveAsMiz(MissionSession session, string outPath)
    {
        SaveMissionIfDirty(session);
        session.Archive.SaveAs(outPath);
        session.MarkMissionSaved();
    }

    public void ExportTxt(MissionSession session, string locale, string outPath)
    {
        session.Localization.ExportTxt(session.Mission, locale, outPath);
    }

    public void ImportTxt(MissionSession session, string locale, string path)
    {
        if (session.Localization.ImportTxt(session.Mission, locale, path))
            session.MarkMissionDirty();
    }

    private void SaveMissionIfDirty(MissionSession session)
    {
        if (session.IsMissionDirty)
            _luaEngine.SaveMission(session.Mission, session.Archive.MissionFilePath);
    }
}
