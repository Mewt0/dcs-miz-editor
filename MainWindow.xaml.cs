using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Microsoft.VisualBasic;
using MizEdit.Core;
using MizEdit.Services;
using MizEdit.Views;
using NAudio.Vorbis;
using NAudio.Wave;

namespace MizEdit
{
    public partial class MainWindow : Window
    {
        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };
        private static readonly string[] AudioExtensions = { ".ogg", ".wav", ".mp3" };

        private readonly MissionService _missionService = new();
        private readonly BatchService _batchService;
        private readonly OllamaTranslationService _ollamaTranslator = new();
        private readonly TranslationQueueRunner _translationQueue;
        private readonly TranslationCheckpointService _translationCheckpoints = new();
        private readonly SessionState _sessionState = new();
        private readonly SaveAnalysisService _saveAnalysisService = new();
        private readonly TranslationWorkspaceViewModel _translationWorkspaceModel =
            new(OllamaTranslationService.LooksLikeLuaScript);
        private MissionSession? _session;
        private WaveOutEvent? _waveOut;
        private WaveStream? _audioReader;
        private string? _selectedAudioPath;
        private string? _selectedScriptPath;
        private string? _selectedScriptOriginalText;
        private readonly List<MissionLua.RadioMessage> _radioTransmissions = new();
        private MissionLua.RadioMessage? _selectedRadioMessage;
        private readonly ObservableCollection<TranslationEntry> _translationEntries = new();
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _translationBaselines =
            new(StringComparer.OrdinalIgnoreCase);
        private ICollectionView? _translationView;
        private string? _translationLocale;
        private IReadOnlyList<TranslationBatchLine> _translationBatchLines = Array.Empty<TranslationBatchLine>();
        private TranslationBatchManifest? _lastCopiedTranslationManifest;
        private bool _batchRefreshPending;
        private CancellationTokenSource? _translationWorkspaceRefresh;
        private TranslationWorkspaceSnapshot? _translationWorkspaceSnapshot;
        private bool _suppressTranslationEntryUpdates;
        private bool _isLoadingBriefingFields;
        private bool _isRevertingLocale;
        private CancellationTokenSource? _translationSearchDebounce;
        private CancellationTokenSource? _pictureThumbnailLoads;
        private CancellationTokenSource? _kneeboardThumbnailLoads;
        private CancellationTokenSource? _triggerPictureThumbnailLoads;
        private bool _briefingEditorsExpanded;

        private DataGrid TranslationGrid => TranslationWorkspaceView.GridControl;
        private TextBox TranslationSearchBox => TranslationWorkspaceView.SearchBox;
        private CheckBox OnlyMissingTranslationsBox => TranslationWorkspaceView.OnlyMissingBox;
        private CheckBox OnlyChangedTranslationsBox => TranslationWorkspaceView.OnlyChangedBox;
        private CheckBox HideTechnicalTranslationsBox => TranslationWorkspaceView.HideTechnicalBox;
        private CheckBox ActionTextKeyBox => TranslationWorkspaceView.ActionTextBox;
        private CheckBox ActionRadioTextKeyBox => TranslationWorkspaceView.ActionRadioTextBox;
        private CheckBox DescriptionKeyBox => TranslationWorkspaceView.DescriptionBox;
        private CheckBox SubtitleKeyBox => TranslationWorkspaceView.SubtitleBox;
        private CheckBox SortieKeyBox => TranslationWorkspaceView.SortieBox;
        private CheckBox NameKeyBox => TranslationWorkspaceView.NameBox;
        private CheckBox ShowOtherKeysBox => TranslationWorkspaceView.OtherKeysBox;
        private CheckBox SkipEmptySourceBox => TranslationWorkspaceView.SkipEmptyBox;
        private TextBox SourceBatchText => TranslationWorkspaceView.SourceBatchBox;
        private TextBox TranslationBatchText => TranslationWorkspaceView.TranslationBatchBox;
        private TextBox TranslationFindBox => TranslationWorkspaceView.FindBox;
        private TextBox TranslationReplaceBox => TranslationWorkspaceView.ReplaceBox;
        private TextBlock TranslationMatchText => TranslationWorkspaceView.MatchText;
        private TextBlock SourceLineCountText => TranslationWorkspaceView.SourceLinesText;
        private TextBlock TranslationLineCountText => TranslationWorkspaceView.TranslationLinesText;
        private TextBlock TranslationStatsText => TranslationWorkspaceView.StatsText;
        private TextBlock AiProgressText => TranslationWorkspaceView.ProgressText;
        private ProgressBar AiProgressBar => TranslationWorkspaceView.ProgressBarControl;
        private ItemsControl AiErrorsList => TranslationWorkspaceView.ErrorsList;
        private CheckBox AiOverwriteExistingBox => TranslationWorkspaceView.OverwriteExistingBox;
        private Button AiTranslateSelectedButton => TranslationWorkspaceView.TranslateSelectedButton;
        private Button AiTranslateMissingButton => TranslationWorkspaceView.TranslateMissingButton;
        private Button AiRetryButton => TranslationWorkspaceView.RetryButton;
        private Button AiCancelButton => TranslationWorkspaceView.CancelButton;

        public MainWindow()
        {
            InitializeComponent();
            UILocalization.LanguageChanged += UILocalization_LanguageChanged;
            UpdateUILanguageButtons();
            _batchService = new BatchService(_missionService);
            _translationQueue = new TranslationQueueRunner(_ollamaTranslator);
            TranslationWorkspaceView.BackRequested += HideTranslationWorkspace_Click;
            TranslationWorkspaceView.SearchChanged += TranslationSearchBox_TextChanged;
            TranslationWorkspaceView.FilterChanged += TranslationFilter_Changed;
            TranslationWorkspaceView.ReloadRequested += ReloadTranslations_Click;
            TranslationWorkspaceView.CopySourceRequested += CopyTranslationSource_Click;
            TranslationWorkspaceView.CopyBatchRequested += CopyBatch_Click;
            TranslationWorkspaceView.PasteBatchRequested += PasteBatch_Click;
            TranslationWorkspaceView.TranslationBatchEdited += TranslationBatchEdited;
            TranslationWorkspaceView.ClearVisibleRequested += ClearVisible_Click;
            TranslationWorkspaceView.FindNextRequested += FindNext_Click;
            TranslationWorkspaceView.FindPreviousRequested += FindPrevious_Click;
            TranslationWorkspaceView.ReplaceSelectedRequested += ReplaceSelected_Click;
            TranslationWorkspaceView.ReplaceAllRequested += ReplaceAll_Click;
            TranslationWorkspaceView.FindBox.TextChanged += (_, _) => UpdateFindMatchCount();
            TranslationWorkspaceView.TranslateSelectedRequested += AiTranslateSelected_Click;
            TranslationWorkspaceView.TranslateMissingRequested += AiTranslateMissing_Click;
            TranslationWorkspaceView.RetryRequested += AiRetry_Click;
            TranslationWorkspaceView.CancelRequested += AiCancel_Click;
            TranslationWorkspaceView.ApplyRequested += ApplyTranslations_Click;
            TranslationWorkspaceView.UndoSelectedRequested += UndoSelected_Click;
            TranslationGrid.ItemsSource = _translationEntries;
            _translationView = CollectionViewSource.GetDefaultView(_translationEntries);
            _translationView.Filter = FilterTranslationEntry;
            _sessionState.PropertyChanged += (_, _) => UpdateSessionStateIndicator();
            _translationQueue.State.PropertyChanged += (_, _) => UpdateTranslationQueueUi();
            _translationQueue.State.Errors.CollectionChanged += (_, _) => UpdateTranslationQueueUi();
            AiErrorsList.ItemsSource = _translationQueue.State.Errors;
            SetEnabled(false);
            UpdateSessionStateIndicator();
            UpdateTranslationQueueUi();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            TryUseDarkTitleBar(new WindowInteropHelper(this).Handle);
        }

