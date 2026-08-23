using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MizEdit.Core;

public enum SaveState
{
    Idle,
    Saving,
    Saved,
    Error
}

public sealed class SessionState : INotifyPropertyChanged
{
    private bool _isDirty;
    private SaveState _saveState = SaveState.Idle;

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

    public SaveState SaveState
    {
        get => _saveState;
        private set
        {
            if (_saveState == value)
                return;

            _saveState = value;
            OnPropertyChanged();
        }
    }

    public void MarkDirty()
    {
        if (SaveState == SaveState.Saving)
            return;

        IsDirty = true;
        SaveState = SaveState.Idle;
    }

    public void MarkSaving() => SaveState = SaveState.Saving;

    public void MarkSaved()
    {
        IsDirty = false;
        SaveState = SaveState.Saved;
    }

    public void MarkError()
    {
        IsDirty = true;
        SaveState = SaveState.Error;
    }

    public void Reset()
    {
        IsDirty = false;
        SaveState = SaveState.Idle;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
