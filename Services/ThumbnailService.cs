using System.Windows.Media.Imaging;

namespace MizEdit.Services;

public static class ThumbnailService
{
    private static readonly SemaphoreSlim DecodeSlots = new(2, 2);

    public static async Task<BitmapSource> LoadAsync(string path, int decodePixelWidth, CancellationToken cancellationToken)
    {
        await DecodeSlots.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run<BitmapSource>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = decodePixelWidth;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                cancellationToken.ThrowIfCancellationRequested();
                return bitmap;
            }, cancellationToken);
        }
        finally
        {
            DecodeSlots.Release();
        }
    }
}
