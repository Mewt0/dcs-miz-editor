using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Data;
using MizEdit.Core;

namespace MizEdit.Views;

public partial class SaveAnalysisWindow : Window
{
    private readonly bool _russian;
    private readonly SaveAnalysisViewModel _viewModel;

    public SaveAnalysisWindow(SaveAnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        InitializeComponent();
        _russian = UILocalization.GetCurrentLanguage().Equals("RU", StringComparison.OrdinalIgnoreCase);
        _viewModel = new SaveAnalysisViewModel(report, _russian);
        DataContext = _viewModel;

        ApplyLocalization(report);
        InitializeSeverityFilter();
        UpdateCriticalState();
        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>
    /// True only after the user explicitly confirms saving. A false/null result is always cancellation.
    /// </summary>
    public bool SaveConfirmed => DialogResult == true;

    private void ApplyLocalization(SaveAnalysisReport report)
    {
        Title = T("Analyze .miz changes", "Анализ сохранения .miz");
        AutomationProperties.SetName(this, Title);
        TitleText.Text = T("Review changes before saving", "Проверьте изменения перед сохранением");
        SubtitleText.Text = T(
            "The mission file will not change until you confirm saving.",
            "Файл миссии не будет изменён, пока вы не подтвердите сохранение.");

        ChangedKeysText.Text = T(
            $"Changed DictKeys: {report.ChangedTranslations.Length}",
            $"Изменено DictKey: {report.ChangedTranslations.Length}");
        ModifiedFilesText.Text = T(
            $"Modified files: {report.ModifiedFileCount}",
            $"Заменено файлов: {report.ModifiedFileCount}");
        AddedFilesText.Text = T($"Added: {report.AddedFileCount}", $"Добавлено: {report.AddedFileCount}");
        RemovedFilesText.Text = T($"Removed: {report.RemovedFileCount}", $"Удалено: {report.RemovedFileCount}");
        IssuesText.Text = T(
            $"Issues: {report.QualityIssues.Length} (errors: {report.ErrorCount})",
            $"Замечаний: {report.QualityIssues.Length} (ошибок: {report.ErrorCount})");
        AutomationProperties.SetName(SummaryPanel,
            string.Join("; ", ChangedKeysText.Text, ModifiedFilesText.Text, AddedFilesText.Text,
                RemovedFilesText.Text, IssuesText.Text));

        SeverityLabel.Text = T("Show issues:", "Показывать замечания:");
        AutomationProperties.SetName(SeverityFilter, SeverityLabel.Text);
        TranslationsTab.Header = T("Translations", "Переводы");
        ArchiveTab.Header = T("Archive files", "Файлы архива");
        QualityTab.Header = T("Quality checks", "Проверка качества");
        OriginalColumn.Header = T("Original", "Оригинал");
        CurrentColumn.Header = T("Translation", "Перевод");
        PathColumn.Header = T("Archive path", "Путь в архиве");
        FileChangeColumn.Header = T("Change", "Изменение");
        FileDetailsColumn.Header = T("Details", "Сведения");
        SeverityColumn.Header = T("Severity", "Важность");
        MessageColumn.Header = T("Issue", "Проблема");
        QualitySourceColumn.Header = T("Original", "Оригинал");
        QualityTranslationColumn.Header = T("Translation", "Перевод");

        ConfirmCriticalCheckBox.Content = T(
            "I understand the risks and want to save despite critical errors",
            "Я понимаю риски и хочу сохранить несмотря на критические ошибки");
        AutomationProperties.SetName(ConfirmCriticalCheckBox,
            ConfirmCriticalCheckBox.Content.ToString() ?? string.Empty);
        SafetyText.Text = report.HasBlockingIssues
            ? T(
                "Saving is blocked until you explicitly acknowledge the critical errors.",
                "Для сохранения необходимо явно подтвердить критические ошибки.")
            : T(
                "Cancel leaves the original mission file unchanged.",
                "Отмена оставит исходный файл миссии без изменений.");
        CancelButton.Content = T("Cancel", "Отмена");
        SaveButton.Content = T("Save", "Сохранить");
        AutomationProperties.SetName(CancelButton, CancelButton.Content.ToString() ?? string.Empty);
        AutomationProperties.SetName(SaveButton, SaveButton.Content.ToString() ?? string.Empty);

        var missionName = Path.GetFileName(report.SourceMizPath);
        if (!string.IsNullOrWhiteSpace(missionName))
            SubtitleText.Text += T($" Mission: {missionName}.", $" Миссия: {missionName}.");
    }

    private void InitializeSeverityFilter()
    {
        SeverityFilter.ItemsSource = new[]
        {
            new SeverityFilterOption(T("All issues", "Все замечания"), MinimumSeverity.All),
            new SeverityFilterOption(T("Warnings and errors", "Предупреждения и ошибки"), MinimumSeverity.Warning),
            new SeverityFilterOption(T("Errors only", "Только ошибки"), MinimumSeverity.Error)
        };
        SeverityFilter.DisplayMemberPath = nameof(SeverityFilterOption.DisplayName);
        SeverityFilter.SelectedIndex = 0;
    }

    private void SeverityFilter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SeverityFilter.SelectedItem is not SeverityFilterOption option)
            return;

