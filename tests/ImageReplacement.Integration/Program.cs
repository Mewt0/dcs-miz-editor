using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MizEdit.Core;
using MizEdit.Services;

const string Locale = "DEFAULT";
const string ResourceKey = "ResKey_Action_2228";
const string ExpectedFileName = "Lesson_Plan_Lesson1b_Startup_Task01_0.png";

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: ImageReplacement.Integration <source.miz> <replacement.png> <output.miz>");
    return 2;
}

var sourcePath = Path.GetFullPath(args[0]);
var replacementPath = Path.GetFullPath(args[1]);
var outputPath = Path.GetFullPath(args[2]);

if (!File.Exists(sourcePath))
    throw new FileNotFoundException("Source mission was not found.", sourcePath);
if (!File.Exists(replacementPath))
    throw new FileNotFoundException("Replacement image was not found.", replacementPath);

var sourceArchive = SnapshotArchive(sourcePath);
var replacementSize = ReadPngSize(replacementPath);
if (replacementSize != (794, 1123))
    throw new InvalidOperationException($"Replacement must be exactly 794x1123, got {replacementSize.Width}x{replacementSize.Height}.");
ValidateAutomaticImageNormalization(replacementPath, replacementSize);

var service = new MissionService();
using (var session = service.LoadMission(sourcePath))
{
    var mapBefore = session.Localization.LoadMapResource(Locale);
    if (!mapBefore.TryGetValue(ResourceKey, out var mappedFile) || mappedFile != ExpectedFileName)
        throw new InvalidOperationException($"{ResourceKey} must map to {ExpectedFileName}, got '{mappedFile ?? "<missing>"}'.");

    var result = session.Localization.ReplaceResourceFile(Locale, ResourceKey, replacementPath);
    if (result.FileName != ExpectedFileName)
        throw new InvalidOperationException($"Replacement changed the mapped filename to '{result.FileName}'.");

    var mapAfter = session.Localization.LoadMapResource(Locale);
    AssertMapEqual(mapBefore, mapAfter);

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    session.Archive.SaveAs(outputPath);
}

var outputArchive = SnapshotArchive(outputPath);
var targetEntry = $"l10n/{Locale}/{ExpectedFileName}";
var mapEntry = $"l10n/{Locale}/mapResource";

if (!outputArchive.ContainsKey(targetEntry))
    throw new InvalidOperationException($"Saved mission does not contain {targetEntry}.");
if (sourceArchive.Count != outputArchive.Count)
    throw new InvalidOperationException($"Archive entry count changed: {sourceArchive.Count} -> {outputArchive.Count}.");

var changedEntries = new List<string>();
foreach (var (name, beforeHash) in sourceArchive)
{
    if (!outputArchive.TryGetValue(name, out var afterHash))
        throw new InvalidOperationException($"Archive entry disappeared: {name}");
    if (!beforeHash.Equals(afterHash, StringComparison.Ordinal))
        changedEntries.Add(name);
}

var unexpected = changedEntries
    .Where(name => !name.Equals(targetEntry, StringComparison.OrdinalIgnoreCase)
        && !name.Equals(mapEntry, StringComparison.OrdinalIgnoreCase))
    .ToArray();
if (unexpected.Length > 0)
    throw new InvalidOperationException("Unexpected archive changes: " + string.Join(", ", unexpected));
if (!changedEntries.Contains(targetEntry, StringComparer.OrdinalIgnoreCase))
    throw new InvalidOperationException("Target image bytes did not change.");

using (var session = service.LoadMission(outputPath))
{
    var map = session.Localization.LoadMapResource(Locale);
    if (!map.TryGetValue(ResourceKey, out var mappedFile) || mappedFile != ExpectedFileName)
        throw new InvalidOperationException("Saved mapResource lost the original resource key or filename.");

    var resolved = session.Localization.ResolveResourceFile(Locale, ResourceKey)
        ?? throw new InvalidOperationException("Saved resource cannot be resolved through LocalizationEngine.");
    var savedSize = ReadPngSize(resolved);
    if (savedSize != replacementSize)
        throw new InvalidOperationException($"Saved image dimensions changed to {savedSize.Width}x{savedSize.Height}.");
}

Console.WriteLine("PASS: one mission image was replaced through LocalizationEngine.");
Console.WriteLine($"Resource key: {ResourceKey}");
Console.WriteLine($"Mapped filename: {ExpectedFileName}");
Console.WriteLine($"PNG dimensions: {replacementSize.Width}x{replacementSize.Height}");
Console.WriteLine($"Archive entries: {sourceArchive.Count} (unchanged)");
Console.WriteLine("Changed entries: " + string.Join(", ", changedEntries));
Console.WriteLine("All mission/Lua/dictionary/audio/other image bytes: unchanged");
Console.WriteLine("PASS: different dimensions/extension are normalized while target name and size stay unchanged");
Console.WriteLine($"Output: {outputPath}");
return 0;

static Dictionary<string, string> SnapshotArchive(string path)
{
    var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    using var archive = ZipFile.OpenRead(path);
    foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
    {
        using var stream = entry.Open();
        snapshot[entry.FullName.Replace('\\', '/')] = Convert.ToHexString(SHA256.HashData(stream));
    }
    return snapshot;
}

static (int Width, int Height) ReadPngSize(string path)
{
    using var stream = File.OpenRead(path);
    var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    var frame = decoder.Frames[0];
    return (frame.PixelWidth, frame.PixelHeight);
}

static void AssertMapEqual(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
{
    if (before.Count != after.Count)
        throw new InvalidOperationException($"mapResource key count changed: {before.Count} -> {after.Count}.");

    foreach (var (key, value) in before)
    {
        if (!after.TryGetValue(key, out var afterValue) || !value.Equals(afterValue, StringComparison.Ordinal))
            throw new InvalidOperationException($"mapResource changed at key '{key}': '{value}' -> '{afterValue ?? "<missing>"}'.");
    }
}

static void ValidateAutomaticImageNormalization(string replacementPath, (int Width, int Height) expectedSize)
{
    var testRoot = Path.Combine(Path.GetTempPath(), $"mizedit-image-normalization-{Guid.NewGuid():N}");
    Directory.CreateDirectory(testRoot);
    var targetPath = Path.Combine(testRoot, ExpectedFileName);
    var mismatchedSource = Path.Combine(testRoot, "user-selected-different-name.jpg");

    try
    {
        File.Copy(replacementPath, targetPath);
        CreateScaledJpeg(replacementPath, mismatchedSource, expectedSize.Width / 2, expectedSize.Height / 2);
        ImageReplacement.CopyNormalized(mismatchedSource, targetPath);

        if (Path.GetFileName(targetPath) != ExpectedFileName)
            throw new InvalidOperationException("Automatic image normalization changed the target filename.");
        if (ReadPngSize(targetPath) != expectedSize)
            throw new InvalidOperationException("Automatic image normalization did not preserve the target dimensions.");
    }
    finally
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

static void CreateScaledJpeg(string sourcePath, string outputPath, int width, int height)
{
    using var stream = File.OpenRead(sourcePath);
    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    var source = decoder.Frames[0];
    var scaled = new TransformedBitmap(
        source,
        new ScaleTransform(width / (double)source.PixelWidth, height / (double)source.PixelHeight));
    var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
    encoder.Frames.Add(BitmapFrame.Create(scaled));
    using var output = File.Create(outputPath);
    encoder.Save(output);
}
