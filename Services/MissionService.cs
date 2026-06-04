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
        _luaEngine.SaveMission(session.Mission, session.Archive.MissionFilePath);
        session.Archive.SaveAs(session.SourcePath);
    }

    public void SaveAsMiz(MissionSession session, string outPath)
    {
        _luaEngine.SaveMission(session.Mission, session.Archive.MissionFilePath);
        session.Archive.SaveAs(outPath);
    }

    public void ExportTxt(MissionSession session, string locale, string outPath)
    {
        session.Localization.ExportTxt(session.Mission, locale, outPath);
    }

    public void ImportTxt(MissionSession session, string locale, string path)
    {
        session.Localization.ImportTxt(session.Mission, locale, path);
    }
}
