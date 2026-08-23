using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MoonSharp.Interpreter;

namespace MizEdit.Core;

public sealed class MissionLua
{
    public Table MissionTable { get; private set; } = null!;

    private readonly Script _lua = new Script(CoreModules.Preset_Complete);

    public string MizId
    {
        get
        {
            var direct = GetString("miz_id", "mizId", "missionId", "missionID", "id");
            if (!string.IsNullOrWhiteSpace(direct))
                return direct;

            var extLoader = MissionTable.Get("ext_loader");
            if (extLoader.Type == DataType.Table)
            {
                var nested = extLoader.Table.Get("miz_id");
                if (nested.Type == DataType.String)
                    return nested.String;
            }

            return "";
        }
    }

    public string Theatre => GetString("theatre");

    public string ExtLoaderLibrary
    {
        get
        {
            var extLoader = MissionTable.Get("ext_loader");
            if (extLoader.Type != DataType.Table)
                return "";

            var library = extLoader.Table.Get("library");
            return library.Type == DataType.String ? library.String : "";
        }
    }

    public void LoadFromMissionFile(string missionPath)
    {
        if (!File.Exists(missionPath))
            throw new FileNotFoundException(UserMessages.Get("MissionFileMissing"), missionPath);

        var text = File.ReadAllText(missionPath).TrimStart('\uFEFF'); // убрать BOM
        var code = BuildMissionWrapper(text);

        try
        {
            _lua.DoString(code);
        }
        catch (SyntaxErrorException ex)
        {
            throw new InvalidOperationException(UserMessages.Get("LuaParseError", ex.DecoratedMessage), ex);
        }

        var missionDyn = _lua.Globals.Get("mission");
        if (missionDyn.Type != DataType.Table)
            throw new InvalidOperationException(UserMessages.Get("MissionNotTable"));

        MissionTable = missionDyn.Table;
    }

    private static string BuildMissionWrapper(string text)
    {
        var trimmed = text.TrimStart();

        // Если уже начинается с "mission" или "local mission" — оставляем как есть.
        if (trimmed.StartsWith("mission", StringComparison.Ordinal) ||
            trimmed.StartsWith("local mission", StringComparison.Ordinal))
        {
            return text;
        }

        // Если начинается с return { ... }
        if (trimmed.StartsWith("return", StringComparison.Ordinal))
        {
            var rest = trimmed.Substring("return".Length).TrimStart();
            return "mission = " + rest;
        }

        // Если начинается с таблицы
        if (trimmed.StartsWith("{"))
        {
            return "mission = " + trimmed;
        }

        // Фоллбек — всё равно заворачиваем
        return "mission = " + trimmed;
    }

    public void SaveToMissionFile(string missionPath)
    {
        if (MissionTable == null)
            throw new InvalidOperationException(UserMessages.Get("MissionTableMissing"));

        var luaText = "mission = " + LuaTableSerializer.SerializeTable(MissionTable);
        File.WriteAllText(missionPath, luaText);
    }

    // Удобные геттеры/сеттеры под Briefing
    public string GetString(params string[] keys)
    {
        // попробуем несколько вариантов ключей (в миссиях они иногда разные)
        foreach (var key in keys)
        {
            var v = MissionTable.Get(key);
            if (v.Type == DataType.String)
                return v.String;
        }
        return "";
    }

    public void SetString(string key, string value)
    {
        MissionTable.Set(key, DynValue.NewString(value ?? ""));
    }

    public List<string> GetPictureFileNames()
    {
        var pictures = new List<string>();
        
        // Читаем из pictureFileNameB, pictureFileNameR, pictureFileNameN, pictureFileNameServer
        foreach (var key in new[] { "pictureFileNameB", "pictureFileNameR", "pictureFileNameN", "pictureFileNameServer" })
        {
            var val = MissionTable.Get(key);
            if (val.Type == DataType.Table)
            {
                foreach (var pair in val.Table.Pairs)
                {
                    if (pair.Value.Type == DataType.String && !string.IsNullOrWhiteSpace(pair.Value.String))
                    {
                        pictures.Add(pair.Value.String);
                    }
                }
            }
        }

        return pictures;
    }

