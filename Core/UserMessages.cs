using System.Globalization;

namespace MizEdit.Core;

/// <summary>
/// User-facing backend messages. Technical identifiers such as Lua, .miz,
/// DictKey and mapResource intentionally remain unchanged in both languages.
/// </summary>
public static class UserMessages
{
    private static readonly IReadOnlyDictionary<string, (string En, string Ru)> Messages =
        new Dictionary<string, (string En, string Ru)>(StringComparer.Ordinal)
        {
            ["MissionFileMissing"] = ("The mission file was not found.", "Файл mission не найден."),
            ["LuaParseError"] = ("Lua parse error: {0}", "Ошибка разбора Lua: {0}"),
            ["MissionNotTable"] = ("The Lua object mission is not a table.", "Lua-объект mission не является таблицей."),
            ["MissionTableMissing"] = ("MissionTable is not loaded.", "MissionTable не загружена."),

            ["MizOutputPathEmpty"] = ("The output .miz path is empty.", "Не указан путь для выходного файла .miz."),
            ["MizDestinationUnknown"] = ("Cannot determine the destination directory.", "Не удалось определить папку назначения."),
            ["MizMissionEntryMissing"] = ("The generated .miz archive does not contain a mission file.", "Созданный архив .miz не содержит файл mission."),

            ["LocalizationReadFailed"] = ("Cannot read localization resources: {0}", "Не удалось прочитать ресурсы локализации: {0}"),
            ["DictionaryReadFailed"] = ("Cannot read the localization dictionary: {0}", "Не удалось прочитать словарь локализации: {0}"),
            ["SourcePathEmpty"] = ("The source file path is empty.", "Не указан путь к исходному файлу."),
            ["ResourceFileMissing"] = ("The resource file was not found.", "Файл ресурса не найден."),
            ["ResourceKeyEmpty"] = ("The resource key is empty.", "Ключ ресурса не указан."),
            ["ResourceKeyMissing"] = ("Resource key '{0}' was not found in locale '{1}'.", "Ключ ресурса '{0}' не найден в локали '{1}'."),
            ["TxtFileMissing"] = ("The TXT file was not found.", "Файл TXT не найден."),

            ["ReplacementImageMissing"] = ("The replacement image was not found.", "Изображение для замены не найдено."),
            ["ReplacementImageFormats"] = ("Supported image formats: PNG, JPG, JPEG and BMP.", "Поддерживаемые форматы изображений: PNG, JPG, JPEG и BMP."),
            ["ReplacementTargetDirectoryMissing"] = ("The replacement target has no directory.", "Не удалось определить папку заменяемого файла."),
            ["UnsupportedImageFormat"] = ("Unsupported target image format: {0}", "Неподдерживаемый формат целевого изображения: {0}"),

            ["TranslationQueueActive"] = ("A translation queue is already running.", "Очередь перевода уже запущена."),
            ["OllamaTimeout"] = ("Ollama did not answer within {0} seconds.", "Ollama не ответила за {0} с."),
            ["OllamaCompletionTimeout"] = ("Ollama did not finish its answer within {0} seconds.", "Ollama не завершила ответ за {0} с."),
            ["OllamaHttpError"] = ("Ollama returned HTTP {0}: {1}", "Ollama вернула ошибку HTTP {0}: {1}"),
            ["OllamaEmptyResult"] = ("Ollama returned an empty translation.", "Ollama вернула пустой перевод."),
            ["ProtectedMarkerCount"] = ("expected {0} marker(s), received {1}", "ожидалось маркеров: {0}; получено: {1}"),
            ["ProtectedMarkerPosition"] = ("expected marker {0} at position {1}, received {2}", "в позиции {1} ожидался маркер {0}, получен {2}"),
            ["ProtectedTokenChanged"] = ("The translation changed a protected mission token ({0}).", "Перевод изменил защищённый токен миссии ({0})."),
            ["ProtectedTokenChangedReason"] = ("The translation changed a protected mission token ({0}): {1}", "Перевод изменил защищённый токен миссии ({0}): {1}"),

            ["GeminiApiKeyRequired"] = ("A Gemini API key is required.", "Необходим API-ключ Gemini."),
            ["GeminiRetriesExhausted"] = ("Gemini batch translation failed after three attempts.", "Пакетный перевод Gemini не выполнен после трёх попыток."),
            ["GeminiHttpError"] = ("Gemini returned HTTP {0}.", "Gemini вернула ошибку HTTP {0}."),
            ["GeminiIncomplete"] = ("The Gemini interaction did not complete.", "Запрос Gemini не был завершён."),
            ["GeminiEmptyJson"] = ("Gemini returned an empty JSON result.", "Gemini вернула пустой результат JSON."),
            ["GeminiMarkerCount"] = ("Gemini changed the protected marker count.", "Gemini изменила количество защищённых маркеров."),
            ["GeminiMarkerOrder"] = ("Gemini reordered or mixed markers between rows.", "Gemini изменила порядок маркеров или смешала их между строками."),
            ["GeminiRowCount"] = ("Gemini changed the number of dictionary rows.", "Gemini изменила количество строк словаря."),
            ["GeminiInvalidRow"] = ("Gemini returned an invalid row.", "Gemini вернула некорректную строку."),
            ["GeminiDuplicateKeys"] = ("Gemini returned duplicate dictionary keys.", "Gemini вернула повторяющиеся ключи словаря."),
            ["GeminiUnknownKey"] = ("Gemini returned an unknown dictionary key.", "Gemini вернула неизвестный ключ словаря."),
            ["GeminiNoText"] = ("The Gemini response contained no text output.", "Ответ Gemini не содержит текста."),
            ["ApiError"] = ("API error", "Ошибка API"),
        };

    public static string Get(string key, params object?[] args)
    {
        if (!Messages.TryGetValue(key, out var message))
            return key;

        var template = UILocalization.GetCurrentLanguage().Equals("RU", StringComparison.OrdinalIgnoreCase)
            ? message.Ru
            : message.En;
        return args.Length == 0
            ? template
            : string.Format(CultureInfo.CurrentCulture, template, args);
    }
}
