using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using MizEdit.Services;

namespace MizEdit.Core;

public sealed class MediaListItem : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;

    public MediaListItem(string displayText, string fullPath, string token, string locale = "")
    {
        DisplayText = displayText;
        FullPath = fullPath;
        Token = token;
        Locale = locale;
    }

    public string DisplayText { get; }
    public string FullPath { get; }
    public string Token { get; }
    public string Locale { get; }

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (ReferenceEquals(_thumbnail, value))
                return;
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public async Task LoadThumbnailAsync(CancellationToken cancellationToken)
    {
        try
        {
            Thumbnail = await ThumbnailService.LoadAsync(FullPath, 96, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Broken source images stay visible by filename and can still be replaced/removed.
        }
    }

    public override string ToString() => DisplayText;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
