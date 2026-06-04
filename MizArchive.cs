using System;
using System.IO;
using System.IO.Compression;

namespace MizEdit.Core;

public sealed class MizArchive : IDisposable
{
    public string SourceMizPath { get; }
    public string WorkDir { get; }

    public MizArchive(string mizPath)
    {
        SourceMizPath = mizPath ?? throw new ArgumentNullException(nameof(mizPath));
        WorkDir = Path.Combine(Path.GetTempPath(), "mizedit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(WorkDir);

        ZipFile.ExtractToDirectory(SourceMizPath, WorkDir);
    }

    public string MissionFilePath => Path.Combine(WorkDir, "mission");

    public void SaveAs(string outMizPath)
    {
        if (File.Exists(outMizPath))
            File.Delete(outMizPath);

        // ZipFile не умеет "в .miz", но .miz это zip -> просто создаём zip и даём расширение .miz
        ZipFile.CreateFromDirectory(WorkDir, outMizPath, CompressionLevel.Optimal, includeBaseDirectory: false);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(WorkDir))
                Directory.Delete(WorkDir, recursive: true);
        }
        catch { /* не критично */ }
    }
}
