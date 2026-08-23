using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MizEdit.Core;

namespace MizEdit.Views;

public partial class TranslationImportPreviewWindow : Window
{
    private readonly bool _russian;

    public TranslationImportPreviewWindow(TranslationBatchImportPlan plan)
    {
        InitializeComponent();
        _russian = UILocalization.GetCurrentLanguage().Equals("RU", StringComparison.OrdinalIgnoreCase);
        DataContext = new PreviewViewModel(plan, _russian);
        ApplyLocalization(plan);
        InitializeFilters();
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void ApplyLocalization(TranslationBatchImportPlan plan)
    {
        Title = T("Translation paste preview", "Предпросмотр вставки перевода");
        AutomationProperties.SetName(this, Title);
        TitleText.Text = T("Review changes before applying", "Проверьте изменения перед применением");
        SubtitleText.Text = T(
            "Existing data will not change until you confirm this operation.",
            "Существующие данные не изменятся, пока вы не подтвердите операцию.");

        FoundText.Text = T($"Found: {plan.FoundCount}", $"Найдено: {plan.FoundCount}");
        ChangedText.Text = T($"Will change: {plan.ChangedCount}", $"Изменится: {plan.ChangedCount}");
        MissingText.Text = T($"Missing: {plan.MissingCount}", $"Не найдено: {plan.MissingCount}");
        UnchangedText.Text = T($"Unchanged: {plan.UnchangedCount}", $"Без изменений: {plan.UnchangedCount}");
        SuspiciousText.Text = T($"Suspicious: {plan.SuspiciousCount}", $"Подозрительно: {plan.SuspiciousCount}");
        SkippedText.Text = T($"Skipped: {plan.SkippedCount}", $"Пропущено: {plan.SkippedCount}");
        RejectedText.Text = T($"Rejected: {plan.RejectedCount}", $"Отклонено: {plan.RejectedCount}");
        AliasesText.Text = T($"Duplicate aliases: {plan.AliasCount}", $"Дубликатов: {plan.AliasCount}");
        AutomationProperties.SetName(SummaryPanel,
            string.Join("; ", FoundText.Text, ChangedText.Text, MissingText.Text, UnchangedText.Text, SuspiciousText.Text, SkippedText.Text, RejectedText.Text, AliasesText.Text));

        KeyColumn.Header = "DictKey";
        PartColumn.Header = T("Part", "Часть");
        SourceColumn.Header = T("Original", "Оригинал");
        CurrentColumn.Header = T("Current translation", "Текущий перевод");
        ProposedColumn.Header = T("New translation", "Новый перевод");
        StatusColumn.Header = T("Status", "Статус");
        FilterLabelText.Text = T("Filter:", "Фильтр:");
        AutomationProperties.SetName(PreviewGrid, T("Translation changes", "Изменения перевода"));

        ProblemsTitleText.Text = plan.Problems.Count == 0
            ? T("No problems detected", "Проблем не обнаружено")
            : T("Warnings and errors", "Предупреждения и ошибки");
        AutomationProperties.SetName(ProblemsList, ProblemsTitleText.Text);
        SafetyText.Text = plan.HasFatalProblems
            ? T("A fatal error must be corrected before applying.", "Перед применением необходимо исправить критическую ошибку.")
            : T("Cancel leaves every translation unchanged.", "Отмена оставит все переводы без изменений.");
        ApplyButton.Content = T($"Apply {plan.ChangedCount} changes", $"Применить изменения: {plan.ChangedCount}");
        AutomationProperties.SetName(ApplyButton, ApplyButton.Content.ToString() ?? string.Empty);
        CancelButton.Content = T("Cancel", "Отмена");
        AutomationProperties.SetName(CancelButton, CancelButton.Content.ToString() ?? string.Empty);
    }

    private string T(string english, string russian) => _russian ? russian : english;

    private void InitializeFilters()
    {
        FilterCombo.Items.Clear();
        AddFilterItem(T("All", "Все"), ImportPreviewFilter.All);
        AddFilterItem(T("Changes", "Изменения"), ImportPreviewFilter.Changes);
        AddFilterItem(T("Missing", "Пропущенные"), ImportPreviewFilter.Missing);
        AddFilterItem(T("Unchanged", "Без изменений"), ImportPreviewFilter.Unchanged);
        AddFilterItem(T("Rejected", "Отклонённые"), ImportPreviewFilter.Rejected);
        AddFilterItem(T("Suspicious", "Подозрительные"), ImportPreviewFilter.Suspicious);
        FilterCombo.SelectedIndex = 0;
    }

    private void AddFilterItem(string text, ImportPreviewFilter filter)
        => FilterCombo.Items.Add(new ComboBoxItem { Content = text, Tag = filter });

    private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var view = CollectionViewSource.GetDefaultView(PreviewGrid.ItemsSource);
        if (view is null)
            return;

        var filter = FilterCombo.SelectedItem is ComboBoxItem { Tag: ImportPreviewFilter selected }
            ? selected
            : ImportPreviewFilter.All;
        view.Filter = item => item is PreviewRow row && RowMatchesFilter(row, filter);
        view.Refresh();
    }