    public void AddPictures(List<string> pictures)
    {
        // Сохраняем обновленные картинки обратно в mission
        var keys = new[] { "pictureFileNameB", "pictureFileNameR", "pictureFileNameN", "pictureFileNameServer" };
        
        // Очищаем старые значения
        foreach (var key in keys)
        {
            var val = MissionTable.Get(key);
            if (val.Type == DataType.Table)
            {
                var table = val.Table;
                var keysToRemove = table.Pairs.Select(p => p.Key).ToList();
                foreach (var k in keysToRemove)
                {
                    table.Remove(k);
                }
            }
        }

        // Добавляем новые значения в pictureFileNameB (или создаем если нет)
        var pictureKey = "pictureFileNameB";
        var pictureVal = MissionTable.Get(pictureKey);
        
        if (pictureVal.Type != DataType.Table)
        {
            // Создаем новую таблицу
            pictureVal = DynValue.NewTable(new Table(new Script()));
            MissionTable.Set(pictureKey, pictureVal);
        }

        var pictureTable = pictureVal.Table;
        int index = 1;
        foreach (var pic in pictures)
        {
            pictureTable.Set(index, DynValue.NewString(pic));
            index++;
        }
    }

    public void RemovePicture(string pictureName)
    {
        var raw = ExtractResourceToken(pictureName);
        foreach (var key in new[] { "pictureFileNameB", "pictureFileNameR", "pictureFileNameN", "pictureFileNameServer" })
        {
            var val = MissionTable.Get(key);
            if (val.Type != DataType.Table) continue;

            var table = val.Table;
            var keysToRemove = table.Pairs
                .Where(p => p.Value.Type == DataType.String &&
                            p.Value.String.Equals(raw, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Key)
                .ToList();

            foreach (var k in keysToRemove)
                table.Remove(k);
        }
    }

    public void AddBriefingPicture(string resourceKey, string sideKey = "pictureFileNameB")
    {
        if (string.IsNullOrWhiteSpace(resourceKey))
            return;

        var table = GetOrCreateTable(MissionTable, sideKey);
        if (table.Pairs.Any(p => p.Value.Type == DataType.String &&
                                 p.Value.String.Equals(resourceKey, StringComparison.OrdinalIgnoreCase)))
            return;

        table.Set(NextNumericIndex(table), DynValue.NewString(resourceKey));
    }

    public List<string> GetAudioFileNames()
    {
        // Audio обычно не хранится в mission как массив, а через mapResource
        // Пока возвращаем пустой список, т.к. звуки надо брать из mapResource
        return new List<string>();
    }

    public List<string> GetTriggers()
    {
        var triggers = new List<string>();
        
        var trig = MissionTable.Get("trig");
        if (trig.Type != DataType.Table) return triggers;

        var conditions = trig.Table.Get("conditions");
        var actions = trig.Table.Get("actions");
        var func = trig.Table.Get("func");
        var funcStartup = trig.Table.Get("funcStartup");

        var indexes = new SortedSet<int>();
        AddIndexes(indexes, conditions);
        AddIndexes(indexes, actions);
        AddIndexes(indexes, func);
        AddIndexes(indexes, funcStartup);

        foreach (var index in indexes)
        {
            var condition = GetIndexedString(conditions, index);
            var action = GetIndexedString(actions, index);
            var normalFunc = GetIndexedString(func, index);
            var startupFunc = GetIndexedString(funcStartup, index);
            var source = string.IsNullOrWhiteSpace(startupFunc) ? "Once/Continuous" : "MissionStart";
            triggers.Add($"[{index}] {source} | IF {condition} | DO {action}");
            if (!string.IsNullOrWhiteSpace(normalFunc) && string.IsNullOrWhiteSpace(action))
                triggers.Add($"[{index}] Func: {normalFunc}");
        }

        return triggers;
    }

    public int AddSimpleTrigger(string comment, string conditionLua, string actionLua, bool missionStart)
    {
        var trig = GetOrCreateTable(MissionTable, "trig");
        var conditions = GetOrCreateTable(trig, "conditions");
        var actions = GetOrCreateTable(trig, "actions");
        var funcs = GetOrCreateTable(trig, missionStart ? "funcStartup" : "func");
        var flags = GetOrCreateTable(trig, "flag");

        var index = NextTriggerIndex(conditions, actions, funcs);
        var condition = NormalizeCondition(conditionLua);
        var action = NormalizeAction(actionLua, index, missionStart);

        conditions.Set(index, DynValue.NewString(condition));
        actions.Set(index, DynValue.NewString(action));
        funcs.Set(index, DynValue.NewString($"if mission.trig.conditions[{index}]() then mission.trig.actions[{index}]() end"));
        flags.Set(index, DynValue.NewBoolean(false));

        AddTriggerRuleStub(index, comment, condition, action, missionStart);
        return index;
    }

    public void RemoveTrigger(int index)
    {
        var trig = MissionTable.Get("trig");
        if (trig.Type == DataType.Table)
        {
            foreach (var tableKey in new[] { "conditions", "actions", "func", "funcStartup", "flag", "events", "custom", "customStartup" })
            {
                var table = trig.Table.Get(tableKey);
                if (table.Type == DataType.Table)
                    table.Table.Remove(DynValue.NewNumber(index));
            }
        }

        var rules = MissionTable.Get("trigrules");
        if (rules.Type == DataType.Table)
            rules.Table.Remove(DynValue.NewNumber(index));
    }

    public List<string> GetTrigRules()
    {
        var rules = new List<string>();
        
        var trigrules = MissionTable.Get("trigrules");
        if (trigrules.Type != DataType.Table) return rules;

        // trigrules это массив правил триггеров
        foreach (var pair in trigrules.Table.Pairs)
        {
            if (pair.Value.Type == DataType.Table)
            {
                var rule = pair.Value.Table;
                var initValue = rule.Get("init");
                var flag = rule.Get("flag");
                var actions = rule.Get("actions");
                var conditions = rule.Get("conditions");

                var description = $"[Rule #{pair.Key}]";
                
                if (initValue.Type != DataType.Nil)
                    description += $" Init: {initValue}";
                
                if (flag.Type != DataType.Nil)
                    description += $" Flag: {flag}";

                rules.Add(description);

                // Добавляем условия
                if (conditions.Type == DataType.Table)
                {
                    foreach (var cond in conditions.Table.Pairs)
                    {
                        rules.Add($"  Condition: {cond.Value}");
                    }
                }

                // Добавляем действия
                if (actions.Type == DataType.Table)
                {
                    foreach (var action in actions.Table.Pairs)
                    {
                        rules.Add($"  Action: {action.Value}");
                    }
                }
            }
        }

        return rules;
    }

    public List<string> GetTaskActions()
    {
        var actions = new List<string>();
        WalkForTaskActions(MissionTable, "mission", "", actions, new HashSet<Table>());
        return actions;
    }

    public class RadioMessage
    {
        public string GroupName { get; set; } = "";
        public int TaskIndex { get; set; }
        public int ActionIndex { get; set; }
        public string SubtitleKey { get; set; } = "";
        public string FileKey { get; set; } = "";
        public int Duration { get; set; }
        public string DisplayText { get; set; } = "";
        public string SourcePath { get; set; } = "";
    }

    public List<RadioMessage> GetRadioTransmissions()
    {
        var messages = new List<RadioMessage>();
        WalkForTransmitMessages(MissionTable, "mission", "", messages, new HashSet<Table>());
        return messages;
    }

    private static void WalkForTransmitMessages(
        Table table,
        string path,
        string groupName,
        List<RadioMessage> messages,
        HashSet<Table> visited)
    {
        if (!visited.Add(table))
            return;

        var tableName = table.Get("name");
        if (tableName.Type == DataType.String && LooksLikeGroupTable(table))
            groupName = tableName.String;

        var id = table.Get("id");
        if (id.Type == DataType.String && id.String.Equals("TransmitMessage", StringComparison.OrdinalIgnoreCase))
        {
            var actionParams = table.Get("params");
            if (actionParams.Type == DataType.Table)
            {
                var subtitle = actionParams.Table.Get("subtitle");
                var file = actionParams.Table.Get("file");
                var duration = actionParams.Table.Get("duration");
                var message = new RadioMessage
                {
                    GroupName = groupName,
                    SubtitleKey = subtitle.Type == DataType.String ? subtitle.String : "",
                    FileKey = file.Type == DataType.String ? file.String : "",
                    Duration = duration.Type == DataType.Number ? (int)duration.Number : 0,
                    SourcePath = path,
                };
                message.DisplayText = BuildRadioDisplayText(message, messages.Count + 1);
                messages.Add(message);
            }
        }

        foreach (var pair in table.Pairs)
        {
            if (pair.Value.Type != DataType.Table)
                continue;

            WalkForTransmitMessages(
                pair.Value.Table,
                $"{path}.{FormatPathSegment(pair.Key)}",
                groupName,
                messages,
                visited);
        }
    }

    private static void WalkForTaskActions(
        Table table,
        string path,
        string groupName,
        List<string> actions,
        HashSet<Table> visited)
    {
        if (!visited.Add(table))
            return;

        var tableName = table.Get("name");
        if (tableName.Type == DataType.String && LooksLikeGroupTable(table))
            groupName = tableName.String;

        var id = table.Get("id");
        if (id.Type == DataType.String && IsTaskActionId(id.String))
        {
            var group = string.IsNullOrWhiteSpace(groupName) ? "Mission" : groupName;
            actions.Add($"{actions.Count + 1}. {group} | {id.String} | {SummarizeTaskAction(table)} | {path}");
        }

        foreach (var pair in table.Pairs)
        {
            if (pair.Value.Type != DataType.Table)
                continue;

            WalkForTaskActions(
                pair.Value.Table,
                $"{path}.{FormatPathSegment(pair.Key)}",
                groupName,
                actions,
                visited);
        }
    }

    private static bool IsTaskActionId(string id)
    {
        return id is "TransmitMessage" or "WrappedAction" or "ComboTask" or "ControlledTask" or "Script" or "Orbit" or "EngageTargets" or "AttackGroup" or "FireAtPoint";
    }

    private static string SummarizeTaskAction(Table table)
    {
        var parameters = table.Get("params");
        if (parameters.Type != DataType.Table)
            return "";

        var action = parameters.Table.Get("action");
        if (action.Type == DataType.Table)
        {
            var nestedId = action.Table.Get("id");
            if (nestedId.Type == DataType.String)
                return "action=" + nestedId.String;
        }

        var subtitle = parameters.Table.Get("subtitle");
        var file = parameters.Table.Get("file");
        if (subtitle.Type == DataType.String || file.Type == DataType.String)
            return $"subtitle={StringOrEmpty(subtitle)} file={StringOrEmpty(file)}";

        var command = parameters.Table.Get("command");
        if (command.Type == DataType.String)
            return "command=" + command.String;

        return "";
    }

    private static string StringOrEmpty(DynValue value)
    {
        return value.Type == DataType.String ? value.String : "";
    }

    private static bool LooksLikeGroupTable(Table table)
    {
        return table.Get("groupId").Type == DataType.Number ||
               table.Get("route").Type == DataType.Table ||
               table.Get("units").Type == DataType.Table;
    }

    private static string BuildRadioDisplayText(RadioMessage message, int ordinal)
    {
        var group = string.IsNullOrWhiteSpace(message.GroupName) ? "Mission" : message.GroupName;
        var subtitle = string.IsNullOrWhiteSpace(message.SubtitleKey) ? "(no subtitle)" : message.SubtitleKey;
        return $"{ordinal}. {group} | {subtitle} | {message.FileKey}";
    }

    private static string FormatPathSegment(DynValue key)
    {
        return key.Type switch
        {
            DataType.String => key.String,
            DataType.Number => $"[{Convert.ToInt32(key.Number)}]",
            _ => key.ToString()
        };
    }

    public List<string> GetTriggerPictures()
    {
        var trigPics = new List<string>();
        
        var trigPicData = MissionTable.Get("triggerPictures");
        if (trigPicData.Type != DataType.Table) return trigPics;

        foreach (var pair in trigPicData.Table.Pairs)
        {
            if (pair.Value.Type == DataType.String && !string.IsNullOrWhiteSpace(pair.Value.String))
            {
                trigPics.Add(pair.Value.String);
            }
        }

        return trigPics;
    }

    public void AddTriggerPicture(string resourceKey)
    {
        if (string.IsNullOrWhiteSpace(resourceKey))
            return;

        var table = GetOrCreateTable(MissionTable, "triggerPictures");
        if (table.Pairs.Any(p => p.Value.Type == DataType.String &&
                                 p.Value.String.Equals(resourceKey, StringComparison.OrdinalIgnoreCase)))
            return;

        table.Set(NextNumericIndex(table), DynValue.NewString(resourceKey));
    }

    public void RemoveTriggerPicture(string resourceKey)
    {
        var raw = ExtractResourceToken(resourceKey);
        var tableValue = MissionTable.Get("triggerPictures");
        if (tableValue.Type != DataType.Table)
            return;

        var keysToRemove = tableValue.Table.Pairs
            .Where(p => p.Value.Type == DataType.String &&
                        p.Value.String.Equals(raw, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Key)
            .ToList();

        foreach (var key in keysToRemove)
            tableValue.Table.Remove(key);
    }

    private void AddTriggerRuleStub(int index, string comment, string condition, string action, bool missionStart)
    {
        var rules = GetOrCreateTable(MissionTable, "trigrules");
        var rule = new Table(_lua);
        rule.Set("comment", DynValue.NewString(string.IsNullOrWhiteSpace(comment) ? $"MizEdit trigger {index}" : comment));
        rule.Set("predicate", DynValue.NewString(missionStart ? "triggerStart" : "triggerOnce"));
        rule.Set("eventlist", DynValue.NewString(""));

        var ruleConditions = new Table(_lua);
        var conditionTable = new Table(_lua);
        conditionTable.Set("predicate", DynValue.NewString("c_lua_predicate"));
        conditionTable.Set("text", DynValue.NewString(condition));
        ruleConditions.Set(1, DynValue.NewTable(conditionTable));
        rule.Set("rules", DynValue.NewTable(ruleConditions));

        var ruleActions = new Table(_lua);
        var actionTable = new Table(_lua);
        actionTable.Set("predicate", DynValue.NewString("a_do_script"));
        actionTable.Set("text", DynValue.NewString(action));
        ruleActions.Set(1, DynValue.NewTable(actionTable));
        rule.Set("actions", DynValue.NewTable(ruleActions));

        rules.Set(index, DynValue.NewTable(rule));
    }

    private static void AddIndexes(SortedSet<int> indexes, DynValue tableValue)
    {
        if (tableValue.Type != DataType.Table) return;
        foreach (var pair in tableValue.Table.Pairs)
        {
            if (pair.Key.Type == DataType.Number)
                indexes.Add(Convert.ToInt32(pair.Key.Number));
        }
    }

    private static string GetIndexedString(DynValue tableValue, int index)
    {
        if (tableValue.Type != DataType.Table) return "";
        var value = tableValue.Table.Get(index);
        return value.Type == DataType.String ? value.String : "";
    }

    private static int NextTriggerIndex(params Table[] tables)
    {
        var max = 0;
        foreach (var table in tables)
        {
            foreach (var pair in table.Pairs)
            {
                if (pair.Key.Type == DataType.Number)
                    max = Math.Max(max, Convert.ToInt32(pair.Key.Number));
            }
        }

        return max + 1;
    }

    private static int NextNumericIndex(Table table)
    {
        var max = 0;
        foreach (var pair in table.Pairs)
        {
            if (pair.Key.Type == DataType.Number)
                max = Math.Max(max, Convert.ToInt32(pair.Key.Number));
        }

        return max + 1;
    }

    private Table GetOrCreateTable(Table parent, string key)
    {
        var value = parent.Get(key);
        if (value.Type == DataType.Table)
            return value.Table;

        var table = new Table(_lua);
        parent.Set(key, DynValue.NewTable(table));
        return table;
    }

    private static string NormalizeCondition(string conditionLua)
    {
        var condition = string.IsNullOrWhiteSpace(conditionLua) ? "return(true)" : conditionLua.Trim();
        return condition.StartsWith("return", StringComparison.Ordinal) ? condition : "return(" + condition + ")";
    }

    private static string NormalizeAction(string actionLua, int index, bool missionStart)
    {
        var action = string.IsNullOrWhiteSpace(actionLua)
            ? "a_do_script(\"env.info('MizEdit trigger executed')\")"
            : actionLua.Trim();

        if (!missionStart && !action.Contains($"mission.trig.func[{index}]=nil", StringComparison.Ordinal))
            action += $" mission.trig.func[{index}]=nil;";

        return action;
    }

    private static string ExtractResourceToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var value = text.Trim();
        var arrowIndex = value.IndexOf('→');
        if (arrowIndex >= 0)
            value = value[..arrowIndex].Trim();
        if (value.StartsWith("[DEFAULT]", StringComparison.OrdinalIgnoreCase))
            value = value["[DEFAULT]".Length..].Trim();

        return value;
    }
}