        private string CurrentLocale => (string?)LocaleCombo.SelectedItem ?? "DEFAULT";

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            StatusText.Text = UILocalization.Get("UI.Ready");
            SideStatusText.Text = UILocalization.Get("UI.NoFile");
        }

        private void UiLanguageEn_Click(object sender, RoutedEventArgs e) => UILocalization.SetLanguage("EN");

        private void UiLanguageRu_Click(object sender, RoutedEventArgs e) => UILocalization.SetLanguage("RU");

        private void UILocalization_LanguageChanged(string language)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateUILanguageButtons();
                UpdateSessionStateIndicator();
                if (_session == null)
                {
                    StatusText.Text = UILocalization.Get("UI.Ready");
                    SideStatusText.Text = UILocalization.Get("UI.NoFile");
                }
            });
        }

        private void UpdateUILanguageButtons()
        {
            var isRussian = string.Equals(UILocalization.GetCurrentLanguage(), "RU", StringComparison.OrdinalIgnoreCase);
            UiLanguageRuButton.IsEnabled = !isRussian;
            UiLanguageEnButton.IsEnabled = isRussian;
            UiLanguageRuButton.FontWeight = isRussian ? FontWeights.Bold : FontWeights.Normal;
            UiLanguageEnButton.FontWeight = isRussian ? FontWeights.Normal : FontWeights.Bold;
        }

        protected override void OnClosed(EventArgs e)
        {
            UILocalization.LanguageChanged -= UILocalization_LanguageChanged;
            _translationSearchDebounce?.Cancel();
            _translationSearchDebounce?.Dispose();
            _pictureThumbnailLoads?.Cancel();
            _pictureThumbnailLoads?.Dispose();
            _kneeboardThumbnailLoads?.Cancel();
            _kneeboardThumbnailLoads?.Dispose();
            _triggerPictureThumbnailLoads?.Cancel();
            _triggerPictureThumbnailLoads?.Dispose();
            StopAudio();
            CloseSession();
            _ollamaTranslator.Dispose();
            base.OnClosed(e);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_translationQueue.State.IsActive)
            {
                _translationQueue.Cancel();
                e.Cancel = true;
                MessageBox.Show(
                    "The translation queue is being stopped. Close MizEdit again after it has stopped.",
                    "Translation in progress",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (!ConfirmSaveChanges("close MizEdit"))
            {
                e.Cancel = true;
                return;
            }

            base.OnClosing(e);
        }

        private static void TryUseDarkTitleBar(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return;

            var enabled = 1;
            _ = DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

        private void SetEnabled(bool enabled)
        {
            LocaleCombo.IsEnabled = enabled;
            LocaleIndexBox.IsEnabled = enabled;
            LocaleNameBox.IsEnabled = enabled;
            MissionNameBox.IsEnabled = enabled;
            MissionDescBox.IsEnabled = enabled;
            RedTaskBox.IsEnabled = enabled;
            BlueTaskBox.IsEnabled = enabled;
            OpenTranslationWorkspaceButton.IsEnabled = enabled;
        }

        private void ToggleBriefingEditors_Click(object sender, RoutedEventArgs e)
        {
            _briefingEditorsExpanded = !_briefingEditorsExpanded;
            MissionDescBox.Height = _briefingEditorsExpanded ? 360 : 150;
            RedTaskBox.Height = _briefingEditorsExpanded ? 260 : 110;
            BlueTaskBox.Height = _briefingEditorsExpanded ? 260 : 110;
            BriefingExpandButton.Content = UILocalization.Get(
                _briefingEditorsExpanded ? "UI.CompactEditors" : "UI.ExpandEditors");
        }

        private void ShowTranslationWorkspace_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null)
                return;

            EnsureRussianTranslationLocale();
            LoadTranslationWorkspace();
            EditorWorkspace.Visibility = Visibility.Collapsed;
            ToolsBar.Visibility = Visibility.Collapsed;
            LocaleBar.Visibility = Visibility.Collapsed;
            TranslationWorkspaceView.Visibility = Visibility.Visible;
            UpdateTranslationStats();
        }

        private string EnsureRussianTranslationLocale()
        {
            if (_session == null || !CurrentLocale.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
                return CurrentLocale;

            var target = _session.Localization.GetLocales()
                .FirstOrDefault(locale => locale.Equals("RU", StringComparison.OrdinalIgnoreCase) ||
                                          locale.StartsWith("RU-", StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                target = "RU";
                _session.Localization.AddLocale(target);
                LoadLocales(target);
                _sessionState.MarkDirty();
                StatusText.Text = "Создана отдельная локаль RU; оригинал DEFAULT не изменяется.";
            }

            _isRevertingLocale = true;
            if (!LocaleCombo.Items.Contains(target))
                LocaleCombo.Items.Add(target);
            LocaleCombo.SelectedItem = target;
            _isRevertingLocale = false;
            UpdateLocaleIndex();
            LoadBriefingFields();
            return target;
        }

        private void HideTranslationWorkspace_Click(object? sender, RoutedEventArgs e)
        {
            if (_translationQueue.State.IsActive)
            {
                StatusText.Text = "Сначала остановите очередь ИИ-перевода.";
                return;
            }

            ApplyTranslationChanges(_translationLocale ?? CurrentLocale, updateStatus: false);
            TranslationWorkspaceView.Visibility = Visibility.Collapsed;
            EditorWorkspace.Visibility = Visibility.Visible;
            ToolsBar.Visibility = Visibility.Visible;
            LocaleBar.Visibility = Visibility.Visible;
        }

        private void LoadMission_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "DCS Mission (*.miz)|*.miz|All files|*.*",
                Title = "Open DCS mission"
            };

            if (dlg.ShowDialog() != true)
                return;

            TryOpenMission(dlg.FileName);
        }

        public bool TryOpenMission(string missionPath)
        {
            if (_translationQueue.State.IsActive)
            {
                MessageBox.Show(
                    "Stop the AI translation queue before opening another mission.",
                    "Translation in progress",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (_session != null && !ConfirmSaveChanges("open another mission"))
                return false;

            try
            {
                CloseSession();
                _session = _missionService.LoadMission(missionPath);
                LoadLocales();
                LoadBriefingFields();
                SetEnabled(true);
                _sessionState.Reset();

                SideStatusText.Text = Path.GetFileName(missionPath);
                MizIdText.Text = BuildMissionSummary();
                StatusText.Text = $"{UILocalization.Get("UI.Loaded")}: {missionPath} ({CurrentLocale})";
                UpdateSessionStateIndicator();
                return true;
            }
            catch (Exception ex)
            {
                SetEnabled(false);
                SideStatusText.Text = UILocalization.Get("UI.NoFile");
                MizIdText.Text = "";
                StatusText.Text = UILocalization.Get("UI.LoadError");
                _sessionState.Reset();
                MessageBox.Show(ex.Message, UILocalization.Get("UI.LoadError"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void UpdateSessionStateIndicator()
        {
            if (_session == null || (_sessionState.SaveState == SaveState.Idle && !_sessionState.IsDirty))
            {
                SessionStateBadge.Visibility = Visibility.Collapsed;
                Title = _session == null
                    ? "DCS Miz Editor"
                    : $"{SideStatusText.Text} — DCS Miz Editor";
                return;
            }

            SessionStateBadge.Visibility = Visibility.Visible;
            var (text, brushKey) = _sessionState.SaveState switch
            {
                SaveState.Saving => ("● Saving...", "AccentBrush"),
                SaveState.Error => ($"● {UILocalization.Get("UI.SaveError")}", "DangerBrush"),
                SaveState.Saved => ($"✓ {UILocalization.Get("UI.Saved")}", "AccentBrush"),
                _ => ($"● {UILocalization.Get("UI.Unsaved")}", "AccentStrongBrush")
            };
            SessionStateText.Text = text;
            SessionStateText.Foreground = (System.Windows.Media.Brush)FindResource(brushKey);
            Title = $"{(_sessionState.IsDirty ? "* " : "")}{SideStatusText.Text} — DCS Miz Editor";
        }

        private void BriefingField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_session != null && !_isLoadingBriefingFields)
                _sessionState.MarkDirty();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _ = TrySaveCurrentSession();
        }

        private bool TrySaveCurrentSession()
        {
            if (_session == null)
                return true;

            try
            {
                StopAudio();
                ApplyPendingUiChanges();
                if (!ConfirmSaveAnalysis())
                {
                    StatusText.Text = "Сохранение отменено после просмотра изменений.";
                    return false;
                }
                _sessionState.MarkSaving();
                _missionService.Save(_session);
                MarkSuccessfulSaveBaseline();
                _sessionState.MarkSaved();
                StatusText.Text = _session.Archive.LastBackupPath is { } backup
                    ? $"Saved safely. Backup: {backup}"
                    : "Saved safely";
                return true;
            }
            catch (Exception ex)
            {
                _sessionState.MarkError();
                StatusText.Text = UILocalization.Get("UI.SaveError");
                MessageBox.Show(ex.Message, UILocalization.Get("UI.SaveError"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private bool ConfirmSaveChanges(string action)
        {
            if (_session == null || !_sessionState.IsDirty)
                return true;

            var result = MessageBox.Show(
                $"The current mission has unsaved changes. Save before you {action}?",
                "Unsaved mission changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);
            return result switch
            {
                MessageBoxResult.Yes => TrySaveCurrentSession(),
                MessageBoxResult.No => true,
                _ => false
            };
        }

        private void SaveAsMiz_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            var dlg = new SaveFileDialog
            {
                Filter = "DCS Mission (*.miz)|*.miz",
                Title = "Save DCS mission as"
            };

            if (dlg.ShowDialog() != true)
                return;

            try
            {
                StopAudio();
                ApplyPendingUiChanges();
                if (!ConfirmSaveAnalysis())
                {
                    StatusText.Text = "Сохранение отменено после просмотра изменений.";
                    return;
                }
                _sessionState.MarkSaving();
                _missionService.SaveAsMiz(_session, dlg.FileName);
                MarkSuccessfulSaveBaseline();
                _sessionState.MarkSaved();
                StatusText.Text = _session.Archive.LastBackupPath is { } backup
                    ? $"Saved as: {dlg.FileName}. Backup: {backup}"
                    : $"Saved as: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                _sessionState.MarkError();
                StatusText.Text = UILocalization.Get("UI.SaveError");
                MessageBox.Show(ex.Message, UILocalization.Get("UI.SaveError"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool ConfirmSaveAnalysis()
        {
            if (_session == null)
                return false;

            var locale = _translationLocale ?? CurrentLocale;
            _translationBaselines.TryGetValue(locale, out var baseline);
            StatusText.Text = "Анализ изменений и качества перевода...";
            var report = _saveAnalysisService.Analyze(
                _session.SourcePath,
                _session.Archive.WorkDir,
                _translationEntries,
                baselineTranslations: baseline);
            var dialog = new SaveAnalysisWindow(report) { Owner = this };
            return dialog.ShowDialog() == true;
        }

        private void MarkSuccessfulSaveBaseline()
        {
            _translationBaselines.Clear();
            if (_translationLocale is { } locale)
            {
                _translationBaselines[locale] = _translationEntries.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Translation,
                    StringComparer.OrdinalIgnoreCase);
            }
            foreach (var entry in _translationEntries)
                entry.MarkSaved();
        }

        private void SaveAsTxt_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            var dlg = new SaveFileDialog
            {
                Filter = "Text file (*.txt)|*.txt|All files|*.*",
                Title = "Export briefing text"
            };

            if (dlg.ShowDialog() != true)
                return;

            ApplyBriefingFieldsToSession();
            _missionService.ExportTxt(_session, CurrentLocale, dlg.FileName);
            StatusText.Text = $"Exported: {dlg.FileName}";
        }

        private void ImportTxt_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            var dlg = new OpenFileDialog
            {
                Filter = "Text file (*.txt)|*.txt|All files|*.*",
                Title = "Import briefing text"
            };

            if (dlg.ShowDialog() != true)
                return;

            _missionService.ImportTxt(_session, CurrentLocale, dlg.FileName);
            LoadBriefingFields();
            _sessionState.MarkDirty();
            StatusText.Text = $"Imported: {dlg.FileName}";
        }

        private void BatchImportTxt_Click(object sender, RoutedEventArgs e)
        {
            var folder = Interaction.InputBox("Folder with .miz files:", "Batch import", Environment.CurrentDirectory);
            if (string.IsNullOrWhiteSpace(folder)) return;

            var locale = Interaction.InputBox("Locale:", "Batch import", "DEFAULT");
            if (string.IsNullOrWhiteSpace(locale)) return;

            var result = _batchService.BatchImportTxt(folder.Trim(), locale.Trim());
            MessageBox.Show(string.Join(Environment.NewLine, result), "Batch import", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BatchSaveAsTxt_Click(object sender, RoutedEventArgs e)
        {
            var folder = Interaction.InputBox("Folder with .miz files:", "Batch export", Environment.CurrentDirectory);
            if (string.IsNullOrWhiteSpace(folder)) return;

            var locale = Interaction.InputBox("Locale:", "Batch export", "DEFAULT");
            if (string.IsNullOrWhiteSpace(locale)) return;

            var result = _batchService.BatchSaveAsTxt(folder.Trim(), locale.Trim());
            MessageBox.Show(string.Join(Environment.NewLine, result), "Batch export", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BatchStateAnalyze_Click(object sender, RoutedEventArgs e)
        {
            var folder = Interaction.InputBox("Folder with .miz files:", "Batch analyze", Environment.CurrentDirectory);
            if (string.IsNullOrWhiteSpace(folder)) return;

            var result = _batchService.BatchStateAnalyze(folder.Trim());
            MessageBox.Show(string.Join(Environment.NewLine, result), "Batch analyze", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void CleanupMissionProtection_Click(object sender, RoutedEventArgs e)
        {
            var openDialog = new OpenFileDialog
            {
                Filter = "DCS Mission (*.miz)|*.miz",
                Title = "Выберите свою или разрешённую .miz миссию"
            };
            if (openDialog.ShowDialog() != true)
                return;

            try
            {
                var plan = MissionProtectionCleanupService.Analyze(openDialog.FileName);
                if (!plan.HasRemovableFindings)
                {
                    MessageBox.Show(
                        "В миссии не найдено removable mission id/ext_loader полей.",
                        "Очистка mission id",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var findingsSummary = string.Join(
                    Environment.NewLine,
                    plan.Findings.Select(f => $"{f.Path}: {f.Kind} {f.ValueSummary}"));
                var confirm = MessageBox.Show(
                    "Будет создана отдельная копия .miz. Оригинал не меняется." +
                    Environment.NewLine + Environment.NewLine +
                    "Найдено:" + Environment.NewLine +
                    findingsSummary + Environment.NewLine + Environment.NewLine +
                    "Продолжить?",
                    "Очистка mission id",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes)
                    return;

                var exactMissionId = Interaction.InputBox(
                    "Exact mission id для удаления (необязательно). Если оставить пустым, будут удалены все найденные mission id/ext_loader поля.",
                    "Очистка mission id",
                    string.Empty).Trim();

                var sourceDir = Path.GetDirectoryName(openDialog.FileName) ?? Environment.CurrentDirectory;
                var sourceName = Path.GetFileNameWithoutExtension(openDialog.FileName);
                var saveDialog = new SaveFileDialog
                {
                    Filter = "DCS Mission (*.miz)|*.miz",
                    Title = "Сохранить очищенную копию",
                    InitialDirectory = sourceDir,
                    FileName = $"{sourceName}_cleanup.miz"
                };
                if (saveDialog.ShowDialog() != true)
                    return;

                var options = new MissionProtectionCleanupOptions(
                    RemoveExtLoader: true,
                    RemoveRootMissionIds: true,
                    ExactMissionId: string.IsNullOrWhiteSpace(exactMissionId) ? null : exactMissionId);
                var result = MissionProtectionCleanupService.ApplyToCopy(
                    openDialog.FileName,
                    saveDialog.FileName,
                    options);

                MessageBox.Show(
                    "Очищенная копия создана:" + Environment.NewLine +
                    result.OutputMizPath + Environment.NewLine + Environment.NewLine +
                    "Удалено:" + Environment.NewLine +
                    string.Join(Environment.NewLine, result.RemovedPaths),
                    "Очистка mission id",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                StatusText.Text = $"Создана очищенная копия: {result.OutputMizPath}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка очистки mission id", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InfoAbout_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "MizEdit edits DCS .miz archives: briefing text, localized resources, kneeboard images, audio, scripts and simple Lua triggers.",
                "About MizEdit",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null)
                return;

            ApplyPendingUiChanges();
            _sessionState.MarkDirty();
            StatusText.Text = "Applied in memory. Use Save or Save as Miz to write the .miz file.";
        }

        private void ApplyPendingUiChanges()
        {
            ApplyBriefingFieldsToSession();
            ApplyTranslationChanges(_translationLocale ?? CurrentLocale, updateStatus: false);
            ApplySelectedRadioSubtitle();
            SaveSelectedScriptInWorkDir();
        }

        private void ApplyBriefingFieldsToSession()
        {
            if (_session?.Mission == null) return;

            var missionChanged = false;
            var hasName = !string.IsNullOrEmpty(_session.Mission.GetString("name"));
            var hasSortie = !string.IsNullOrEmpty(_session.Mission.GetString("sortie"));

            // The UI displays sortie as a fallback when mission.name is absent.
            // Do not create the missing field on save: that needlessly dirties and
            // reserializes the complete mission Lua table.
            if (hasName)
                missionChanged |= _session.Localization.SetBriefingString(_session.Mission, "name", MissionNameBox.Text, CurrentLocale);
            else if (hasSortie)
                missionChanged |= _session.Localization.SetBriefingString(_session.Mission, "sortie", MissionNameBox.Text, CurrentLocale);
            else if (CurrentLocale.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
                missionChanged |= _session.Localization.SetBriefingString(_session.Mission, "name", MissionNameBox.Text, CurrentLocale);

            missionChanged |= _session.Localization.SetBriefingString(_session.Mission, "descriptionText", MissionDescBox.Text, CurrentLocale);
            missionChanged |= _session.Localization.SetBriefingString(_session.Mission, "descriptionRedTask", RedTaskBox.Text, CurrentLocale);
            missionChanged |= _session.Localization.SetBriefingString(_session.Mission, "descriptionBlueTask", BlueTaskBox.Text, CurrentLocale);
            if (missionChanged)
                _session.MarkMissionDirty();
        }

        private void ApplySelectedRadioSubtitle()
        {
            if (_session == null || _selectedRadioMessage == null)
                return;
            if (!_selectedRadioMessage.SubtitleKey.StartsWith("DictKey_", StringComparison.OrdinalIgnoreCase))
                return;

            _session.Localization.UpdateDictionaryEntry(CurrentLocale, _selectedRadioMessage.SubtitleKey, RadioSubtitleTextBox.Text);
        }

        private void SaveSelectedScriptInWorkDir()
        {
            if (string.IsNullOrWhiteSpace(_selectedScriptPath))
                return;
            if (!File.Exists(_selectedScriptPath))
                return;
            if (string.Equals(ScriptBox.Text, _selectedScriptOriginalText, StringComparison.Ordinal))
                return;

            File.WriteAllText(_selectedScriptPath, ScriptBox.Text);
            _selectedScriptOriginalText = ScriptBox.Text;
        }

        private void LoadLocales(string? preferredLocale = null)
        {
            if (_session == null) return;

            LocaleCombo.Items.Clear();
            foreach (var locale in _session.Localization.GetLocales())
                LocaleCombo.Items.Add(locale);

            if (!string.IsNullOrWhiteSpace(preferredLocale) && LocaleCombo.Items.Contains(preferredLocale))
            {
                LocaleCombo.SelectedItem = preferredLocale;
            }
            else
            {
                LocaleCombo.SelectedItem = LocaleCombo.Items.Contains("DEFAULT")
                    ? "DEFAULT"
                    : LocaleCombo.Items.Cast<object>().FirstOrDefault();
            }
            UpdateLocaleIndex();
        }

        private void LocaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRevertingLocale)
                return;

            if (_translationQueue.State.IsActive)
            {
                _isRevertingLocale = true;
                LocaleCombo.SelectedItem = _translationLocale ?? "DEFAULT";
                _isRevertingLocale = false;
                StatusText.Text = "Stop the AI translation queue before changing locale.";
                return;
            }

            if (_session != null && _translationLocale != null &&
                !_translationLocale.Equals(CurrentLocale, StringComparison.OrdinalIgnoreCase))
            {
                ApplyTranslationChanges(_translationLocale, updateStatus: false);
            }

            UpdateLocaleIndex();
            if (_session != null)
            {
                LoadBriefingFields();
                LoadTranslationWorkspace();
            }
        }

        private void LoadTranslationWorkspace()
        {
            _translationWorkspaceRefresh?.Cancel();
            _translationWorkspaceModel.InvalidateAll();
            _translationWorkspaceSnapshot = null;
            _translationEntries.Clear();
            _translationLocale = null;
            _lastCopiedTranslationManifest = null;

            if (_session == null)
            {
                UpdateTranslationStats();
                return;
            }

            var locale = CurrentLocale;
            var source = _session.Localization.GetDictionaryEntries("DEFAULT");
            var target = _session.Localization.GetDictionaryEntries(locale);
            if (!_translationBaselines.ContainsKey(locale))
            {
                _translationBaselines[locale] = new Dictionary<string, string>(
                    target,
                    StringComparer.OrdinalIgnoreCase);
            }
            var keys = source.Keys
                .Concat(target.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase);

            foreach (var key in keys)
            {
                source.TryGetValue(key, out var sourceText);
                target.TryGetValue(key, out var translation);
                var entry = new TranslationEntry(key, sourceText ?? string.Empty, translation ?? string.Empty);
                entry.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName is nameof(TranslationEntry.Translation) or nameof(TranslationEntry.IsMissing))
                    {
                        _translationWorkspaceModel.InvalidateChangedEntry(entry);
                        if (entry.IsDirty)
                            _sessionState.MarkDirty();
                        if (_suppressTranslationEntryUpdates)
                            return;
                        UpdateTranslationStats();
                        ScheduleBatchRefresh();
                    }
                };
                _translationEntries.Add(entry);
            }

            _translationLocale = locale;
            var restored = _translationCheckpoints.RestoreMissing(
                _session.SourcePath,
                locale,
                _translationEntries);
            if (restored > 0)
                StatusText.Text = $"Восстановлено переводов из контрольной точки: {restored}.";
            _translationView?.Refresh();
            UpdateTranslationStats();
            RefreshBatchWorkspace();
        }

        private bool FilterTranslationEntry(object item)
        {
            if (item is not TranslationEntry entry)
                return false;
            if (TranslationWorkspaceViewModel.IsBackendPlaceholder(entry.Key, entry.SourceText))
                return false;
            if (OnlyMissingTranslationsBox.IsChecked == true && !entry.IsMissing)
                return false;
            if (OnlyChangedTranslationsBox.IsChecked == true && !entry.IsDirty)
                return false;
            if (SkipEmptySourceBox.IsChecked == true && string.IsNullOrWhiteSpace(entry.SourceText))
                return false;
            if (HideTechnicalTranslationsBox.IsChecked == true &&
                OllamaTranslationService.LooksLikeLuaScript(entry.SourceText))
            {
                return false;
            }

            var key = entry.Key;
            var isActionRadio = key.Contains("ActionRadioText", StringComparison.OrdinalIgnoreCase);
            var isActionText = !isActionRadio && key.Contains("ActionText", StringComparison.OrdinalIgnoreCase);
            var isDescription = key.Contains("description", StringComparison.OrdinalIgnoreCase);
            var isSubtitle = key.Contains("subtitle", StringComparison.OrdinalIgnoreCase);
            var isSortie = key.Contains("sortie", StringComparison.OrdinalIgnoreCase);
            var isName = key.Contains("name", StringComparison.OrdinalIgnoreCase);
            var isKnownType = isActionRadio || isActionText || isDescription || isSubtitle || isSortie || isName;

            if (isActionRadio && ActionRadioTextKeyBox.IsChecked != true)
                return false;
            if (isActionText && ActionTextKeyBox.IsChecked != true)
                return false;
            if (isDescription && DescriptionKeyBox.IsChecked != true)
                return false;
            if (isSubtitle && SubtitleKeyBox.IsChecked != true)
                return false;
            if (isSortie && SortieKeyBox.IsChecked != true)
                return false;
            if (isName && NameKeyBox.IsChecked != true)
                return false;
            if (!isKnownType && ShowOtherKeysBox.IsChecked != true)
                return false;

            var query = TranslationSearchBox.Text.Trim();
            return query.Length == 0 ||
                   entry.Key.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                   entry.SourceText.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                   entry.Translation.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        }

        private async void TranslationSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _translationSearchDebounce?.Cancel();
            _translationSearchDebounce?.Dispose();
            _translationSearchDebounce = new CancellationTokenSource();
            try
            {
                await Task.Delay(250, _translationSearchDebounce.Token);
                _translationView?.Refresh();
                RefreshBatchWorkspace();
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void TranslationFilter_Changed(object sender, RoutedEventArgs e)
        {
            _translationView?.Refresh();
            RefreshBatchWorkspace();
        }

        private void ApplyTranslations_Click(object sender, RoutedEventArgs e)
        {
            var locale = _translationLocale ?? CurrentLocale;
            if (locale.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
            {
                locale = EnsureRussianTranslationLocale();
            }

            ApplyTranslationChanges(locale, updateStatus: true);
        }

        private void ApplyTranslationChanges(string locale, bool updateStatus)
        {
            if (_session == null || _translationEntries.Count == 0)
                return;

            if (locale.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
            {
                if (updateStatus)
                    MessageBox.Show("DEFAULT — исходный текст миссии. Выберите или создайте локаль RU перед применением перевода.", "Защита оригинала", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusText.Text = "Перевод не применён: локаль DEFAULT защищена от перезаписи.";
                return;
            }

            TranslationGrid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
            TranslationGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

            var protectedLua = _translationEntries.Count(entry => entry.IsDirty && IsProtectedTranslationEntry(entry));
            var changed = _translationEntries
                .Where(entry => entry.IsDirty && !IsProtectedTranslationEntry(entry))
                .ToList();
            if (changed.Count == 0)
            {
                if (updateStatus && protectedLua > 0)
                    StatusText.Text = $"Пропущено защищённых Lua/технических записей: {protectedLua}.";
                return;
            }

            _session.Localization.UpdateDictionaryEntriesWithDefaultFallback(
                locale,
                changed.Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Translation)));
            _sessionState.MarkDirty();
            foreach (var entry in changed)
                entry.MarkSaved();

            _translationCheckpoints.Delete(_session.SourcePath, locale);

            UpdateTranslationStats();
            RefreshBatchWorkspace();
            if (updateStatus)
                StatusText.Text = $"Применено переводов: {changed.Count} ({locale}). Сохраните .miz для завершения.";
        }

        private void CopyTranslationSource_Click(object sender, RoutedEventArgs e)
        {
            RunTranslationBulkUpdate(() =>
            {
                foreach (var entry in TranslationGrid.SelectedItems.OfType<TranslationEntry>().Where(entry => !IsProtectedTranslationEntry(entry)))
                    entry.Translation = entry.SourceText;
            });
        }

        private List<TranslationEntry> GetVisibleTranslationEntries()
            => _translationView?.Cast<TranslationEntry>().ToList() ?? new List<TranslationEntry>();

        private List<TranslationEntry> GetEditableVisibleTranslationEntries()
            => GetVisibleTranslationEntries().Where(entry => !IsProtectedTranslationEntry(entry)).ToList();

        private static bool IsProtectedTranslationEntry(TranslationEntry entry)
            => OllamaTranslationService.LooksLikeLuaScript(entry.SourceText) ||
               TranslationWorkspaceViewModel.IsBackendPlaceholder(entry.Key, entry.SourceText);

        private void RunTranslationBulkUpdate(Action action)
        {
            _suppressTranslationEntryUpdates = true;
            try
            {
                action();
            }
            finally
            {
                _suppressTranslationEntryUpdates = false;
            }

            _translationView?.Refresh();
            UpdateTranslationStats();
            ScheduleBatchRefresh();
        }

        private async void RefreshBatchWorkspace()
            => await RefreshBatchWorkspaceAsync();

        private async Task<bool> RefreshBatchWorkspaceAsync()
        {
            if (!IsInitialized)
                return false;

            _translationWorkspaceRefresh?.Cancel();
            _translationWorkspaceRefresh?.Dispose();
            var cancellation = new CancellationTokenSource();
            _translationWorkspaceRefresh = cancellation;
            var input = _translationWorkspaceModel.Capture(
                _translationEntries,
                CaptureTranslationWorkspaceFilter());

            TranslationWorkspaceSnapshot snapshot;
            try
            {
                snapshot = await _translationWorkspaceModel.BuildSnapshotAsync(input, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (cancellation.IsCancellationRequested || !_translationWorkspaceModel.IsCurrent(snapshot))
                return false;

            _translationWorkspaceSnapshot = snapshot;
            _translationBatchLines = snapshot.BatchLines;
            TranslationWorkspaceView.SetRowNumbers(_translationBatchLines);
            var displayManifest = TranslationBatchDocument.BuildManifest(_translationBatchLines, deduplicate: false);
            SourceBatchText.Text = TranslationBatchDocument.FormatSource(displayManifest, includeInstruction: false);
            TranslationWorkspaceView.SetTranslationBatchText(
                TranslationBatchDocument.FormatTranslation(_translationBatchLines));

            var metrics = snapshot.Metrics;
            SourceLineCountText.Text = FormatSourceMetrics(metrics);
            TranslationLineCountText.Text = FormatTranslationMetrics(metrics);
            TranslationWorkspaceView.SetCorpusMetrics(metrics);
            UpdateFindMatchCount();
            return true;
        }

        private TranslationWorkspaceFilterOptions CaptureTranslationWorkspaceFilter()
        {
            return new TranslationWorkspaceFilterOptions(
                OnlyMissing: OnlyMissingTranslationsBox.IsChecked == true,
                OnlyChanged: OnlyChangedTranslationsBox.IsChecked == true,
                SkipEmptySource: SkipEmptySourceBox.IsChecked == true,
                // Technical/Lua entries may be shown in the grid, but are never exported or edited in a batch.
                HideTechnical: true,
                IncludeActionRadioText: ActionRadioTextKeyBox.IsChecked == true,
                IncludeActionText: ActionTextKeyBox.IsChecked == true,
                IncludeDescription: DescriptionKeyBox.IsChecked == true,
                IncludeSubtitle: SubtitleKeyBox.IsChecked == true,
                IncludeSortie: SortieKeyBox.IsChecked == true,
                IncludeName: NameKeyBox.IsChecked == true,
                IncludeOther: ShowOtherKeysBox.IsChecked == true,
                SearchText: TranslationSearchBox.Text.Trim(),
                Deduplicate: TranslationWorkspaceView.DeduplicateOption.IsChecked == true);
        }

        private static string FormatSourceMetrics(TranslationCorpusMetrics metrics)
            => $"видимых строк {metrics.VisibleLines}/{metrics.TotalPhysicalLines} · непустых {metrics.NonEmptyVisibleLines} · исключено {metrics.ExcludedEmptyLines + metrics.ExcludedTechnicalLines + metrics.ExcludedOtherLines}";

        private static string FormatTranslationMetrics(TranslationCorpusMetrics metrics)
            => $"заполнено {metrics.FilledVisibleLines}/{metrics.VisibleLines} · уникальных {metrics.UniqueExportLines} · дубликатов {metrics.DeduplicatedAliasLines}";

        private void ScheduleBatchRefresh()
        {
            _translationWorkspaceRefresh?.Cancel();
            if (_batchRefreshPending)
                return;
            _batchRefreshPending = true;
            Dispatcher.BeginInvoke(() =>
            {
                _batchRefreshPending = false;
                RefreshBatchWorkspace();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private async void CopyBatch_Click(object sender, RoutedEventArgs e)
        {
            if (!await RefreshBatchWorkspaceAsync())
                return;
            if (_translationBatchLines.Count == 0)
            {
                StatusText.Text = "По текущим фильтрам нет текста для копирования.";
                return;
            }

            var manifest = TranslationBatchDocument.BuildManifest(
                _translationBatchLines,
                TranslationWorkspaceView.DeduplicateOption.IsChecked == true);
            var exportFormat = TranslationBatchExportFormat.CompactNumbered;
            var text = TranslationBatchDocument.FormatSource(
                manifest,
                TranslationWorkspaceView.IncludeInstructionOption.IsChecked == true,
                exportFormat);
            try
            {
                Clipboard.SetText(text);
                _lastCopiedTranslationManifest = manifest;
                StatusText.Text = manifest.AliasCount > 0
                    ? $"Скопировано уникальных строк: {manifest.ExportItems.Count}; формат 1»; совпадений оптимизировано: {manifest.AliasCount}."
                    : $"Скопировано строк одним пакетом: {manifest.ExportItems.Count}; формат 1». Вставьте их в любую нейросеть.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Не удалось скопировать пакет: {ex.Message}";
            }
        }

        private void PasteBatch_Click(object sender, RoutedEventArgs e)
        {
            string text;
            try
            {
                text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Не удалось прочитать буфер обмена: {ex.Message}";
                return;
            }

            var manifest = SelectClipboardManifest(text ?? string.Empty);
            var plan = TranslationBatchDocument.Analyze(
                text ?? string.Empty,
                manifest,
                TranslationWorkspaceView.PasteOverwriteOption.IsChecked == true,
                _translationLocale ?? CurrentLocale);

            if (!TranslationWorkspaceView.ShowImportPreview(plan))
            {
                StatusText.Text = "Вставка перевода отменена; данные не изменены.";
                return;
            }

            TranslationBatchImportResult result = new(0, 0, 0);
            RunTranslationBulkUpdate(() => result = TranslationBatchDocument.Apply(plan));
            if (!result.Success)
            {
                MessageBox.Show(result.Error, "Вставка перевода", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusText.Text = result.Error;
                return;
            }

            StatusText.Text = $"Применено строк: {plan.ChangedCount}; обновлено ключей DictKey: {result.ChangedEntries}" +
                              (plan.RejectedCount > 0 ? $"; отклонено: {plan.RejectedCount}." : ".");
        }

        private TranslationBatchManifest SelectClipboardManifest(string text)
        {
            if (ContainsBatchMarker(text) && _lastCopiedTranslationManifest is not null)
                return _lastCopiedTranslationManifest;

            var selectedEntries = TranslationGrid.SelectedItems
                .OfType<TranslationEntry>()
                .Distinct()
                .ToList();
            if (!ContainsBatchMarker(text) && selectedEntries.Count > 0)
                return TranslationBatchDocument.BuildManifest(
                    TranslationBatchDocument.Build(selectedEntries),
                    deduplicate: false);

            return TranslationBatchDocument.BuildManifest(_translationBatchLines, deduplicate: false);
        }

        private static bool ContainsBatchMarker(string text)
            => Regex.IsMatch(
                text,
                @"\[\[\s*(?:M2\|[A-Za-z0-9_-]{8}\|\d+|MZ1\|[A-Za-z0-9_-]+\|\d+|\d+)\s*\]\]|🔹\s*\d+\s*🔹|(?m)^\s*(?:Batch|BatchId|Batch ID)\s*:|(?m)^\s*\d+\s*»|(?m)^\s*\d+\s*(?:\.(?!\d)|\)|:)",
                RegexOptions.CultureInvariant);

        private void TranslationBatchEdited(object? sender, EventArgs e)
        {
            if (_translationBatchLines.Count == 0)
                return;

            TranslationBatchImportResult result = new(0, 0, 0);
            _suppressTranslationEntryUpdates = true;
            try
            {
                // Ручной ввод всегда является явным намерением заменить текущее значение.
                var manifest = TranslationBatchDocument.BuildManifest(_translationBatchLines, deduplicate: false);
                var plan = TranslationBatchDocument.Analyze(
                    TranslationBatchDocument.StripDisplayLineNumbers(TranslationBatchText.Text),
                    manifest,
                    overwriteExisting: true,
                    _translationLocale ?? CurrentLocale);
                result = TranslationBatchDocument.Apply(plan);
            }
            finally
            {
                _suppressTranslationEntryUpdates = false;
            }

            if (!result.Success)
            {
                StatusText.Text = result.Error;
                return;
            }

            _translationView?.Refresh();
            UpdateTranslationStats();
            StatusText.Text = $"Ручной перевод обновлён: {result.ChangedEntries} записей.";
        }

        private void ClearVisible_Click(object sender, RoutedEventArgs e)
        {
            var entries = GetEditableVisibleTranslationEntries().Where(entry => !entry.IsMissing).ToList();
            if (entries.Count > 0 && MessageBox.Show($"Очистить перевод в {entries.Count} видимых записях?", "Очистка перевода", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            RunTranslationBulkUpdate(() =>
            {
                foreach (var entry in entries)
                    entry.Translation = string.Empty;
            });
            StatusText.Text = entries.Count == 0
                ? "Нет видимых переводов для очистки."
                : $"Очищено видимых переводов: {entries.Count}. Lua и технические записи защищены.";
        }

        private List<TranslationEntry> GetFindMatches()
        {
            var query = TranslationFindBox.Text;
            if (string.IsNullOrWhiteSpace(query))
                return new List<TranslationEntry>();

            return GetVisibleTranslationEntries()
                .Where(entry => entry.Key.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                entry.SourceText.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                entry.Translation.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
        }

        private void UpdateFindMatchCount()
        {
            var count = GetFindMatches().Count;
            TranslationMatchText.Text = $"совпадений: {count}";
        }

        private void FindNext_Click(object sender, RoutedEventArgs e) => SelectFindMatch(forward: true);
        private void FindPrevious_Click(object sender, RoutedEventArgs e) => SelectFindMatch(forward: false);

        private void SelectFindMatch(bool forward)
        {
            var matches = GetFindMatches();
            UpdateFindMatchCount();
            if (matches.Count == 0)
            {
                StatusText.Text = "В видимых строках совпадений не найдено.";
                return;
            }

            var current = TranslationGrid.SelectedItem as TranslationEntry;
            var index = current == null ? -1 : matches.IndexOf(current);
            index = forward
                ? (index + 1) % matches.Count
                : (index <= 0 ? matches.Count : index) - 1;
            TranslationGrid.SelectedItem = matches[index];
            TranslationGrid.ScrollIntoView(matches[index]);
            TranslationGrid.Focus();
            StatusText.Text = $"Совпадение {index + 1} из {matches.Count}: {matches[index].Key}";
        }

        private void ReplaceSelected_Click(object sender, RoutedEventArgs e)
        {
            var query = TranslationFindBox.Text;
            if (string.IsNullOrEmpty(query))
                return;

            var replacement = TranslationReplaceBox.Text;
            var changed = 0;
            foreach (var entry in TranslationGrid.SelectedItems.OfType<TranslationEntry>())
            {
                var updated = entry.Translation.Replace(query, replacement, StringComparison.CurrentCultureIgnoreCase);
                if (updated == entry.Translation)
                    continue;
                entry.Translation = updated;
                changed++;
            }

            StatusText.Text = $"Замена выполнена в выбранных строках: {changed}.";
            RefreshBatchWorkspace();
        }

        private void ReplaceAll_Click(object sender, RoutedEventArgs e)
        {
            var query = TranslationFindBox.Text;
            if (string.IsNullOrEmpty(query))
                return;

            var replacement = TranslationReplaceBox.Text;
            var entries = GetEditableVisibleTranslationEntries()
                .Where(entry => entry.Translation.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
            if (entries.Count > 0 && MessageBox.Show($"Заменить текст в {entries.Count} видимых записях?", "Заменить всё", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var changed = 0;
            RunTranslationBulkUpdate(() =>
            {
                foreach (var entry in entries)
                {
                    var updated = entry.Translation.Replace(query, replacement, StringComparison.CurrentCultureIgnoreCase);
                    if (updated == entry.Translation)
                        continue;
                    entry.Translation = updated;
                    changed++;
                }
            });

            StatusText.Text = $"Замена выполнена во всех видимых строках: {changed}.";
        }

        private void UndoSelected_Click(object sender, RoutedEventArgs e)
        {
            var entries = TranslationGrid.SelectedItems.OfType<TranslationEntry>().ToList();
            if (entries.Count == 0)
            {
                var last = _translationEntries.LastOrDefault(entry => entry.CanUndo);
                if (last != null)
                    entries.Add(last);
            }

            var restored = entries.Count(entry => entry.Undo());
            StatusText.Text = restored == 0
                ? "Нет изменений перевода для отмены."
                : $"Отменено последних изменений в строках: {restored}.";
            UpdateTranslationStats();
            RefreshBatchWorkspace();
        }

        private async void AiTranslateSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = TranslationGrid.SelectedItems
                .OfType<TranslationEntry>()
                .Where(entry => !string.IsNullOrWhiteSpace(entry.SourceText))
                .ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(
                    "Сначала выберите одну или несколько строк.",
                    "Перевод Ollama",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            await RunTranslationQueueAsync(selected, AiOverwriteExistingBox.IsChecked == true);
        }

        private async void AiTranslateMissing_Click(object sender, RoutedEventArgs e)
        {
            var targets = GetVisibleTranslationEntries()
                .Where(entry => !string.IsNullOrWhiteSpace(entry.SourceText) &&
                                (AiOverwriteExistingBox.IsChecked == true || entry.IsMissing))
                .ToList();
            await RunTranslationQueueAsync(targets, AiOverwriteExistingBox.IsChecked == true);
        }

        private async void AiRetry_Click(object sender, RoutedEventArgs e)
        {
            var retryKeys = _translationQueue.State.Errors
                .Where(error => error.Kind is TranslationErrorKind.Transient or TranslationErrorKind.ProtectedTokenMismatch)
                .Select(error => error.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var targets = _translationEntries.Where(entry => retryKeys.Contains(entry.Key)).ToList();
            if (targets.Count == 0)
            {
                StatusText.Text = "Нет ошибок ИИ-перевода, которые можно повторить.";
                return;
            }

            _translationQueue.State.RemoveErrors(retryKeys);
            await RunTranslationQueueAsync(targets, overwriteExisting: true, clearErrors: false);
        }

        private void AiCancel_Click(object sender, RoutedEventArgs e)
        {
            _translationQueue.Cancel();
            UpdateTranslationQueueUi();
        }

        private async Task RunTranslationQueueAsync(
            IReadOnlyList<TranslationEntry> targets,
            bool overwriteExisting,
            bool clearErrors = true)
        {
            if (_session == null || _translationQueue.State.IsActive)
                return;

            if (targets.Count == 0)
            {
                StatusText.Text = "По текущим фильтрам нет строк для ИИ-перевода.";
                return;
            }

            StatusText.Text = $"Ollama запущена: строк {targets.Count}, модель {OllamaTranslationService.DefaultModel}.";
            try
            {
                await _translationQueue.RunAsync(targets, overwriteExisting, clearErrors);
            }
            finally
            {
                string? checkpointWarning = null;
                if (_session != null && _translationLocale != null)
                {
                    try
                    {
                        await _translationCheckpoints.SaveAsync(
                            _session.SourcePath,
                            _translationLocale,
                            _translationEntries);
                    }
                    catch (Exception ex)
                    {
                        checkpointWarning = $" Контрольная точка не сохранена: {ex.Message}";
                    }
                }
                UpdateTranslationStats();
                RefreshBatchWorkspace();
                UpdateTranslationQueueUi();
                var state = _translationQueue.State;
                StatusText.Text = state.Status == TranslationQueueStatus.Cancelled
                    ? $"ИИ-перевод остановлен на {state.Processed}/{state.Total}; готовые результаты сохранены."
                    : $"ИИ-перевод завершён: переведено {state.Succeeded}, пропущено {state.Skipped}, ошибок {state.Errors.Count}. Проверьте и примените перевод.";
                StatusText.Text += checkpointWarning;
            }
        }

        private void UpdateTranslationQueueUi()
        {
            if (!IsInitialized)
                return;

            var state = _translationQueue.State;
            var active = state.IsActive;
            AiProgressBar.Value = state.ProgressPercent;
            AiProgressText.Text = active
                ? $"{state.Status}: {state.Processed}/{state.Total} · переведено {state.Succeeded} · пропущено {state.Skipped} · ошибок {state.Errors.Count}"
                : state.Status == TranslationQueueStatus.Idle
                    ? "Очередь ИИ готова"
                    : $"{state.Status}: {state.Processed}/{state.Total} · переведено {state.Succeeded} · пропущено {state.Skipped} · ошибок {state.Errors.Count}";
            AiTranslateSelectedButton.IsEnabled = _session != null && !active;
            AiTranslateMissingButton.IsEnabled = _session != null && !active;
            AiOverwriteExistingBox.IsEnabled = _session != null && !active;
            AiCancelButton.IsEnabled = active && state.Status == TranslationQueueStatus.Running;
            AiRetryButton.IsEnabled = _session != null && !active && state.Errors.Any(error =>
                error.Kind is TranslationErrorKind.Transient or TranslationErrorKind.ProtectedTokenMismatch);
            LocaleCombo.IsEnabled = _session != null && !active;
        }

        private void ReloadTranslations_Click(object sender, RoutedEventArgs e)
        {
            if (_translationEntries.Any(entry => entry.IsDirty) &&
                MessageBox.Show(
                    "Отменить неприменённые изменения перевода?",
                    "Перезагрузка переводов",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            LoadTranslationWorkspace();
        }

        private void UpdateTranslationStats()
        {
            if (_translationWorkspaceSnapshot is { } snapshot)
            {
                TranslationWorkspaceView.SetCorpusMetrics(snapshot.Metrics);
                return;
            }

            TranslationWorkspaceView.SetSummary($"Ключей: {_translationEntries.Count}. Показатели пересчитываются…");
        }

        private void LocaleUp_Click(object sender, RoutedEventArgs e)
        {
            if (LocaleCombo.Items.Count == 0) return;
            LocaleCombo.SelectedIndex = Math.Max(0, LocaleCombo.SelectedIndex - 1);
        }

        private void LocaleDown_Click(object sender, RoutedEventArgs e)
        {
            if (LocaleCombo.Items.Count == 0) return;
            LocaleCombo.SelectedIndex = Math.Min(LocaleCombo.Items.Count - 1, LocaleCombo.SelectedIndex + 1);
        }

        private void AddLocale_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            var locale = LocaleNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(locale))
                locale = Interaction.InputBox("Locale name:", "Add locale", "RU").Trim();
            if (string.IsNullOrWhiteSpace(locale)) return;

            _session.Localization.AddLocale(locale);
            LoadLocales();
            LocaleCombo.SelectedItem = locale;
            _sessionState.MarkDirty();
            StatusText.Text = $"Locale added: {locale}";
        }

        private void DeleteLocale_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            var locale = CurrentLocale;
            if (locale.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("DEFAULT locale cannot be deleted.", "Delete locale", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MessageBox.Show($"Delete locale '{locale}'?", "Delete locale", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            _session.Localization.DeleteLocale(locale);
            LoadLocales();
            LoadBriefingFields();
            _sessionState.MarkDirty();
            StatusText.Text = $"Locale deleted: {locale}";
        }

        private void UpdateLocaleIndex()
        {
            LocaleIndexBox.Text = LocaleCombo.SelectedIndex >= 0 ? (LocaleCombo.SelectedIndex + 1).ToString() : "";
        }

        private void LoadBriefingFields()
        {
            if (_session?.Mission == null) return;

            _isLoadingBriefingFields = true;
            try
            {
                MissionNameBox.Text = FirstNonEmpty(
                    _session.Localization.ResolveBriefingString(_session.Mission, "name", CurrentLocale),
                    _session.Localization.ResolveBriefingString(_session.Mission, "sortie", CurrentLocale));
                MissionDescBox.Text = _session.Localization.ResolveBriefingString(_session.Mission, "descriptionText", CurrentLocale);
                RedTaskBox.Text = _session.Localization.ResolveBriefingString(_session.Mission, "descriptionRedTask", CurrentLocale);
                BlueTaskBox.Text = _session.Localization.ResolveBriefingString(_session.Mission, "descriptionBlueTask", CurrentLocale);
            }
            finally
            {
                _isLoadingBriefingFields = false;
            }

            LoadPictures();
            LoadKneeboard();
            LoadAudio();
            LoadTriggers();
            LoadRadio();
            LoadTriggerPic();
            LoadScripts();
        }

        private void LoadPictures()
        {
            _pictureThumbnailLoads?.Cancel();
            _pictureThumbnailLoads?.Dispose();
            _pictureThumbnailLoads = new CancellationTokenSource();
            PicturesList.Items.Clear();
            PictureViewer.Source = null;
            if (_session?.Mission == null) return;

            foreach (var pic in _session.Mission.GetPictureFileNames())
            {
                var display = FormatResourceDisplayWithFallback(pic);
                var path = ResolveMissionFile(ResourceTokenFromDisplay(display));
                if (path == null)
                    continue;
                var locale = display.StartsWith("[DEFAULT]", StringComparison.OrdinalIgnoreCase) ? "DEFAULT" : CurrentLocale;
                var item = new MediaListItem(display, path, ResourceTokenFromDisplay(display), locale);
                PicturesList.Items.Add(item);
                _ = item.LoadThumbnailAsync(_pictureThumbnailLoads.Token);
            }

            if (PicturesList.Items.Count > 0)
                PicturesList.SelectedIndex = 0;
        }

        private void PicturesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            PictureViewer.Source = null;
            if (_session == null || PicturesList.SelectedItem is not MediaListItem selected) return;
            if (File.Exists(selected.FullPath))
                DisplayImage(PictureViewer, selected.FullPath);
        }

        private void AddPicture_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            var dlg = new OpenFileDialog
            {
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*",
                Multiselect = true,
                Title = "Add briefing picture"
            };

            if (dlg.ShowDialog() != true)
                return;

            foreach (var fileName in dlg.FileNames)
            {
                var resource = _session.Localization.AddResourceFile(CurrentLocale, fileName, LocalizationEngine.ResourceKind.Picture);
                _session.Mission.AddBriefingPicture(resource.Key);
            }

            LoadPictures();
            _session.MarkMissionDirty();
            _sessionState.MarkDirty();
            StatusText.Text = "Briefing picture added. Save the mission to write it into .miz.";
        }

        private void RemovePicture_Click(object sender, RoutedEventArgs e)
        {
            if (_session?.Mission == null || PicturesList.SelectedItem is not MediaListItem selected) return;

            _session.Mission.RemovePicture(selected.Token);
            LoadPictures();
            _session.MarkMissionDirty();
            _sessionState.MarkDirty();
            StatusText.Text = "Briefing picture link removed.";
        }

        private void ReplacePicture_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null || PicturesList.SelectedItem is not MediaListItem selected) return;

            var key = selected.Token;
            if (!key.StartsWith("ResKey_", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Select a picture that is registered in mapResource first.", "Replace picture", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var locale = selected.Locale;
            var dlg = new OpenFileDialog
            {
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*",
                Title = "Replace briefing picture"
            };

            if (dlg.ShowDialog() != true)
                return;

            _session.Localization.ReplaceResourceFile(locale, key, dlg.FileName);
            LoadPictures();
            _sessionState.MarkDirty();
            StatusText.Text = $"Picture resource replaced: {key}";
        }

        private void LoadKneeboard()
        {
            _kneeboardThumbnailLoads?.Cancel();
            _kneeboardThumbnailLoads?.Dispose();
            _kneeboardThumbnailLoads = new CancellationTokenSource();
            KneeboardList.Items.Clear();
            KneeboardViewer.Source = null;
            if (_session?.Archive == null) return;

            var root = Path.Combine(_session.Archive.WorkDir, "KNEEBOARD");
            if (!Directory.Exists(root)) return;

            foreach (var file in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                         .Where(IsImageFile)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(_session.Archive.WorkDir, file);
                var item = new MediaListItem(relative, file, relative);
                KneeboardList.Items.Add(item);
                _ = item.LoadThumbnailAsync(_kneeboardThumbnailLoads.Token);
            }

            if (KneeboardList.Items.Count > 0)
                KneeboardList.SelectedIndex = 0;
        }

        private void KneeboardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            KneeboardViewer.Source = null;
            if (_session == null || KneeboardList.SelectedItem is not MediaListItem selected) return;
            if (File.Exists(selected.FullPath))
                DisplayImage(KneeboardViewer, selected.FullPath);
        }

        private void AddKneeboard_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            var dlg = new OpenFileDialog
            {
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*",
                Multiselect = true,
                Title = "Add kneeboard image"
            };

            if (dlg.ShowDialog() != true)
                return;

            var targetDir = Path.Combine(_session.Archive.WorkDir, "KNEEBOARD", "IMAGES");
            Directory.CreateDirectory(targetDir);

            foreach (var source in dlg.FileNames)
            {
                var fileName = MakeUniqueFileName(targetDir, Path.GetFileName(source));
                File.Copy(source, Path.Combine(targetDir, fileName));
            }

            LoadKneeboard();
            _sessionState.MarkDirty();
            StatusText.Text = "Kneeboard image added.";
        }

        private void RemoveKneeboard_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null || KneeboardList.SelectedItem is not MediaListItem selected) return;

            var path = selected.FullPath;
            if (!File.Exists(path)) return;
            if (MessageBox.Show($"Delete '{selected.DisplayText}'?", "Remove kneeboard", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            KneeboardViewer.Source = null;
            File.Delete(path);
            LoadKneeboard();
            _sessionState.MarkDirty();
            StatusText.Text = "Kneeboard image removed.";
        }

        private void ReplaceKneeboard_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null || KneeboardList.SelectedItem is not MediaListItem selected) return;
            if (!File.Exists(selected.FullPath)) return;

            var dlg = new OpenFileDialog
            {
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*",
                Title = "Replace kneeboard image"
            };
            if (dlg.ShowDialog() != true)
                return;

            var originalSize = ImageReplacement.ReadSize(selected.FullPath);
            ImageReplacement.CopyNormalized(dlg.FileName, selected.FullPath);
            LoadKneeboard();
            _sessionState.MarkDirty();
            StatusText.Text = $"Kneeboard image replaced; filename and size {originalSize.Width}x{originalSize.Height} preserved.";
        }

        private void LoadAudio()
        {
            AudioList.Items.Clear();
            _selectedAudioPath = null;
            if (_session == null) return;

            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mapResource = _session.Localization.LoadMapResource(CurrentLocale);

            AddMapAudioFiles(mapResource, "", added);

            if (!CurrentLocale.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
                AddMapAudioFiles(_session.Localization.LoadMapResource("DEFAULT"), "[DEFAULT] ", added);

            AddPhysicalAudioFiles(CurrentLocale, added, prefix: "");

            if (AudioList.Items.Count == 0 && !CurrentLocale.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
                AddPhysicalAudioFiles("DEFAULT", added, prefix: "[DEFAULT] ");
        }

        private void AddPhysicalAudioFiles(string locale, HashSet<string> added, string prefix)
        {
            if (_session == null) return;

            var audioDir = Path.Combine(_session.Archive.WorkDir, "l10n", locale);
            if (!Directory.Exists(audioDir)) return;

            foreach (var file in Directory.GetFiles(audioDir)
                         .Where(IsAudioFile)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                if (added.Add(name))
                    AudioList.Items.Add(prefix + name);
            }
        }

        private void AddMapAudioFiles(Dictionary<string, string> mapResource, string prefix, HashSet<string> added)
        {
            foreach (var kv in mapResource.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (IsAudioName(kv.Value) && added.Add(kv.Value))
                    AudioList.Items.Add($"{prefix}{kv.Key} -> {kv.Value}");
            }
        }

        private void AudioList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedAudioPath = null;
            if (_session == null || AudioList.SelectedItem is not string selected) return;

            _selectedAudioPath = ResolveMissionFile(ResourceTokenFromDisplay(selected), selected.StartsWith("[DEFAULT]", StringComparison.OrdinalIgnoreCase) ? "DEFAULT" : CurrentLocale);
            StatusText.Text = _selectedAudioPath == null ? "Audio file not found." : $"Selected audio: {_selectedAudioPath}";
        }

        private void PlayAudio_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAudioPath == null || !File.Exists(_selectedAudioPath))
            {
                MessageBox.Show("Select an audio file first.", "Audio", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                StopAudio();
                _audioReader = Path.GetExtension(_selectedAudioPath).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
                    ? new VorbisWaveReader(_selectedAudioPath)
                    : new AudioFileReader(_selectedAudioPath);
                _waveOut = new WaveOutEvent();
                _waveOut.Init(_audioReader);
                _waveOut.Play();
                StatusText.Text = "Playing audio.";
            }
            catch (Exception ex)
            {
                StopAudio();
                MessageBox.Show(ex.Message, "Audio error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StopAudio_Click(object sender, RoutedEventArgs e) => StopAudio();

        private void AddAudio_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            var dlg = new OpenFileDialog
            {
                Filter = "Audio|*.ogg;*.wav;*.mp3|All files|*.*",
                Multiselect = true,
                Title = "Add audio resource"
            };

            if (dlg.ShowDialog() != true)
                return;

            foreach (var fileName in dlg.FileNames)
                _session.Localization.AddResourceFile(CurrentLocale, fileName, LocalizationEngine.ResourceKind.Audio);

            LoadAudio();
            _sessionState.MarkDirty();
            StatusText.Text = "Audio resource added.";
        }

        private void RemoveAudio_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null || AudioList.SelectedItem is not string selected) return;
            if (MessageBox.Show($"Remove '{selected}'?", "Remove audio", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            StopAudio();
            var token = ResourceTokenFromDisplay(selected);
            if (token.StartsWith("ResKey_", StringComparison.OrdinalIgnoreCase))
            {
                _session.Localization.RemoveResource(CurrentLocale, token, deletePhysicalFile: true);
            }
            else
            {
                var path = ResolveMissionFile(token, selected.StartsWith("[DEFAULT]", StringComparison.OrdinalIgnoreCase) ? "DEFAULT" : CurrentLocale);
                if (path != null && File.Exists(path))
                    File.Delete(path);
            }

            LoadAudio();
            _sessionState.MarkDirty();
            StatusText.Text = "Audio resource removed.";
        }

        private void ReplaceAudio_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null || AudioList.SelectedItem is not string selected) return;

            var key = ResourceTokenFromDisplay(selected);
            if (!key.StartsWith("ResKey_", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Select an audio file that is registered in mapResource first.", "Replace audio", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var locale = selected.StartsWith("[DEFAULT]", StringComparison.OrdinalIgnoreCase) ? "DEFAULT" : CurrentLocale;
            var dlg = new OpenFileDialog
            {
                Filter = "Audio|*.ogg;*.wav;*.mp3|All files|*.*",
                Title = "Replace audio resource"
            };

            if (dlg.ShowDialog() != true)
                return;

            StopAudio();
            _session.Localization.ReplaceResourceFile(locale, key, dlg.FileName);
            LoadAudio();
            _sessionState.MarkDirty();
            StatusText.Text = $"Audio resource replaced: {key}";
        }

        private void AddAudioTrigger_Click(object sender, RoutedEventArgs e)
        {
            if (_session?.Mission == null || AudioList.SelectedItem is not string selected) return;

            var key = ResourceTokenFromDisplay(selected);
            if (!key.StartsWith("ResKey_", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Select an audio file that is registered in mapResource first.", "Audio trigger", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var escapedKey = key.Replace("\"", "\\\"");
            var index = _session.Mission.AddSimpleTrigger(
                $"Play audio {key}",
                "return(true)",
                $"a_out_sound(getValueResourceByKey(\"{escapedKey}\"));",
                missionStart: true);

            LoadTriggers();
            _session.MarkMissionDirty();
            _sessionState.MarkDirty();
            StatusText.Text = $"Mission Start audio trigger added at index {index}.";
        }

        private void LoadTriggers()
        {
            TriggersList.Items.Clear();
            if (_session?.Mission == null) return;

            var classicTriggers = _session.Mission.GetTriggers();
            foreach (var trigger in classicTriggers)
                TriggersList.Items.Add(trigger);

            var taskActions = _session.Mission.GetTaskActions();
            if (taskActions.Count > 0)
            {
                if (classicTriggers.Count > 0)
                    TriggersList.Items.Add("");

                TriggersList.Items.Add($"Route/task actions: {taskActions.Count}");
                foreach (var action in taskActions)
                    TriggersList.Items.Add(action);
            }

            if (classicTriggers.Count == 0 && taskActions.Count == 0)
                TriggersList.Items.Add("No mission.trig or route task actions found.");
        }

        private void AddTrigger_Click(object sender, RoutedEventArgs e)
        {
            if (_session?.Mission == null) return;

            var comment = Interaction.InputBox("Trigger name/comment:", "Add trigger", "MizEdit trigger");
            if (string.IsNullOrWhiteSpace(comment)) return;

            var condition = Interaction.InputBox("Lua condition. Examples: return(true), return(c_flag_is_true(\"FLAG\"))", "Add trigger", "return(true)");
            if (string.IsNullOrWhiteSpace(condition)) return;

            var action = Interaction.InputBox("DCS action Lua. Example: a_out_text_delay(\"Hello\", 10, false, 0);", "Add trigger", "a_out_text_delay(\"MizEdit trigger\", 10, false, 0);");
            if (string.IsNullOrWhiteSpace(action)) return;

            var missionStart = MessageBox.Show("Run as Mission Start trigger?", "Add trigger", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            var index = _session.Mission.AddSimpleTrigger(comment, condition, action, missionStart);
            LoadTriggers();
            _session.MarkMissionDirty();
            _sessionState.MarkDirty();
            StatusText.Text = $"Trigger added at index {index}.";
        }

        private void RemoveTrigger_Click(object sender, RoutedEventArgs e)
        {
            if (_session?.Mission == null || TriggersList.SelectedItem is not string selected) return;

            var match = Regex.Match(selected, @"^\[(\d+)\]");
            if (!match.Success) return;

            var index = int.Parse(match.Groups[1].Value);
            if (MessageBox.Show($"Remove trigger [{index}]?", "Remove trigger", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            _session.Mission.RemoveTrigger(index);
            LoadTriggers();
            _session.MarkMissionDirty();
            _sessionState.MarkDirty();
            StatusText.Text = $"Trigger removed: {index}";
        }

        private void LoadRadio()
        {
            RadioList.Items.Clear();
            _radioTransmissions.Clear();
            _selectedRadioMessage = null;
            ClearRadioEditor();

            if (_session?.Mission == null) return;

            _radioTransmissions.AddRange(_session.Mission.GetRadioTransmissions());
            foreach (var message in _radioTransmissions)
                RadioList.Items.Add(message.DisplayText);

            if (RadioList.Items.Count > 0)
                RadioList.SelectedIndex = 0;
        }

        private void RadioList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ClearRadioEditor();
            if (_session == null || RadioList.SelectedIndex < 0 || RadioList.SelectedIndex >= _radioTransmissions.Count)
                return;

            _selectedRadioMessage = _radioTransmissions[RadioList.SelectedIndex];
            RadioSubtitleKeyBox.Text = _selectedRadioMessage.SubtitleKey;
            RadioFileKeyBox.Text = _selectedRadioMessage.FileKey;
            RadioDurationBox.Text = _selectedRadioMessage.Duration.ToString();

            if (_selectedRadioMessage.SubtitleKey.StartsWith("DictKey_", StringComparison.OrdinalIgnoreCase))
            {
                RadioOriginalSubtitleTextBox.Text = _session.Localization.GetDictionaryValue("DEFAULT", _selectedRadioMessage.SubtitleKey) ?? "";
                RadioSubtitleTextBox.Text = _session.Localization.GetDictionaryValue(CurrentLocale, _selectedRadioMessage.SubtitleKey) ?? "";
            }
            else
            {
                RadioOriginalSubtitleTextBox.Text = _selectedRadioMessage.SubtitleKey;
                RadioSubtitleTextBox.Text = _selectedRadioMessage.SubtitleKey;
            }
        }

        private void RefreshRadio_Click(object sender, RoutedEventArgs e) => LoadRadio();

        private void SaveRadioSubtitle_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null || _selectedRadioMessage == null) return;

            if (!_selectedRadioMessage.SubtitleKey.StartsWith("DictKey_", StringComparison.OrdinalIgnoreCase))
            {
                RadioStatusText.Text = "Subtitle is not a DictKey.";
                return;
            }

            ApplySelectedRadioSubtitle();
            _sessionState.MarkDirty();
            RadioStatusText.Text = "Saved in dictionary.";
            StatusText.Text = "Radio subtitle updated.";
        }

        private void LoadTriggerPic()
        {
            _triggerPictureThumbnailLoads?.Cancel();
            _triggerPictureThumbnailLoads?.Dispose();
            _triggerPictureThumbnailLoads = new CancellationTokenSource();
            TriggerPicsList.Items.Clear();
            if (_session?.Mission == null) return;

            foreach (var pic in _session.Mission.GetTriggerPictures())
            {
                var display = FormatResourceDisplayWithFallback(pic);
                var path = ResolveMissionFile(ResourceTokenFromDisplay(display));
                if (path == null)
                    continue;
                var locale = display.StartsWith("[DEFAULT]", StringComparison.OrdinalIgnoreCase) ? "DEFAULT" : CurrentLocale;
                var item = new MediaListItem(display, path, ResourceTokenFromDisplay(display), locale);
                TriggerPicsList.Items.Add(item);
                _ = item.LoadThumbnailAsync(_triggerPictureThumbnailLoads.Token);
            }
        }

        private void AddTriggerPic_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            var dlg = new OpenFileDialog
            {
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*",
                Multiselect = true,
                Title = "Add trigger picture"
            };

            if (dlg.ShowDialog() != true)
                return;

            foreach (var fileName in dlg.FileNames)
            {
                var resource = _session.Localization.AddResourceFile(CurrentLocale, fileName, LocalizationEngine.ResourceKind.Picture);
                _session.Mission.AddTriggerPicture(resource.Key);
            }

            LoadTriggerPic();
            _session.MarkMissionDirty();
            _sessionState.MarkDirty();
            StatusText.Text = "Trigger picture added.";
        }

        private void RemoveTriggerPic_Click(object sender, RoutedEventArgs e)
        {
            if (_session?.Mission == null || TriggerPicsList.SelectedItem is not MediaListItem selected) return;

            _session.Mission.RemoveTriggerPicture(selected.Token);
            LoadTriggerPic();
            _session.MarkMissionDirty();
            _sessionState.MarkDirty();
            StatusText.Text = "Trigger picture link removed.";
        }

        private void ReplaceTriggerPic_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null || TriggerPicsList.SelectedItem is not MediaListItem selected) return;

            var key = selected.Token;
            if (!key.StartsWith("ResKey_", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Select a trigger picture registered in mapResource first.", "Replace trigger picture", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new OpenFileDialog
            {
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*",
                Title = "Replace trigger picture"
            };
            if (dlg.ShowDialog() != true)
                return;

            _session.Localization.ReplaceResourceFile(selected.Locale, key, dlg.FileName);
            LoadTriggerPic();
            _sessionState.MarkDirty();
            StatusText.Text = $"Trigger picture replaced; resource key and filename preserved: {key}";
        }

        private void LoadScripts()
        {
            ScriptCombo.Items.Clear();
            ScriptBox.Clear();
            ScriptStatusText.Text = "";
            _selectedScriptPath = null;
            _selectedScriptOriginalText = null;
            if (_session == null) return;

            var addedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mapResource = _session.Localization.LoadMapResource(CurrentLocale);

            AddMapScriptFiles(mapResource, "", addedFiles);

            if (!CurrentLocale.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
                AddMapScriptFiles(_session.Localization.LoadMapResource("DEFAULT"), "[DEFAULT] ", addedFiles);

            var localeDir = Path.Combine(_session.Archive.WorkDir, "l10n", CurrentLocale);
            if (Directory.Exists(localeDir))
            {
                foreach (var file in Directory.GetFiles(localeDir, "*.lua").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(file);
                    if (addedFiles.Add(name))
                        ScriptCombo.Items.Add(name);
                }
            }

            if (ScriptCombo.Items.Count > 0)
                ScriptCombo.SelectedIndex = 0;
        }

        private void AddMapScriptFiles(Dictionary<string, string> mapResource, string prefix, HashSet<string> addedFiles)
        {
            foreach (var kv in mapResource.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetExtension(kv.Value).Equals(".lua", StringComparison.OrdinalIgnoreCase) && addedFiles.Add(kv.Value))
                    ScriptCombo.Items.Add($"{prefix}{kv.Key} -> {kv.Value}");
            }
        }

        private void ScriptCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ScriptBox.Clear();
            ScriptStatusText.Text = "";
            _selectedScriptPath = null;
            _selectedScriptOriginalText = null;
            if (_session == null || ScriptCombo.SelectedItem is not string selected) return;

            var token = ResourceTokenFromDisplay(selected);
            var path = ResolveMissionFile(token);
            if (path == null || !File.Exists(path))
            {
                ScriptStatusText.Text = "Script file not found.";
                return;
            }

            _selectedScriptPath = path;
            ScriptBox.Text = File.ReadAllText(path);
            _selectedScriptOriginalText = ScriptBox.Text;
            ScriptStatusText.Text = Path.GetFileName(path);
        }

        private void ReloadScripts_Click(object sender, RoutedEventArgs e) => LoadScripts();

        private void SaveScript_Click(object sender, RoutedEventArgs e)
        {
            SaveSelectedScriptInWorkDir();
            _sessionState.MarkDirty();
            ScriptStatusText.Text = "Script saved.";
            StatusText.Text = "Script saved in mission work directory.";
        }

        private void AddScript_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            var dlg = new OpenFileDialog
            {
                Filter = "Lua scripts|*.lua|All files|*.*",
                Multiselect = true,
                Title = "Add Lua script resource"
            };

            if (dlg.ShowDialog() != true)
                return;

            string? lastKey = null;
            foreach (var fileName in dlg.FileNames)
            {
                var resource = _session.Localization.AddResourceFile(CurrentLocale, fileName, LocalizationEngine.ResourceKind.Script);
                lastKey = resource.Key;
            }

            LoadScripts();
            SelectComboItemStartingWith(ScriptCombo, lastKey);
            _sessionState.MarkDirty();
            StatusText.Text = "Script resource added.";
        }

        private void RemoveScript_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null || ScriptCombo.SelectedItem is not string selected) return;
            if (MessageBox.Show($"Remove '{selected}'?", "Remove script", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            var token = ResourceTokenFromDisplay(selected);
            if (token.StartsWith("ResKey_", StringComparison.OrdinalIgnoreCase))
                _session.Localization.RemoveResource(CurrentLocale, token, deletePhysicalFile: true);
            else
            {
                var path = ResolveMissionFile(token);
                if (path != null && File.Exists(path))
                    File.Delete(path);
            }

            LoadScripts();
            _sessionState.MarkDirty();
            StatusText.Text = "Script removed.";
        }

        private void AddScriptTrigger_Click(object sender, RoutedEventArgs e)
        {
            if (_session?.Mission == null || ScriptCombo.SelectedItem is not string selected) return;

            var key = ResourceTokenFromDisplay(selected);
            if (!key.StartsWith("ResKey_", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Select a script that is registered in mapResource first.", "Script trigger", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var escapedKey = key.Replace("\"", "\\\"");
            var index = _session.Mission.AddSimpleTrigger(
                $"Run script {key}",
                "return(true)",
                $"a_do_script_file(getValueResourceByKey(\"{escapedKey}\"));",
                missionStart: true);

            LoadTriggers();
            _session.MarkMissionDirty();
            _sessionState.MarkDirty();
            StatusText.Text = $"Mission Start script trigger added at index {index}.";
        }

        private void StopAudio()
        {
            try
            {
                _waveOut?.Stop();
            }
            finally
            {
                _waveOut?.Dispose();
                _audioReader?.Dispose();
                _waveOut = null;
                _audioReader = null;
            }
        }

        private void CloseSession()
        {
            StopAudio();
            _translationWorkspaceRefresh?.Cancel();
            _translationWorkspaceRefresh?.Dispose();
            _translationWorkspaceRefresh = null;
            _translationWorkspaceModel.InvalidateAll();
            _translationWorkspaceSnapshot = null;
            _translationLocale = null;
            _translationEntries.Clear();
            _translationBaselines.Clear();
            _session?.Dispose();
            _session = null;
            _sessionState.Reset();
            UpdateSessionStateIndicator();
        }

        private string? ResolveMissionFile(string token, string? locale = null)
        {
            if (_session == null || string.IsNullOrWhiteSpace(token))
                return null;

            locale ??= CurrentLocale;
            token = token.Trim();
            if (token.StartsWith("[DEFAULT]", StringComparison.OrdinalIgnoreCase))
            {
                token = token["[DEFAULT]".Length..].Trim();
                locale = "DEFAULT";
            }

            var resolved = _session.Localization.ResolveResourceFile(locale, token);
            if (resolved != null && File.Exists(resolved))
                return resolved;

            var direct = Path.Combine(_session.Archive.WorkDir, token);
            if (File.Exists(direct))
                return direct;

            return null;
        }

        private static void DisplayImage(Image target, string path)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            target.Source = bitmap;
        }

        private void ClearRadioEditor()
        {
            RadioSubtitleKeyBox.Text = "";
            RadioFileKeyBox.Text = "";
            RadioDurationBox.Text = "";
            RadioOriginalSubtitleTextBox.Text = "";
            RadioSubtitleTextBox.Text = "";
            RadioStatusText.Text = "";
        }

        private string BuildMissionSummary()
        {
            if (_session?.Mission == null) return "";

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(_session.Mission.Theatre))
                parts.Add($"Theatre: {_session.Mission.Theatre}");
            if (!string.IsNullOrWhiteSpace(_session.Mission.MizId))
                parts.Add($"Miz ID: {_session.Mission.MizId}");
            if (!string.IsNullOrWhiteSpace(_session.Mission.ExtLoaderLibrary))
                parts.Add($"Ext loader: {_session.Mission.ExtLoaderLibrary}");
            parts.Add($"Work dir: {_session.Archive.WorkDir}");
            return string.Join(Environment.NewLine, parts);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
        }

        private static string FormatResourceDisplay(string keyOrFileName, Dictionary<string, string> mapResource)
        {
            return mapResource.TryGetValue(keyOrFileName, out var fileName)
                ? $"{keyOrFileName} -> {fileName}"
                : keyOrFileName;
        }

        private string FormatResourceDisplayWithFallback(string keyOrFileName)
        {
            if (_session == null)
                return keyOrFileName;

            var localMap = _session.Localization.LoadMapResource(CurrentLocale);
            if (localMap.TryGetValue(keyOrFileName, out var localFile))
                return $"{keyOrFileName} -> {localFile}";

            if (!CurrentLocale.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
            {
                var defaultMap = _session.Localization.LoadMapResource("DEFAULT");
                if (defaultMap.TryGetValue(keyOrFileName, out var defaultFile))
                    return $"[DEFAULT] {keyOrFileName} -> {defaultFile}";
            }

            return keyOrFileName;
        }

        private static string ResourceTokenFromDisplay(string display)
        {
            var value = display.Trim();
            if (value.StartsWith("[DEFAULT]", StringComparison.OrdinalIgnoreCase))
                value = value["[DEFAULT]".Length..].Trim();

            var arrow = value.IndexOf("->", StringComparison.Ordinal);
            if (arrow >= 0)
                return value[..arrow].Trim();

            var unicodeArrow = value.IndexOf('→');
            if (unicodeArrow >= 0)
                return value[..unicodeArrow].Trim();

            return value;
        }

        private static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        private static bool IsAudioFile(string path) => IsAudioName(Path.GetFileName(path));

        private static bool IsAudioName(string fileName) => AudioExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

        private static string MakeUniqueFileName(string directory, string requestedFileName)
        {
            var stem = Path.GetFileNameWithoutExtension(requestedFileName);
            var ext = Path.GetExtension(requestedFileName);
            var candidate = requestedFileName;
            var index = 1;

            while (File.Exists(Path.Combine(directory, candidate)))
            {
                candidate = $"{stem}_{index}{ext}";
                index++;
            }

            return candidate;
        }

        private static void SelectComboItemStartingWith(ComboBox comboBox, string? prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix)) return;

            for (var i = 0; i < comboBox.Items.Count; i++)
            {
                if (comboBox.Items[i] is string item && item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedIndex = i;
                    return;
                }
            }
        }
    }
}
