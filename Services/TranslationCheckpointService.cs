using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using MizEdit.Core;

namespace MizEdit.Services;

public sealed class TranslationCheckpointService
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _root;

    public TranslationCheckpointService(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MizEdit",
            "translation-checkpoints");
    }

    public async Task SaveAsync(
        string missionPath,
        string locale,
        IReadOnlyCollection<TranslationEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var translations = entries
            .Where(entry => entry.WasAiTranslated && !string.IsNullOrWhiteSpace(entry.Translation))
            .ToDictionary(entry => entry.Key, entry => entry.Translation, StringComparer.OrdinalIgnoreCase);
        if (translations.Count == 0)
            return;

        Directory.CreateDirectory(_root);
        var checkpoint = new TranslationCheckpoint(
            CurrentVersion,
            Path.GetFullPath(missionPath),
            locale,
            ComputeSourceHash(entries),
            DateTimeOffset.UtcNow,
            translations);
        var destination = GetPath(missionPath, locale);
        var temporary = destination + ".tmp";
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(checkpoint, JsonOptions),
            new UTF8Encoding(false),
            cancellationToken);
        File.Move(temporary, destination, overwrite: true);
    }

    public int RestoreMissing(
        string missionPath,
        string locale,
        IReadOnlyCollection<TranslationEntry> entries)
    {
        var path = GetPath(missionPath, locale);
        if (!File.Exists(path))
            return 0;

        try
        {
            var checkpoint = JsonSerializer.Deserialize<TranslationCheckpoint>(File.ReadAllText(path));
            if (checkpoint == null || checkpoint.Version != CurrentVersion ||
                !checkpoint.Locale.Equals(locale, StringComparison.OrdinalIgnoreCase) ||
                checkpoint.SourceHash != ComputeSourceHash(entries))
            {
                return 0;
            }

            var restored = 0;
            foreach (var entry in entries.Where(entry => entry.IsMissing))
            {
                if (!checkpoint.Translations.TryGetValue(entry.Key, out var translation))
                    continue;
                entry.ApplyAiTranslation(translation);
                entry.SetWorkStatus(TranslationWorkStatus.Completed);
                restored++;
            }
            return restored;
        }
        catch (JsonException)
        {
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public void Delete(string missionPath, string locale)
    {
        var path = GetPath(missionPath, locale);
        if (File.Exists(path))
            File.Delete(path);
    }

    public static string ComputeSourceHash(IEnumerable<TranslationEntry> entries)
    {
        var canonical = string.Join("\n", entries
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Key + "\u001f" + entry.SourceText));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private string GetPath(string missionPath, string locale)
    {
        var identity = Path.GetFullPath(missionPath).ToUpperInvariant() + "\u001f" + locale.ToUpperInvariant();
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(_root, name + ".json");
    }

    private sealed record TranslationCheckpoint(
        int Version,
        string MissionPath,
        string Locale,
        string SourceHash,
        DateTimeOffset UpdatedAt,
        Dictionary<string, string> Translations);
}
