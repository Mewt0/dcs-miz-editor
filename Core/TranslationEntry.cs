using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MizEdit.Core;

public enum TranslationWorkStatus
{
    None,
    InProgress,
    Completed,
    Error,
    Skipped
}

public sealed class TranslationEntry : INotifyPropertyChanged
{
    private string _translation;
    private bool _isDirty;
    private bool _isApplyingAiTranslation;
    private bool _wasAiTranslated;
    private TranslationWorkStatus _workStatus;
    private string _displayNumber = string.Empty;
    private readonly Stack<string> _undoHistory = new();
    private bool _isUndoing;

    public TranslationEntry(string key, string sourceText, string translation)
    {
        Key = key;
        SourceText = sourceText;
        _translation = translation;
    }

    public string Key { get; }
    public string SourceText { get; }
    public string DisplayNumber => _displayNumber;

    public string Translation
    {
        get => _translation;
        set
        {
            value ??= string.Empty;
            if (_translation == value)
                return;

            if (!_isUndoing)
            {
                if (_undoHistory.Count >= 100)
                {
                    var recent = _undoHistory.Take(99).Reverse().ToArray();
                    _undoHistory.Clear();
                    foreach (var previous in recent)
                        _undoHistory.Push(previous);
                }
                _undoHistory.Push(_translation);
                OnPropertyChanged(nameof(CanUndo));
            }
            _translation = value;
            if (!_isApplyingAiTranslation)
                WasAiTranslated = false;
            IsDirty = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMissing));
        }
    }

    public bool IsMissing => string.IsNullOrWhiteSpace(Translation);
    public bool CanUndo => _undoHistory.Count > 0;

    public bool WasAiTranslated
    {
        get => _wasAiTranslated;
        private set
        {
            if (_wasAiTranslated == value)
                return;
            _wasAiTranslated = value;
            OnPropertyChanged();
        }
    }

    public TranslationWorkStatus WorkStatus
    {
        get => _workStatus;
        private set
        {
            if (_workStatus == value)
                return;
            _workStatus = value;
            OnPropertyChanged();
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty == value)
                return;

            _isDirty = value;
            OnPropertyChanged();
        }
    }

    public void MarkSaved() => IsDirty = false;

    public void ApplyAiTranslation(string translation)
    {
        _isApplyingAiTranslation = true;
        try
        {
            Translation = translation;
            WasAiTranslated = true;
        }
        finally
        {
            _isApplyingAiTranslation = false;
        }
    }

    public void SetWorkStatus(TranslationWorkStatus status) => WorkStatus = status;

    public void SetDisplayNumber(string value)
    {
        value ??= string.Empty;
        if (_displayNumber == value)
            return;
        _displayNumber = value;
        OnPropertyChanged(nameof(DisplayNumber));
    }

    public bool Undo()
    {
        if (_undoHistory.Count == 0)
            return false;

        _isUndoing = true;
        try
        {
            _translation = _undoHistory.Pop();
            WasAiTranslated = false;
            IsDirty = true;
            OnPropertyChanged(nameof(Translation));
            OnPropertyChanged(nameof(IsMissing));
            OnPropertyChanged(nameof(CanUndo));
            return true;
        }
        finally
        {
            _isUndoing = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
