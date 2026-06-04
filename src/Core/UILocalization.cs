using System;
using System.Collections.Generic;

namespace MizEdit.Core
{
    public static class UILocalization
    {
        private static string _currentLanguage = "EN";

        public static event Action<string>? LanguageChanged;

        private static readonly Dictionary<string, Dictionary<string, string>> Strings = new()
        {
            ["EN"] = new()
            {
                // File Menu
                ["Menu_File"] = "File",
                ["Menu_File_Open"] = "Open Mission",
                ["Menu_File_Save"] = "Save",
                ["Menu_File_SaveAs"] = "Save As",
                ["Menu_File_Exit"] = "Exit",

                // View Menu
                ["Menu_View"] = "View",
                ["Menu_View_Briefing"] = "Briefing Text",
                ["Menu_View_RouteTasks"] = "Route Tasks",
                ["Menu_View_Settings"] = "Settings",

                // Tabs
                ["Tab_BriefingText"] = "Briefing Text",
                ["Tab_RouteTasks"] = "Route Tasks",
                ["Tab_Callsign"] = "Callsign",
                ["Tab_RouteTasks_Groups"] = "Groups",
                ["Tab_RouteTasks_Waypoints"] = "Waypoints",
                ["Tab_RouteTasks_Tasks"] = "Tasks",
                ["Tab_Pictures"] = "Pictures",
                ["Tab_Kneeboard"] = "Kneeboard",
                ["Tab_Audio"] = "Audio",
                ["Tab_Triggers"] = "Triggers",
                ["Tab_Radio"] = "Transmit Radio",
                ["Tab_TriggerPic"] = "Trigger Pic",
                ["Tab_Script"] = "Script",

                // Buttons
                ["Button_Save"] = "Save",
                ["Button_SaveAs"] = "Save As",
                ["Button_Load"] = "Load",
                ["Button_SaveTask"] = "Save Task",
                ["Button_Cancel"] = "Cancel",
                ["Button_OK"] = "OK",
                ["Button_Add"] = "Add",
                ["Button_Remove"] = "Remove",
                ["Button_Edit"] = "Edit",

                // Labels
                ["Label_Groups"] = "Groups:",
                ["Label_Waypoints"] = "Waypoints:",
                ["Label_Tasks"] = "Tasks:",
                ["Label_Action"] = "Action:",
                ["Label_SubtitleKey"] = "Subtitle Key:",
                ["Label_SubtitleFile"] = "Subtitle File:",
                ["Label_SubtitleDuration"] = "Duration (ms):",
                ["Label_OptionName"] = "Option Name:",
                ["Label_OptionValue"] = "Option Value:",
                ["Label_Language"] = "Language:",

                // Status
                ["Status_Loading"] = "Loading mission...",
                ["Status_Saving"] = "Saving mission...",
                ["Status_Ready"] = "Ready",
                ["Status_Error"] = "Error",
                ["Status_Success"] = "Success",
                // Extra
                    ["Menu_Batch"] = "Batch",
                    ["Menu_Info"] = "Info",
                    ["Label_LocaleNumber"] = "Locale #",
                    ["Button_LocaleUp"] = "Up",
                    ["Button_LocaleDown"] = "Down",
                    ["Button_DeleteLocale"] = "DEL LOCALE",
                    ["Button_AddLocale"] = "ADD LOCALE",
                    ["Label_UI_Language"] = "UI Language:",
                    ["UILang_EN_Display"] = "English",
                    ["UILang_RU_Display"] = "Русский",
                    ["Label_MissionName"] = "Mission Name:",
                    ["Label_MissionDesc"] = "Mission Description:",
                    ["Label_RedTask"] = "Red Task:",
                    ["Label_BlueTask"] = "Blue Task:",
                    ["Button_ApplyMemory"] = "Apply to mission (in memory)",

                // Messages
                ["Message_SelectMission"] = "Please select a mission file",
                ["Message_NoTaskSelected"] = "No task selected",
                ["Message_SaveSuccess"] = "Mission saved successfully",
                ["Message_SaveError"] = "Error saving mission",
                ["Message_ConfirmExit"] = "Are you sure you want to exit?",
                ["Dialog_SelectLocale_Title"] = "Select mission language",
                ["Dialog_AvailableLocales"] = "Available languages:",
                ["Status_Loaded"] = "Loaded: {0}",
                ["Label_MizId"] = "Miz ID: {0}",
                ["Status_NoFile"] = "No file opened",
                ["Menu_File_SaveAsTxt"] = "Save briefing as TXT",
                ["Menu_File_ImportTxt"] = "Import TXT",
            },
            ["RU"] = new()
            {
                // Меню Файл
                ["Menu_File"] = "Файл",
                ["Menu_File_Open"] = "Открыть миссию",
                ["Menu_File_Save"] = "Сохранить",
                ["Menu_File_SaveAs"] = "Сохранить как",
                ["Menu_File_Exit"] = "Выход",

                // Меню Вид
                ["Menu_View"] = "Вид",
                ["Menu_View_Briefing"] = "Текст брифинга",
                ["Menu_View_RouteTasks"] = "Маршруты и задачи",
                ["Menu_View_Settings"] = "Параметры",

                // Вкладки
                ["Tab_BriefingText"] = "Текст брифинга",
                ["Tab_RouteTasks"] = "Маршруты и задачи",
                ["Tab_Callsign"] = "Позывной",
                ["Tab_RouteTasks_Groups"] = "Группы",
                ["Tab_RouteTasks_Waypoints"] = "Путевые точки",
                ["Tab_RouteTasks_Tasks"] = "Задачи",
                ["Tab_Pictures"] = "Изображения",
                ["Tab_Kneeboard"] = "Kneeboard",
                ["Tab_Audio"] = "Аудио",
                ["Tab_Triggers"] = "Триггеры",
                ["Tab_Radio"] = "Радиопередачи",
                ["Tab_TriggerPic"] = "Trigger Pic",
                ["Tab_Script"] = "Script",

                // Кнопки
                ["Button_Save"] = "Сохранить",
                ["Button_SaveAs"] = "Сохранить как",
                ["Button_Load"] = "Загрузить",
                ["Button_SaveTask"] = "Сохранить задачу",
                ["Button_Cancel"] = "Отмена",
                ["Button_OK"] = "OK",
                ["Button_Add"] = "Добавить",
                ["Button_Remove"] = "Удалить",
                ["Button_Edit"] = "Редактировать",

                // Метки
                ["Label_Groups"] = "Группы:",
                ["Label_Waypoints"] = "Путевые точки:",
                ["Label_Tasks"] = "Задачи:",
                ["Label_Action"] = "Действие:",
                ["Label_SubtitleKey"] = "Ключ субтитров:",
                ["Label_SubtitleFile"] = "Файл субтитров:",
                ["Label_SubtitleDuration"] = "Длительность (мс):",
                ["Label_OptionName"] = "Имя опции:",
                ["Label_OptionValue"] = "Значение опции:",
                ["Label_Language"] = "Язык:",

                // Статус
                ["Status_Loading"] = "Загрузка миссии...",
                ["Status_Saving"] = "Сохранение миссии...",
                ["Status_Ready"] = "Готово",
                ["Status_Error"] = "Ошибка",
                ["Status_Success"] = "Успешно",

                // Сообщения
                ["Message_SelectMission"] = "Пожалуйста, выберите файл миссии",
                ["Message_NoTaskSelected"] = "Задача не выбрана",
                ["Message_SaveSuccess"] = "Миссия успешно сохранена",
                ["Message_SaveError"] = "Ошибка при сохранении миссии",
                ["Message_ConfirmExit"] = "Вы уверены, что хотите выйти?",
                // Дополнительно
                ["Menu_Batch"] = "Пакет",
                ["Menu_Info"] = "Справка",
                ["Label_LocaleNumber"] = "Язык #",
                ["Button_LocaleUp"] = "Вверх",
                ["Button_LocaleDown"] = "Вниз",
                ["Button_DeleteLocale"] = "Удалить локаль",
                ["Button_AddLocale"] = "Добавить локаль",
                ["Label_UI_Language"] = "Язык интерфейса:",
                ["UILang_EN_Display"] = "English",
                ["UILang_RU_Display"] = "Русский",
                ["Label_MissionName"] = "Название миссии:",
                ["Label_MissionDesc"] = "Описание миссии:",
                ["Label_RedTask"] = "Задача (красные):",
                ["Label_BlueTask"] = "Задача (синие):",
                ["Button_ApplyMemory"] = "Применить (в памяти)",
                ["Dialog_SelectLocale_Title"] = "Выберите язык миссии",
                ["Dialog_AvailableLocales"] = "Доступные языки:",
                ["Status_Loaded"] = "Загружено: {0}",
                ["Label_MizId"] = "ID миссии: {0}",
                ["Status_NoFile"] = "Файл не открыт",
                ["Menu_File_SaveAsTxt"] = "Сохранить брифинг как TXT",
                ["Menu_File_ImportTxt"] = "Импорт TXT",
            }
        };

        public static string Get(string key)
        {
            // Safe lookup with fallback to EN
            if (Strings.TryGetValue(_currentLanguage, out var dict) && dict != null)
            {
                if (dict.TryGetValue(key, out var value))
                    return value;
            }

            // Fallback to English
            if (Strings.TryGetValue("EN", out var en) && en != null && en.TryGetValue(key, out var ev))
            {
                return ev;
            }

            return $"[{key}]";
        }

        public static void SetLanguage(string language)
        {
            if (Strings.ContainsKey(language) && _currentLanguage != language)
            {
                _currentLanguage = language;
                LanguageChanged?.Invoke(language);
            }
        }

        public static string GetCurrentLanguage() => _currentLanguage;

        public static List<string> GetAvailableLanguages() => new(Strings.Keys);
    }
}