        _viewModel.SetMinimumSeverity(option.MinimumSeverity);
        LiveStatusText.Text = T(
            $"Shown: {_viewModel.FilteredQualityRows.Cast<object>().Count()} of {_viewModel.QualityIssueCount}",
            $"Показано: {_viewModel.FilteredQualityRows.Cast<object>().Count()} из {_viewModel.QualityIssueCount}");
        AutomationProperties.SetName(LiveStatusText, LiveStatusText.Text);
    }

    private void ConfirmCritical_Changed(object sender, RoutedEventArgs e) => UpdateCriticalState();

    private void UpdateCriticalState()
    {
        ConfirmCriticalCheckBox.Visibility = _viewModel.HasBlockingIssues
            ? Visibility.Visible
            : Visibility.Collapsed;
        SaveButton.IsEnabled = !_viewModel.HasBlockingIssues || ConfirmCriticalCheckBox.IsChecked == true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled)
            return;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private string T(string english, string russian) => _russian ? russian : english;

    private enum MinimumSeverity
    {
        All,
        Warning,
        Error
    }

    private sealed record SeverityFilterOption(string DisplayName, MinimumSeverity MinimumSeverity);

    private sealed class SaveAnalysisViewModel
    {
        private readonly ICollectionView _qualityView;
        private MinimumSeverity _minimumSeverity;

        public SaveAnalysisViewModel(SaveAnalysisReport report, bool russian)
        {
            TranslationRows = report.ChangedTranslations
                .Select(item => new TranslationRow(item.DictKey, item.SourceText, item.CurrentTranslation))
                .ToArray();
            ArchiveRows = report.ArchiveChanges
                .Select(item => ArchiveRow.Create(item, russian))
                .ToArray();
            QualityRows = report.QualityIssues
                .Select(item => QualityRow.Create(item, russian))
                .ToArray();
            _qualityView = CollectionViewSource.GetDefaultView(QualityRows);
            _qualityView.Filter = IncludeQualityRow;
            HasBlockingIssues = report.HasBlockingIssues;
        }

        public IReadOnlyList<TranslationRow> TranslationRows { get; }
        public IReadOnlyList<ArchiveRow> ArchiveRows { get; }
        public IReadOnlyList<QualityRow> QualityRows { get; }
        public ICollectionView FilteredQualityRows => _qualityView;
        public int QualityIssueCount => QualityRows.Count;
        public bool HasBlockingIssues { get; }

        public void SetMinimumSeverity(MinimumSeverity minimumSeverity)
        {
            _minimumSeverity = minimumSeverity;
            _qualityView.Refresh();
        }

        private bool IncludeQualityRow(object candidate)
        {
            if (candidate is not QualityRow row)
                return false;

            return _minimumSeverity switch
            {
                MinimumSeverity.Error => row.Severity == TranslationQualitySeverity.Error,
                MinimumSeverity.Warning => row.Severity is TranslationQualitySeverity.Warning or TranslationQualitySeverity.Error,
                _ => true
            };
        }
    }

    private sealed record TranslationRow(string DictKey, string SourceText, string CurrentText);

    private sealed record ArchiveRow(string Path, string ChangeText, string Details)
    {
        public static ArchiveRow Create(ArchiveFileChange change, bool russian)
        {
            var changeText = change.Kind switch
            {
                ArchiveChangeKind.Added => russian ? "Добавлен" : "Added",
                ArchiveChangeKind.Removed => russian ? "Удалён" : "Removed",
                ArchiveChangeKind.Modified => russian ? "Заменён" : "Modified",
                _ => change.Kind.ToString()
            };
            var oldSize = FormatSize(change.OriginalSize, russian ? "нет" : "none");
            var newSize = FormatSize(change.CurrentSize, russian ? "нет" : "none");
            var details = russian ? $"Размер: {oldSize} → {newSize}" : $"Size: {oldSize} → {newSize}";
            return new ArchiveRow(change.RelativePath, changeText, details);
        }

        private static string FormatSize(long? bytes, string none)
            => bytes.HasValue ? $"{bytes.Value:N0} B" : none;
    }

    private sealed record QualityRow(
        string DictKey,
        TranslationQualitySeverity Severity,
        string SeverityText,
        string Message,
        string SourceText,
        string TranslationText)
    {
        public static QualityRow Create(TranslationQualityIssue issue, bool russian)
        {
            var severityText = issue.Severity switch
            {
                TranslationQualitySeverity.Error => russian ? "Ошибка" : "Error",
                TranslationQualitySeverity.Warning => russian ? "Предупреждение" : "Warning",
                _ => russian ? "Сведения" : "Info"
            };
            var message = russian ? issue.Message : EnglishMessage(issue.Kind);
            return new QualityRow(issue.DictKey, issue.Severity, severityText, message,
                issue.SourceText, issue.Translation);
        }

        private static string EnglishMessage(TranslationQualityIssueKind kind)
            => kind switch
            {
                TranslationQualityIssueKind.PlaceholderMismatch => "The placeholder set (%s, %d, {0}) differs from the original.",
                TranslationQualityIssueKind.LineBreakMismatch => "The number of line breaks differs from the original.",
                TranslationQualityIssueKind.FrequencyMismatch => "A DCS radio frequency changed or is missing.",
                TranslationQualityIssueKind.CoordinateMismatch => "Coordinates changed or are missing.",
                TranslationQualityIssueKind.ProtectedTokenMismatch => "A resource name, URL, or protected token changed.",
                TranslationQualityIssueKind.EnglishLikeRemainder => "The translation appears to contain the English original.",
                TranslationQualityIssueKind.ExtremeLengthRatio => "The translation length differs unusually from the original.",
                TranslationQualityIssueKind.InconsistentTranslation => "Identical originals have different translations.",
                TranslationQualityIssueKind.TechnicalSourceModified => "A technical or Lua string was modified.",
                TranslationQualityIssueKind.UnexpectedArchiveChange => "An archive file will be removed or a suspicious file will be added. Verify that this is intentional.",
                _ => "A translation quality issue was detected."
            };
    }
}
