using System;
using System.IO;
using System.IO.Compression;

namespace MizEdit.Core;

public sealed class MizArchive : IDisposable
{
    private const int BackupsToKeep = 10;

    public string SourceMizPath { get; }
    public string WorkDir { get; }
    public string? LastBackupPath { get; private set; }

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
        if (string.IsNullOrWhiteSpace(outMizPath))
            throw new ArgumentException(UserMessages.Get("MizOutputPathEmpty"), nameof(outMizPath));

        var targetPath = Path.GetFullPath(outMizPath);
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException(UserMessages.Get("MizDestinationUnknown"));
        Directory.CreateDirectory(targetDirectory);

        var temporaryPath = Path.Combine(
            targetDirectory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        LastBackupPath = null;
        try
        {
            ZipFile.CreateFromDirectory(WorkDir, temporaryPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            ValidateArchive(temporaryPath);

            if (File.Exists(targetPath))
            {
                LastBackupPath = CreateBackup(targetPath);
                File.Replace(temporaryPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, targetPath);
            }
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    private static void ValidateArchive(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var missionEntry = archive.GetEntry("mission")
            ?? throw new InvalidDataException(UserMessages.Get("MizMissionEntryMissing"));

        using var missionStream = missionEntry.Open();
        _ = missionStream.ReadByte();
    }

    private static string CreateBackup(string targetPath)
    {
        var targetDirectory = Path.GetDirectoryName(targetPath)!;
        var backupDirectory = Path.Combine(targetDirectory, "mizedit-backups");
        Directory.CreateDirectory(backupDirectory);

        var stem = Path.GetFileNameWithoutExtension(targetPath);
        var extension = Path.GetExtension(targetPath);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var backupPath = Path.Combine(backupDirectory, $"{stem}.{timestamp}{extension}");
        File.Copy(targetPath, backupPath, overwrite: false);

        foreach (var oldBackup in Directory.EnumerateFiles(backupDirectory, $"{stem}.*{extension}")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(BackupsToKeep))
        {
            TryDeleteFile(oldBackup);
        }

        return backupPath;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Cleanup must never hide the original save error.
        }
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