    private static bool RowMatchesFilter(PreviewRow row, ImportPreviewFilter filter)
        => filter switch
        {
            ImportPreviewFilter.Changes => row.Status == TranslationBatchImportStatus.Change,
            ImportPreviewFilter.Missing => row.Status == TranslationBatchImportStatus.Missing,
            ImportPreviewFilter.Unchanged => row.Status == TranslationBatchImportStatus.Unchanged,
            ImportPreviewFilter.Rejected => row.Status == TranslationBatchImportStatus.Rejected,
            ImportPreviewFilter.Suspicious => row.IsSuspicious,
            _ => true
        };

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private sealed class PreviewViewModel
    {
        public PreviewViewModel(TranslationBatchImportPlan plan, bool russian)
        {
            Rows = plan.Items.Select(item => new PreviewRow(item, russian)).ToArray();
            Problems = plan.Problems.Select(problem => new ProblemRow(problem, russian)).ToArray();
            CanApply = plan.CanApply;
        }

        public IReadOnlyList<PreviewRow> Rows { get; }
        public IReadOnlyList<ProblemRow> Problems { get; }
        public bool CanApply { get; }
    }

    private sealed class PreviewRow
    {
        public PreviewRow(TranslationBatchImportItem item, bool russian)
        {
            Key = item.Id.DictKey;
            PartNumber = item.Id.PartIndex + 1;
            SourceText = item.SourceText;
            CurrentTranslation = item.CurrentTranslation;
            ProposedTranslation = item.ProposedTranslation;
            Status = item.Status;
            Flags = item.Flags;
            IsSuspicious = item.Status == TranslationBatchImportStatus.SuspiciousLength ||
                           item.Flags.HasFlag(TranslationBatchImportFlags.SuspiciousLength) ||
                           item.Flags.HasFlag(TranslationBatchImportFlags.PossiblyUntranslated);
            StatusSortKey = IsSuspicious ? "1-Suspicious" : item.Status.ToString();
            Detail = FormatDetail(item, russian);
            (StatusText, StatusBrush) = FormatStatus(item.Status, item.Flags, russian);
        }

        public string Key { get; }
        public int PartNumber { get; }
        public string SourceText { get; }
        public string CurrentTranslation { get; }
        public string ProposedTranslation { get; }
        public TranslationBatchImportStatus Status { get; }
        public TranslationBatchImportFlags Flags { get; }
        public bool IsSuspicious { get; }
        public string StatusSortKey { get; }
        public string StatusText { get; }
        public Brush StatusBrush { get; }
        public string Detail { get; }

        private static string FormatDetail(TranslationBatchImportItem item, bool russian)
        {
            var details = new List<string>();
            if (item.AliasCount > 0)
                details.Add(russian ? $"ещё совпадений: {item.AliasCount}" : $"additional matches: {item.AliasCount}");
            if (!string.IsNullOrWhiteSpace(item.Detail))
                details.Add(item.Detail);
            return string.Join("; ", details);
        }

