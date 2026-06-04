using System;
using MizEdit.Core;

namespace MizEdit.Services;

public sealed class MissionSession : IDisposable
{
    public MissionSession(string sourcePath, MizArchive archive, MissionLua mission, LocalizationEngine localization)
    {
        SourcePath = sourcePath;
        Archive = archive;
        Mission = mission;
        Localization = localization;
    }

    public string SourcePath { get; }
    public MizArchive Archive { get; }
    public MissionLua Mission { get; }
    public LocalizationEngine Localization { get; }

    public void Dispose()
    {
        Archive.Dispose();
    }
}
