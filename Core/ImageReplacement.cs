using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MizEdit.Core;

public static class ImageReplacement
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp" };

    public static bool IsSupported(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path));

    public static void CopyNormalized(string sourcePath, string targetPath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(UserMessages.Get("ReplacementImageMissing"), sourcePath);
        if (!IsSupported(sourcePath) || !IsSupported(targetPath))
            throw new InvalidDataException(UserMessages.Get("ReplacementImageFormats"));

        var originalSize = File.Exists(targetPath) ? ReadSize(targetPath) : ((int Width, int Height)?)null;
        var source = Decode(sourcePath);
        BitmapSource output = source;

        if (originalSize is { } size &&
            (source.PixelWidth != size.Width || source.PixelHeight != size.Height))
        {
            output = new TransformedBitmap(
                source,
                new ScaleTransform(
                    size.Width / (double)source.PixelWidth,
                    size.Height / (double)source.PixelHeight));
        }

        var encoder = CreateEncoder(Path.GetExtension(targetPath));
        encoder.Frames.Add(BitmapFrame.Create(output));

        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException(UserMessages.Get("ReplacementTargetDirectoryMissing"));
        Directory.CreateDirectory(targetDirectory);
        var temporaryPath = Path.Combine(targetDirectory, $".mizedit-image-{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = File.Create(temporaryPath))
                encoder.Save(stream);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static (int Width, int Height) ReadSize(string path)
    {
        var bitmap = Decode(path);
        return (bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private static BitmapSource Decode(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static BitmapEncoder CreateEncoder(string extension)
        => extension.ToLowerInvariant() switch
        {
            ".png" => new PngBitmapEncoder(),
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
            ".bmp" => new BmpBitmapEncoder(),
            _ => throw new InvalidDataException(UserMessages.Get("UnsupportedImageFormat", extension))
        };
}
