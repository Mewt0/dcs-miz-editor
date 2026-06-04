using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Microsoft.VisualBasic;
using MizEdit.Core;
using MizEdit.Services;
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
        private MissionSession? _session;
        private WaveOutEvent? _waveOut;
        private WaveStream? _audioReader;
        private string? _selectedAudioPath;
        private string? _selectedScriptPath;
        private readonly List<MissionLua.RadioMessage> _radioTransmissions = new();
        private MissionLua.RadioMessage? _selectedRadioMessage;

        public MainWindow()
        {
            InitializeComponent();
            _batchService = new BatchService(_missionService);
            SetEnabled(false);
        }

        private string CurrentLocale => (string?)LocaleCombo.SelectedItem ?? "DEFAULT";

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Ready";
            SideStatusText.Text = "No file opened";
        }

        protected override void OnClosed(EventArgs e)
        {
            StopAudio();
            CloseSession();
            base.OnClosed(e);
        }

        private void SetEnabled(bool enabled)
        {
            LocaleCombo.IsEnabled = enabled;
            LocaleIndexBox.IsEnabled = enabled;
            LocaleNameBox.IsEnabled = enabled;
            MissionNameBox.IsEnabled = enabled;
            MissionDescBox.IsEnabled = enabled;
            RedTaskBox.IsEnabled = enabled;
            BlueTaskBox.IsEnabled = enabled;
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

            try
            {
                CloseSession();
                _session = _missionService.LoadMission(dlg.FileName);
                LoadLocales();
                LoadBriefingFields();
                SetEnabled(true);

                SideStatusText.Text = Path.GetFileName(dlg.FileName);
                MizIdText.Text = BuildMissionSummary();
                StatusText.Text = $"Loaded: {dlg.FileName} ({CurrentLocale})";
            }
            catch (Exception ex)
            {
                SetEnabled(false);
                SideStatusText.Text = "No file opened";
                MizIdText.Text = "";
                StatusText.Text = "Load error";
                MessageBox.Show(ex.Message, "Load error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            try
            {
                StopAudio();
                ApplyPendingUiChanges();
                _missionService.Save(_session);
                StatusText.Text = "Saved";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Save error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                _missionService.SaveAsMiz(_session, dlg.FileName);
                StatusText.Text = $"Saved as: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Save error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
            ApplyPendingUiChanges();
            StatusText.Text = "Applied in memory. Use Save or Save as Miz to write the .miz file.";
        }

        private void ApplyPendingUiChanges()
        {
            ApplyBriefingFieldsToSession();
            ApplySelectedRadioSubtitle();
            SaveSelectedScriptInWorkDir();
        }

        private void ApplyBriefingFieldsToSession()
        {
            if (_session?.Mission == null) return;

            _session.Localization.SetBriefingString(_session.Mission, "name", MissionNameBox.Text, CurrentLocale);
            _session.Localization.SetBriefingString(_session.Mission, "sortie", MissionNameBox.Text, CurrentLocale);
            _session.Localization.SetBriefingString(_session.Mission, "descriptionText", MissionDescBox.Text, CurrentLocale);
            _session.Localization.SetBriefingString(_session.Mission, "descriptionRedTask", RedTaskBox.Text, CurrentLocale);
            _session.Localization.SetBriefingString(_session.Mission, "descriptionBlueTask", BlueTaskBox.Text, CurrentLocale);
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

            File.WriteAllText(_selectedScriptPath, ScriptBox.Text);
        }

        private void LoadLocales()
        {
            if (_session == null) return;

            LocaleCombo.Items.Clear();
            foreach (var locale in _session.Localization.GetLocales())
                LocaleCombo.Items.Add(locale);

            LocaleCombo.SelectedItem = LocaleCombo.Items.Contains("DEFAULT")
                ? "DEFAULT"
                : LocaleCombo.Items.Cast<object>().FirstOrDefault();
            UpdateLocaleIndex();
        }

        private void LocaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateLocaleIndex();
            if (_session != null)
                LoadBriefingFields();
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
            StatusText.Text = $"Locale deleted: {locale}";
        }

        private void UpdateLocaleIndex()
        {
            LocaleIndexBox.Text = LocaleCombo.SelectedIndex >= 0 ? (LocaleCombo.SelectedIndex + 1).ToString() : "";
        }

        private void LoadBriefingFields()
        {
            if (_session?.Mission == null) return;

            MissionNameBox.Text = FirstNonEmpty(
                _session.Localization.ResolveBriefingString(_session.Mission, "name", CurrentLocale),
                _session.Localization.ResolveBriefingString(_session.Mission, "sortie", CurrentLocale));
            MissionDescBox.Text = _session.Localization.ResolveBriefingString(_session.Mission, "descriptionText", CurrentLocale);
            RedTaskBox.Text = _session.Localization.ResolveBriefingString(_session.Mission, "descriptionRedTask", CurrentLocale);
            BlueTaskBox.Text = _session.Localization.ResolveBriefingString(_session.Mission, "descriptionBlueTask", CurrentLocale);

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
            PicturesList.Items.Clear();
            PictureViewer.Source = null;
            if (_session?.Mission == null) return;

            foreach (var pic in _session.Mission.GetPictureFileNames())
                PicturesList.Items.Add(FormatResourceDisplayWithFallback(pic));
        }

        private void PicturesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            PictureViewer.Source = null;
            if (_session == null || PicturesList.SelectedItem is not string selected) return;

            var token = ResourceTokenFromDisplay(selected);
            var path = ResolveMissionFile(token);
            if (path != null)
                DisplayImage(PictureViewer, path);
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
            StatusText.Text = "Briefing picture added. Save the mission to write it into .miz.";
        }

        private void RemovePicture_Click(object sender, RoutedEventArgs e)
        {
            if (_session?.Mission == null || PicturesList.SelectedItem is not string selected) return;

            _session.Mission.RemovePicture(ResourceTokenFromDisplay(selected));
            LoadPictures();
            StatusText.Text = "Briefing picture link removed.";
        }

        private void ReplacePicture_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null || PicturesList.SelectedItem is not string selected) return;

            var key = ResourceTokenFromDisplay(selected);
            if (!key.StartsWith("ResKey_", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Select a picture that is registered in mapResource first.", "Replace picture", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var locale = selected.StartsWith("[DEFAULT]", StringComparison.OrdinalIgnoreCase) ? "DEFAULT" : CurrentLocale;
            var dlg = new OpenFileDialog
            {
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*",
                Title = "Replace briefing picture"
            };

            if (dlg.ShowDialog() != true)
                return;

            _session.Localization.ReplaceResourceFile(locale, key, dlg.FileName);
            LoadPictures();
            StatusText.Text = $"Picture resource replaced: {key}";
        }

        private void LoadKneeboard()
        {
            KneeboardList.Items.Clear();
            KneeboardViewer.Source = null;
            if (_session?.Archive == null) return;

            var root = Path.Combine(_session.Archive.WorkDir, "KNEEBOARD");
            if (!Directory.Exists(root)) return;

            foreach (var file in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                         .Where(IsImageFile)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                KneeboardList.Items.Add(Path.GetRelativePath(_session.Archive.WorkDir, file));
            }
        }

        private void KneeboardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            KneeboardViewer.Source = null;
            if (_session == null || KneeboardList.SelectedItem is not string selected) return;

            var path = Path.Combine(_session.Archive.WorkDir, selected);
            if (File.Exists(path))
                DisplayImage(KneeboardViewer, path);
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
            StatusText.Text = "Kneeboard image added.";
        }

        private void RemoveKneeboard_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null || KneeboardList.SelectedItem is not string selected) return;

            var path = Path.Combine(_session.Archive.WorkDir, selected);
            if (!File.Exists(path)) return;
            if (MessageBox.Show($"Delete '{selected}'?", "Remove kneeboard", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            KneeboardViewer.Source = null;
            File.Delete(path);
            LoadKneeboard();
            StatusText.Text = "Kneeboard image removed.";
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
                RadioSubtitleTextBox.Text = _session.Localization.GetDictionaryValue(CurrentLocale, _selectedRadioMessage.SubtitleKey) ?? "";
            else
                RadioSubtitleTextBox.Text = _selectedRadioMessage.SubtitleKey;
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
            RadioStatusText.Text = "Saved in dictionary.";
            StatusText.Text = "Radio subtitle updated.";
        }

        private void LoadTriggerPic()
        {
            TriggerPicsList.Items.Clear();
            if (_session?.Mission == null) return;

            foreach (var pic in _session.Mission.GetTriggerPictures())
                TriggerPicsList.Items.Add(FormatResourceDisplayWithFallback(pic));
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
            StatusText.Text = "Trigger picture added.";
        }

        private void RemoveTriggerPic_Click(object sender, RoutedEventArgs e)
        {
            if (_session?.Mission == null || TriggerPicsList.SelectedItem is not string selected) return;

            _session.Mission.RemoveTriggerPicture(ResourceTokenFromDisplay(selected));
            LoadTriggerPic();
            StatusText.Text = "Trigger picture link removed.";
        }

        private void LoadScripts()
        {
            ScriptCombo.Items.Clear();
            ScriptBox.Clear();
            ScriptStatusText.Text = "";
            _selectedScriptPath = null;
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
            ScriptStatusText.Text = Path.GetFileName(path);
        }

        private void ReloadScripts_Click(object sender, RoutedEventArgs e) => LoadScripts();

        private void SaveScript_Click(object sender, RoutedEventArgs e)
        {
            SaveSelectedScriptInWorkDir();
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
            _session?.Dispose();
            _session = null;
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
