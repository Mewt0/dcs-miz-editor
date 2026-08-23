using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MizEdit.Core;

public enum TranslationQueueStatus
{
    Idle,
    Running,
    Cancelling,
    Completed,
    Cancelled
}

public enum TranslationErrorKind
{
    Transient,
    ProtectedTokenMismatch,
    Permanent
}

public sealed record TranslationError(string Key, string Message, TranslationErrorKind Kind);

public sealed class TranslationQueueState : INotifyPropertyChanged
{
    private int _total;
    private int _processed;
    private int _succeeded;
    private int _skipped;
    private TranslationQueueStatus _status = TranslationQueueStatus.Idle;

    public int Total { get => _total; private set => SetField(ref _total, value); }
    public int Processed { get => _processed; private set => SetField(ref _processed, value); }
    public int Succeeded { get => _succeeded; private set => SetField(ref _succeeded, value); }
    public int Skipped { get => _skipped; private set => SetField(ref _skipped, value); }
    public TranslationQueueStatus Status { get => _status; private set => SetField(ref _status, value); }
    public ObservableCollection<TranslationError> Errors { get; } = new();

    public bool IsActive => Status is TranslationQueueStatus.Running or TranslationQueueStatus.Cancelling;
    public double ProgressPercent => Total == 0 ? 0 : Processed * 100d / Total;

    public void Begin(int total, bool clearErrors = true)
    {
        Total = total;
        Processed = 0;
        Succeeded = 0;
        Skipped = 0;
        if (clearErrors)
            Errors.Clear();
        Status = TranslationQueueStatus.Running;
        NotifyCalculatedProperties();
    }

    public void ReportSuccess()
    {
        Succeeded++;
        Processed++;
        NotifyCalculatedProperties();
    }

    public void ReportSkipped()
    {
        Skipped++;
        Processed++;
        NotifyCalculatedProperties();
    }

    public void ReportError(TranslationError error)
    {
        Errors.Add(error);
        Processed++;
        NotifyCalculatedProperties();
    }

    public void MarkCancelling()
    {
        if (Status == TranslationQueueStatus.Running)
            Status = TranslationQueueStatus.Cancelling;
        NotifyCalculatedProperties();
    }

    public void MarkCancelled()
    {
        Status = TranslationQueueStatus.Cancelled;
        NotifyCalculatedProperties();
    }

    public void MarkCompleted()
    {
        Status = TranslationQueueStatus.Completed;
        NotifyCalculatedProperties();
    }

    public void Reset()
    {
        Total = 0;
        Processed = 0;
        Succeeded = 0;
        Skipped = 0;
        Errors.Clear();
        Status = TranslationQueueStatus.Idle;
        NotifyCalculatedProperties();
    }

    public void RemoveErrors(IEnumerable<string> keys)
    {
        var keySet = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = Errors.Count - 1; index >= 0; index--)
        {
            if (keySet.Contains(Errors[index].Key))
                Errors.RemoveAt(index);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void NotifyCalculatedProperties()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(ProgressPercent));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