        private static (string Text, Brush Brush) FormatStatus(TranslationBatchImportStatus status, TranslationBatchImportFlags flags, bool russian)
        {
            if (flags.HasFlag(TranslationBatchImportFlags.SuspiciousLength))
                return (russian ? "Подозрительно длинно" : "Suspicious length", Brushes.Gold);
            if (flags.HasFlag(TranslationBatchImportFlags.PossiblyUntranslated))
                return (russian ? "Возможно не переведено" : "Possibly untranslated", Brushes.Gold);

            return FormatStatus(status, russian);
        }

        private static (string Text, Brush Brush) FormatStatus(TranslationBatchImportStatus status, bool russian)
            => status switch
            {
                TranslationBatchImportStatus.Change => (russian ? "Изменится" : "Will change", Brushes.LimeGreen),
                TranslationBatchImportStatus.Unchanged => (russian ? "Без изменений" : "Unchanged", Brushes.Gray),
                TranslationBatchImportStatus.Missing => (russian ? "Не найдено" : "Missing", Brushes.Gold),
                TranslationBatchImportStatus.SkippedExisting => (russian ? "Пропущено" : "Skipped", Brushes.DarkOrange),
                TranslationBatchImportStatus.Rejected => (russian ? "Отклонено" : "Rejected", Brushes.OrangeRed),
                TranslationBatchImportStatus.SuspiciousLength => (russian ? "Подозрительно длинно" : "Suspicious length", Brushes.Gold),
                _ => (status.ToString(), Brushes.Gray)
            };
    }

    private sealed class ProblemRow
    {
        public ProblemRow(TranslationImportProblem problem, bool russian)
        {
            var severity = problem.Severity switch
            {
                TranslationImportProblemSeverity.Fatal => russian ? "Критическая ошибка" : "Fatal",
                TranslationImportProblemSeverity.Error => russian ? "Ошибка" : "Error",
                _ => russian ? "Предупреждение" : "Warning"
            };
            var message = russian ? RussianProblemMessage(problem) : problem.Message;
            DisplayText = string.IsNullOrWhiteSpace(problem.Marker)
                ? $"{severity}: {message}"
                : $"{severity} ({problem.Marker}): {message}";
            Brush = problem.Severity switch
            {
                TranslationImportProblemSeverity.Fatal => Brushes.OrangeRed,
                TranslationImportProblemSeverity.Error => Brushes.OrangeRed,
                _ => Brushes.DarkOrange
            };
        }

        public string DisplayText { get; }
        public Brush Brush { get; }

        private static string RussianProblemMessage(TranslationImportProblem problem)
            => problem.Kind switch
            {
                TranslationImportProblemKind.EmptyInput => "Буфер обмена не содержит текста перевода.",
                TranslationImportProblemKind.EmptyManifest => "Нет сохранённого пакета, с которым можно сопоставить перевод.",
                TranslationImportProblemKind.InvalidMarker => "Маркер повреждён или имеет неподдерживаемый формат.",
                TranslationImportProblemKind.UnknownMarker => "Маркер не найден в скопированном пакете.",
                TranslationImportProblemKind.InvalidPartIndex => "Указанная часть строки отсутствует.",
                TranslationImportProblemKind.DuplicateMarkerConflict => "Один маркер содержит разные варианты перевода.",
                TranslationImportProblemKind.KeyMismatch => "DictKey в ответе не совпадает с ожидаемым ключом.",
                TranslationImportProblemKind.AmbiguousPlainText => "Текст без маркеров нельзя однозначно сопоставить со строками.",
                TranslationImportProblemKind.StalePlan => "Данные изменились после анализа. Выполните вставку ещё раз.",
                TranslationImportProblemKind.MissingBatchId => "В ответе нет строки Batch; проверьте сопоставление перед применением.",
                TranslationImportProblemKind.WrongBatchId => "Ответ относится к другому пакету перевода.",
                TranslationImportProblemKind.PasteTooLarge => "Вставленный текст слишком большой; похоже, скопирован не тот блок.",
                TranslationImportProblemKind.SuspiciousLength => "Перевод подозрительно длинный.",
                TranslationImportProblemKind.PossiblyUntranslated => "Строка похожа на непереведённую.",
                _ => problem.Message
            };
    }

    private enum ImportPreviewFilter
    {
        All,
        Changes,
        Missing,
        Unchanged,
        Rejected,
        Suspicious
    }
}
