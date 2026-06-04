using System;
using System.Globalization;
using System.Text;
using MoonSharp.Interpreter;

namespace MizEdit.Core;

public static class LuaTableSerializer
{    public static string SerializeTable(Table table)
    {
        var sb = new StringBuilder(1024);
        SerializeTableInternal(table, sb, 0);
        return sb.ToString();
    }

    private static void SerializeTableInternal(Table t, StringBuilder sb, int indent)
    {
        sb.Append("{\n");

        foreach (var pair in t.Pairs)
        {
            Indent(sb, indent + 1);

            // ключ
            AppendKey(sb, pair.Key);

            sb.Append(" = ");

            // значение
            AppendValue(sb, pair.Value, indent + 1);

            sb.Append(",\n");
        }

        Indent(sb, indent);
        sb.Append("}");
    }

    private static void AppendKey(StringBuilder sb, DynValue key)
    {
        // В Lua ключ может быть строкой или числом.
        if (key.Type == DataType.String)
        {
            var k = key.String;
            // если похоже на идентификатор -> key, иначе ["key"]
            if (IsLuaIdentifier(k))
                sb.Append(k);
            else
                sb.Append("[\"").Append(EscapeLuaString(k)).Append("\"]");
        }
        else if (key.Type == DataType.Number)
        {
            sb.Append("[").Append(key.Number.ToString(CultureInfo.InvariantCulture)).Append("]");
        }
        else
        {
            // fallback
            sb.Append("[\"").Append(EscapeLuaString(key.ToString())).Append("\"]");
        }
    }

    private static void AppendValue(StringBuilder sb, DynValue v, int indent)
    {
        switch (v.Type)
        {
            case DataType.String:
                sb.Append("\"").Append(EscapeLuaString(v.String)).Append("\"");
                break;
            case DataType.Number:
                sb.Append(v.Number.ToString(CultureInfo.InvariantCulture));
                break;
            case DataType.Boolean:
                sb.Append(v.Boolean ? "true" : "false");
                break;
            case DataType.Table:
                SerializeTableInternal(v.Table, sb, indent);
                break;
            case DataType.Nil:
            case DataType.Void:
                sb.Append("nil");
                break;
            default:
                // функции/юзердата и т.п. в mission почти не встречаются
                sb.Append("nil");
                break;
        }
    }

    private static bool IsLuaIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (!(char.IsLetter(s[0]) || s[0] == '_')) return false;
        for (int i = 1; i < s.Length; i++)
        {
            if (!(char.IsLetterOrDigit(s[i]) || s[i] == '_')) return false;
        }
        return true;
    }

    private static string EscapeLuaString(string s)
    {
        return (s ?? "")
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }

    private static void Indent(StringBuilder sb, int indent)
    {
        for (int i = 0; i < indent; i++)
            sb.Append("  ");
    }
}
