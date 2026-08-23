using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Threading;
using MizEdit.Core;

namespace MizEdit.Views;

public partial class TranslationWorkspace : UserControl
{
    private ScrollViewer? _sourceScroll;
    private ScrollViewer? _translationScroll;
    private bool _syncingScroll;
    private bool _languageSubscribed;
    private bool _runtimeTextObserversAttached;
    private bool _localizingRuntimeText;

    // Правка в TranslationBatchText не разбирается на каждое нажатие клавиши —
    // на 9500+ строк это была бы работа с огромной строкой на каждый символ.
    // Вместо этого правки debounce'ятся и раз в ~500 мс наружу летит одно событие.
    private readonly DispatcherTimer _translationEditDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(500)
    };

    // Поднимается, пока текст в TranslationBatchText меняется программно
    // (после CopyBatchRequested/PasteBatchRequested или SetTranslationBatchText) —
    // такие изменения не должны запускать TranslationBatchEdited и debounce-таймер,
    // иначе после программного заполнения тут же прилетело бы "пользователь всё поменял".
    private bool _suppressTranslationBatchEditEvents;
    private bool _suppressSourceBatchDisplayFormatting;

    public TranslationWorkspace()
    {
        InitializeComponent();
        _translationEditDebounce.Tick += TranslationEditDebounce_Tick;
        Loaded += Workspace_Loaded;
        Unloaded += Workspace_Unloaded;
        PreviewKeyDown += Workspace_PreviewKeyDown;
        ApplyLocalization();
    }

    public DataGrid GridControl => TranslationGrid;
    public TextBox SearchBox => TranslationSearchBox;
    public CheckBox OnlyMissingBox => OnlyMissingTranslationsBox;
    public CheckBox OnlyChangedBox => OnlyChangedTranslationsBox;
    public CheckBox HideTechnicalBox => HideTechnicalTranslationsBox;
    public CheckBox ActionTextBox => ActionTextKeyBox;
    public CheckBox ActionRadioTextBox => ActionRadioTextKeyBox;
    public CheckBox DescriptionBox => DescriptionKeyBox;
    public CheckBox SubtitleBox => SubtitleKeyBox;
    public CheckBox SortieBox => SortieKeyBox;
    public CheckBox NameBox => NameKeyBox;
    public CheckBox OtherKeysBox => ShowOtherKeysBox;
    public CheckBox SkipEmptyBox => SkipEmptySourceBox;
    public CheckBox IncludeInstructionOption => IncludeInstructionBox;
    public CheckBox DeduplicateOption => DeduplicateBatchBox;
    public CheckBox CompactNumberedOption => CompactNumberedBatchBox;
    public CheckBox PasteOverwriteOption => PasteOverwriteBox;
    public TextBox SourceBatchBox => SourceBatchText;
    public TextBox TranslationBatchBox => TranslationBatchText;
    public TextBox FindBox => FindTextBox;
    public TextBox ReplaceBox => ReplaceTextBox;
    public TextBlock MatchText => MatchCountText;
    public TextBlock SourceLinesText => SourceLineCountText;
    public TextBlock TranslationLinesText => TranslationLineCountText;
    public TextBlock StatsText => TranslationStatsText;
    public TextBlock ProgressText => AiProgressText;
    public ProgressBar ProgressBarControl => AiProgressBar;
    public ItemsControl ErrorsList => AiErrorsList;
    public CheckBox OverwriteExistingBox => AiOverwriteExistingBox;
    public Button TranslateSelectedButton => AiTranslateSelectedButton;
    public Button TranslateMissingButton => AiTranslateMissingButton;
    public Button RetryButton => AiRetryButton;
    public Button CancelButton => AiCancelButton;

    public event RoutedEventHandler? BackRequested;
    public event TextChangedEventHandler? SearchChanged;
    public event RoutedEventHandler? FilterChanged;
    public event RoutedEventHandler? ReloadRequested;
    public event RoutedEventHandler? CopySourceRequested;
    public event RoutedEventHandler? CopyBatchRequested;
    public event RoutedEventHandler? PasteBatchRequested;
    public event RoutedEventHandler? ClearVisibleRequested;
    public event RoutedEventHandler? FindNextRequested;
    public event RoutedEventHandler? FindPreviousRequested;
    public event RoutedEventHandler? ReplaceSelectedRequested;
    public event RoutedEventHandler? ReplaceAllRequested;
    public event RoutedEventHandler? TranslateSelectedRequested;
    public event RoutedEventHandler? TranslateMissingRequested;
    public event RoutedEventHandler? RetryRequested;
    public event RoutedEventHandler? CancelRequested;
    public event RoutedEventHandler? ApplyRequested;
    public event RoutedEventHandler? UndoSelectedRequested;

    // Новое: поднимается ~500 мс после того, как пользователь перестал печатать
    // в TranslationBatchText. Подпишите на это тот же обработчик, что уже разбирает
    // [[N]]-маркеры на PasteBatchRequested — с одним отличием: он должен читать текст
    // из TranslationBatchBox.Text (или e), а не из буфера обмена.
    public event EventHandler? TranslationBatchEdited;

    public void SetSummary(string text) => WorkspaceSummaryText.Text = LocalizeRuntimeText(text);

    public void SetRowNumbers(IReadOnlyList<TranslationBatchLine> lines)
    {
        var rowNumbers = lines
            .GroupBy(line => line.Entry)
            .ToDictionary(
                group => group.Key,
                group => group.First().Number == group.Last().Number
                    ? $"#{group.First().Number}"
                    : $"#{group.First().Number}–{group.Last().Number}");

        foreach (var item in TranslationGrid.Items.OfType<TranslationEntry>())
            item.SetDisplayNumber(rowNumbers.TryGetValue(item, out var number) ? number : string.Empty);
    }

    /// <summary>Renders corpus counts with explicit labels instead of translating fragments of a preformatted string.</summary>
    public void SetCorpusMetrics(TranslationCorpusMetrics metrics)
    {
        WorkspaceSummaryText.Text = T(
            $"Keys: {metrics.VisibleKeys}/{metrics.TotalKeys}",
            $"Ключи: {metrics.VisibleKeys}/{metrics.TotalKeys}");
        TranslationStatsText.Text = T(
            $"Physical lines: {metrics.VisibleLines}/{metrics.TotalPhysicalLines} · non-empty visible: {metrics.NonEmptyVisibleLines} · filled: {metrics.FilledVisibleLines} · translated: {metrics.TranslatedLines} · missing: {metrics.MissingLines} · changed: {metrics.ChangedLines} · excluded empty/technical/other: {metrics.ExcludedEmptyLines}/{metrics.ExcludedTechnicalLines}/{metrics.ExcludedOtherLines} · unique export: {metrics.UniqueExportLines}, duplicate aliases: {metrics.DeduplicatedAliasLines}",
            $"Физические строки: {metrics.VisibleLines}/{metrics.TotalPhysicalLines} · непустых видимых: {metrics.NonEmptyVisibleLines} · заполнено: {metrics.FilledVisibleLines} · переведено: {metrics.TranslatedLines} · отсутствует: {metrics.MissingLines} · изменено: {metrics.ChangedLines} · исключено пустых/технических/прочих: {metrics.ExcludedEmptyLines}/{metrics.ExcludedTechnicalLines}/{metrics.ExcludedOtherLines} · уникальных для экспорта: {metrics.UniqueExportLines}, дубликатов: {metrics.DeduplicatedAliasLines}");
        AutomationProperties.SetName(WorkspaceSummaryText, WorkspaceSummaryText.Text);
        AutomationProperties.SetName(TranslationStatsText, TranslationStatsText.Text);
    }

    /// <summary>
    /// Shows the non-mutating import plan and returns true only after explicit confirmation.
    /// The caller remains responsible for applying the plan and revalidating its snapshot.
    /// </summary>
    public bool ShowImportPreview(TranslationBatchImportPlan plan)
    {
        var dialog = new TranslationImportPreviewWindow(plan)
        {
            Owner = Window.GetWindow(this)
        };
        return dialog.ShowDialog() == true;
    }

    // Используйте этот метод, а не прямое присвоение TranslationBatchText.Text,
    // из внешнего кода (после CopyBatchRequested/PasteBatchRequested) — он подавляет
    // TranslationBatchEdited на время программной записи, чтобы не запустить
    // разбор текста, который мы сами только что туда положили.
    public void SetTranslationBatchText(string text)
    {
        _suppressTranslationBatchEditEvents = true;
        try
        {
            TranslationBatchText.Text = TranslationBatchDocument.FormatNumberedForDisplay(text);
        }
        finally
        {
            _suppressTranslationBatchEditEvents = false;
        }
        _translationEditDebounce.Stop();
    }

    private void SourceBatchText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSourceBatchDisplayFormatting)
            return;

        var displayText = TranslationBatchDocument.FormatNumberedForDisplay(SourceBatchText.Text);
        if (displayText == SourceBatchText.Text)
            return;

        _suppressSourceBatchDisplayFormatting = true;
        try
        {
            SourceBatchText.Text = displayText;
        }
        finally
        {
            _suppressSourceBatchDisplayFormatting = false;
        }
    }

    private void TranslationBatchText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTranslationBatchEditEvents)
            return;

        // Перезапуск таймера на каждое нажатие — событие наружу уйдёт только
        // после паузы в наборе текста, а не на каждый символ.
        _translationEditDebounce.Stop();
        _translationEditDebounce.Start();
    }

    private void TranslationEditDebounce_Tick(object? sender, EventArgs e)
    {
        _translationEditDebounce.Stop();
        TranslationBatchEdited?.Invoke(this, EventArgs.Empty);
    }

    private void Workspace_Loaded(object sender, RoutedEventArgs e)
    {
        AttachSynchronizedScrolling();
        if (!_languageSubscribed)
        {
            UILocalization.LanguageChanged += UILocalization_LanguageChanged;
            _languageSubscribed = true;
        }
        AttachRuntimeTextObservers();
        ApplyLocalization();
    }

    private void Workspace_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_languageSubscribed)
        {
            UILocalization.LanguageChanged -= UILocalization_LanguageChanged;
            _languageSubscribed = false;
        }
        DetachRuntimeTextObservers();
        _translationEditDebounce.Stop();
    }

    private void UILocalization_LanguageChanged(string language)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ApplyLocalization);
            return;
        }
        ApplyLocalization();
    }

    private void ApplyLocalization()
    {
        BackButton.Content = T("← Editor", "← В редактор");
        BackButton.ToolTip = T("Return to the mission editor", "Вернуться в редактор миссии");
        WorkspaceTitleText.Text = T("Mission translations", "Перевод текстов миссии");
        WorkspaceSubtitleText.Text = T(
            "Filter lines · copy all · translate · paste all · review",
            "Отфильтруйте строки · скопируйте всё · переведите · вставьте всё · проверьте");

        TranslationSearchBox.ToolTip = T(
            "Search DictKey, original or translation (Ctrl+F)",
            "Поиск по DictKey, оригиналу или переводу (Ctrl+F)");
        SetCheckBox(OnlyMissingTranslationsBox, "Only missing", "Только без перевода",
            "Show only lines without a translation", "Показывать только строки, для которых ещё нет перевода");
        SetCheckBox(OnlyChangedTranslationsBox, "Only changed", "Только изменённые",
            "Show only lines changed in this session", "Показывать только строки, изменённые в текущем сеансе");
        SetCheckBox(HideTechnicalTranslationsBox, "Hide Lua and code", "Скрыть Lua и код",
            "Exclude technical Lua and code blocks from copy, paste and AI translation",
            "Технические блоки Lua и код не попадут в копирование, вставку и AI-перевод");
        ReloadButton.Content = T("Reload list", "Обновить список");
        ReloadButton.ToolTip = T("Reload translation lines from the open mission", "Перечитать строки перевода из открытой миссии");
        KeyTypesText.Text = T("DictKey types:", "Типы DictKey:");
        SetCheckBox(ShowOtherKeysBox, "Other keys", "Другие ключи");
        SetCheckBox(SkipEmptySourceBox, "Skip empty lines", "Пропускать пустые строки",
            "Hide DictKey entries without source text", "Не показывать DictKey без исходного текста");
        SetCheckBox(CompactNumberedBatchBox, "Short 1» format", "Короткий формат 1»",
            "AI copy always uses one Batch header and short 1» lines instead of long M2/MZ markers",
            "Копирование для AI всегда использует одну строку Batch и короткие строки 1» вместо длинных M2/MZ-маркеров");

        SourceHeaderText.Text = T("Original · DEFAULT", "Оригинал · DEFAULT");
        TranslationHeaderText.Text = T("Translation · selected locale", "Перевод · выбранная локализация");
        CopyAllButton.Content = T("Copy all for translator", "Скопировать всё для переводчика");
        CopyAllButton.ToolTip = T("Copy all visible lines as one block (Ctrl+Shift+C)", "Скопировать все видимые строки одним блоком (Ctrl+Shift+C)");
        SetCheckBox(IncludeInstructionBox, "Add prompt for AI", "Добавить промпт для AI",
            "Add DCS translation rules: preserve DictKey, Lua, commands and formatting",
            "Добавить правила перевода DCS: не менять DictKey, Lua, команды и форматирование");
        SetCheckBox(DeduplicateBatchBox, "Optimize duplicate lines", "Оптимизировать одинаковые строки",
            "Send identical source text once and apply its translation to every match",
            "Отправлять одинаковый оригинал один раз и применять перевод ко всем совпадениям");
        SetCheckBox(SyncScrollBox, "Sync scrolling", "Связать прокрутку",
            "Scroll original and translation together", "Прокручивать оригинал и перевод одновременно");
        SetCheckBox(PasteOverwriteBox, "Overwrite completed translations", "Заменять готовый перевод",
            "Allow paste to replace already completed lines", "Разрешить вставке заменить уже заполненные строки");
        PasteAllButton.Content = T("Paste full translation", "Вставить весь перевод");
        PasteAllButton.ToolTip = T("Paste the translator's complete marked response (Ctrl+Shift+V)", "Вставить полный размеченный ответ переводчика (Ctrl+Shift+V)");

        AutomationProperties.SetName(TranslationGrid, T("Mission translation table", "Таблица перевода миссии"));
        OriginalColumn.Header = T("Original · DEFAULT", "Оригинал · DEFAULT");
        TranslationColumn.Header = T("Translation editor", "Редактор перевода");

        FindLabelText.Text = T("Find:", "Найти:");
        FindTextBox.ToolTip = T("Text to find (Ctrl+F)", "Текст для поиска (Ctrl+F)");
        FindPreviousButton.ToolTip = T("Previous match (Shift+F3)", "Предыдущее совпадение (Shift+F3)");
        FindNextButton.ToolTip = T("Next match (F3)", "Следующее совпадение (F3)");
        ReplaceLabelText.Text = T("Replace with:", "Заменить на:");
        ReplaceTextBox.ToolTip = T("Replacement text (Ctrl+H)", "Текст для замены (Ctrl+H)");
        ReplaceSelectedButton.Content = T("Replace in selected", "Заменить в выбранных");
        ReplaceSelectedButton.ToolTip = T("Replace matches only in selected lines", "Заменить совпадения только в выделенных строках");
        ReplaceAllButton.Content = T("Replace in all visible", "Заменить во всех видимых");
        ReplaceAllButton.ToolTip = T("Replace matches in every line in the current filter", "Заменить совпадения во всех строках текущего фильтра");

        SetCheckBox(AiOverwriteExistingBox, "Retranslate completed", "Переводить заново заполненные",
            "AI replaces existing translations; turn off to preserve manual edits",
            "AI заменит существующий перевод; выключите для сохранения ручных правок");
        AiTranslateMissingButton.Content = T("Translate all missing", "Перевести все пропущенные");
        AiTranslateMissingButton.ToolTip = T("Run AI translation for all untranslated lines", "Запустить AI-перевод для всех строк без перевода");
        AiRetryButton.Content = T("Retry errors", "Повторить ошибки");
        AiRetryButton.ToolTip = T("Retry lines that AI could not translate", "Повторно отправить строки, которые AI не смог перевести");
        AiCancelButton.Content = T("Stop", "Остановить");
        AiCancelButton.ToolTip = T("Stop the AI translation queue", "Остановить очередь AI-перевода");

        CopyOriginalSelectedButton.Content = T("Use original for selected", "Взять оригинал для выбранных");
        CopyOriginalSelectedButton.ToolTip = T("Copy source text into the translation of selected lines", "Скопировать исходный текст в перевод выделенных строк");
        AiTranslateSelectedButton.Content = T("Translate selected with AI", "Перевести выбранные через AI");
        AiTranslateSelectedButton.ToolTip = T("Send only selected lines to AI", "Отправить в AI только выделенные строки");
        UndoSelectedButton.Content = T("Undo in selected", "Отменить в выбранных");
        UndoSelectedButton.ToolTip = T("Undo the last edit in selected lines (Ctrl+Z outside an editor)", "Отменить последнюю правку в выделенных строках (Ctrl+Z вне поля ввода)");
        ClearVisibleButton.Content = T("Clear visible", "Очистить видимые");
        ClearVisibleButton.ToolTip = T("Delete translations from every line in the current filter", "Удалить перевод из всех строк, показанных текущим фильтром");
        ApplyTranslationsButton.Content = T("Apply translations to mission", "Применить перевод к миссии");
        ApplyTranslationsButton.ToolTip = T("Apply changes to the open mission (Ctrl+Enter)", "Применить изменения к открытой миссии (Ctrl+Enter)");

        RelocalizeRuntimeText();
    }

    private static string T(string english, string russian)
        => UILocalization.GetCurrentLanguage().Equals("RU", StringComparison.OrdinalIgnoreCase) ? russian : english;

    private static void SetCheckBox(CheckBox checkBox, string english, string russian, string? englishToolTip = null, string? russianToolTip = null)
    {
        checkBox.Content = T(english, russian);
        if (englishToolTip != null && russianToolTip != null)
            checkBox.ToolTip = T(englishToolTip, russianToolTip);
    }

    private TextBlock[] RuntimeTextBlocks =>
    [
        WorkspaceSummaryText,
        SourceLineCountText,
        TranslationLineCountText,
        MatchCountText,
        TranslationStatsText,
        AiProgressText
    ];

    private void AttachRuntimeTextObservers()
    {
        if (_runtimeTextObserversAttached)
            return;
        var descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        foreach (var block in RuntimeTextBlocks)
            descriptor.AddValueChanged(block, RuntimeTextChanged);
        _runtimeTextObserversAttached = true;
    }

    private void DetachRuntimeTextObservers()
    {
        if (!_runtimeTextObserversAttached)
            return;
        var descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        foreach (var block in RuntimeTextBlocks)
            descriptor.RemoveValueChanged(block, RuntimeTextChanged);
        _runtimeTextObserversAttached = false;
    }

    private void RuntimeTextChanged(object? sender, EventArgs e)
    {
        if (_localizingRuntimeText || sender is not TextBlock block)
            return;
        var localized = LocalizeRuntimeText(block.Text);
        if (localized == block.Text)
            return;
        _localizingRuntimeText = true;
        block.Text = localized;
        _localizingRuntimeText = false;
    }

    private void RelocalizeRuntimeText()
    {
        _localizingRuntimeText = true;
        foreach (var block in RuntimeTextBlocks)
            block.Text = LocalizeRuntimeText(block.Text);
        _localizingRuntimeText = false;
    }

    private static string LocalizeRuntimeText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var russian = UILocalization.GetCurrentLanguage().Equals("RU", StringComparison.OrdinalIgnoreCase);
        (string From, string To)[] replacements = russian
            ?
            [
                ("Dictionary has no entries", "В словаре нет записей"),
                ("AI translation is ready", "AI-перевод готов к запуску"),
                ("AI queue is ready", "Очередь AI готова"),
                ("Running", "Выполняется"), ("Cancelling", "Останавливается"),
                ("Completed", "Завершено"), ("Cancelled", "Остановлено"), ("Idle", "Ожидание"),
                ("lines", "строк"), ("filled", "заполнено"), ("matches", "совпадений"),
                ("translated", "переведено"), ("missing", "отсутствует"), ("changed", "изменено"),
                ("skipped", "пропущено"), ("errors", "ошибок"), ("ИИ", "AI")
            ]
            :
            [
                ("В словаре нет записей", "Dictionary has no entries"),
                ("AI-перевод готов к запуску", "AI translation is ready"),
                ("ИИ-перевод готов к запуску", "AI translation is ready"),
                ("Очередь AI готова", "AI queue is ready"),
                ("Очередь ИИ готова", "AI queue is ready"),
                ("Выполняется", "Running"), ("Останавливается", "Cancelling"),
                ("Завершено", "Completed"), ("Остановлено", "Cancelled"), ("Ожидание", "Idle"),
                ("заполнено", "filled"), ("совпадений", "matches"), ("строк", "lines"),
                ("переведено", "translated"), ("отсутствует", "missing"), ("изменено", "changed"),
                ("пропущено", "skipped"), ("ошибок", "errors"), ("ИИ", "AI")
            ];
        foreach (var replacement in replacements)
            text = text.Replace(replacement.From, replacement.To, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, e);
    private void TranslationSearchBox_TextChanged(object sender, TextChangedEventArgs e) => SearchChanged?.Invoke(this, e);
    private void TranslationFilter_Changed(object sender, RoutedEventArgs e) => FilterChanged?.Invoke(this, e);
    private void ReloadTranslations_Click(object sender, RoutedEventArgs e) => ReloadRequested?.Invoke(this, e);
    private void CopyTranslationSource_Click(object sender, RoutedEventArgs e) => CopySourceRequested?.Invoke(this, e);
    private void CopyBatch_Click(object sender, RoutedEventArgs e) => CopyBatchRequested?.Invoke(this, e);
    private void PasteBatch_Click(object sender, RoutedEventArgs e) => PasteBatchRequested?.Invoke(this, e);
    private void ClearVisible_Click(object sender, RoutedEventArgs e) => ClearVisibleRequested?.Invoke(this, e);
    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNextRequested?.Invoke(this, e);
    private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindPreviousRequested?.Invoke(this, e);
    private void ReplaceSelected_Click(object sender, RoutedEventArgs e) => ReplaceSelectedRequested?.Invoke(this, e);
    private void ReplaceAll_Click(object sender, RoutedEventArgs e) => ReplaceAllRequested?.Invoke(this, e);
    private void AiTranslateSelected_Click(object sender, RoutedEventArgs e) => TranslateSelectedRequested?.Invoke(this, e);
    private void AiTranslateMissing_Click(object sender, RoutedEventArgs e) => TranslateMissingRequested?.Invoke(this, e);
    private void AiRetry_Click(object sender, RoutedEventArgs e) => RetryRequested?.Invoke(this, e);
    private void AiCancel_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, e);
    private void ApplyTranslations_Click(object sender, RoutedEventArgs e) => ApplyRequested?.Invoke(this, e);
    private void UndoSelected_Click(object sender, RoutedEventArgs e) => UndoSelectedRequested?.Invoke(this, e);

    private void Workspace_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.C)
        {
            CopyBatchRequested?.Invoke(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.V)
        {
            PasteBatchRequested?.Invoke(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            FindTextBox.Focus();
            FindTextBox.SelectAll();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.H)
        {
            ReplaceTextBox.Focus();
            ReplaceTextBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F3 && modifiers.HasFlag(ModifierKeys.Shift))
        {
            FindPreviousRequested?.Invoke(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.F3)
        {
            FindNextRequested?.Invoke(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            if (Keyboard.FocusedElement is TextBox focusedEditor && !focusedEditor.IsReadOnly)
                return;
            UndoSelectedRequested?.Invoke(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.Enter)
        {
            ApplyRequested?.Invoke(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void AttachSynchronizedScrolling()
    {
        if (_sourceScroll != null || _translationScroll != null)
            return;

        _sourceScroll = FindVisualChild<ScrollViewer>(SourceBatchText);
        _translationScroll = FindVisualChild<ScrollViewer>(TranslationBatchText);
        if (_sourceScroll == null || _translationScroll == null)
            return;

        _sourceScroll.ScrollChanged += SourceScroll_ScrollChanged;
        _translationScroll.ScrollChanged += TranslationScroll_ScrollChanged;
    }

    private void SourceScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || SyncScrollBox.IsChecked != true || _translationScroll == null)
            return;
        _syncingScroll = true;
        _translationScroll.ScrollToVerticalOffset(e.VerticalOffset);
        _translationScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
        _syncingScroll = false;
    }

    private void TranslationScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || SyncScrollBox.IsChecked != true || _sourceScroll == null)
            return;
        _syncingScroll = true;
        _sourceScroll.ScrollToVerticalOffset(e.VerticalOffset);
        _sourceScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
        _syncingScroll = false;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            var nested = FindVisualChild<T>(child);
            if (nested != null)
                return nested;
        }
        return null;
    }
}
