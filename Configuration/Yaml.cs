using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Byxcr.Core;

namespace Byxcr.Configuration;

/// <summary>
/// 轻量 YAML 读取器：把 YAML 子集解析为 <see cref="JsonNode"/>，从而复用既有的
/// 「默认值 → 深合并 → 环境变量覆盖 → 源生成反序列化」管线。
/// <para>刻意不引入反射型 YAML 库：NativeAOT 下反射序列化不可用，且会带来裁剪告警。</para>
/// <para>支持：块映射、块序列、流式数组 <c>[a, b]</c>、流式映射 <c>{a: b}</c>、单/双引号标量、
/// <c>#</c> 注释、<c>---</c> 文档头、空值（<c>~</c> / 留空）。</para>
/// <para>不支持：锚点与别名（<c>&amp;</c> / <c>*</c>）、显式标签、块标量（<c>|</c> / <c>&gt;</c>）、多文档。</para>
/// </summary>
public static class YamlReader
{
    private sealed record Line(int Indent, string Text, int Number);

    public static JsonNode Parse(string text, string source)
    {
        var lines = Scan(text, source);
        if (lines.Count == 0) return new JsonObject();

        var position = 0;
        var node = ParseNode(lines, ref position, lines[0].Indent, source) ?? new JsonObject();
        if (position < lines.Count)
            throw Error(source, lines[position], "缩进层级不一致，无法确定归属");

        return node;
    }

    // ------------------------------------------------------------------ 词法

    private static List<Line> Scan(string text, string source)
    {
        var lines = new List<Line>();
        var number = 0;

        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.Length == 0) continue;

            var indent = 0;
            while (indent < line.Length && line[indent] == ' ') indent++;

            if (indent < line.Length && line[indent] == '\t')
                throw Error(source, new Line(indent, line[indent..], number), "缩进请使用空格，不要使用制表符");

            var content = StripComment(line[indent..]);
            if (content.Length == 0) continue;
            if (content[0] == '%') continue;                                       // YAML 指令行
            if (content == "..." ) continue;
            if (content == "---" || content.StartsWith("--- ", StringComparison.Ordinal)) continue;

            lines.Add(new Line(indent, content, number));
        }

        return lines;
    }

    /// <summary>去掉行尾注释。引号内的 <c>#</c> 不算注释，紧贴内容的 <c>#</c> 也不算（如 <c>abc#def</c>）。</summary>
    private static string StripComment(string content)
    {
        var quote = '\0';

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];

            if (quote != '\0')
            {
                if (quote == '"' && c == '\\') { i++; continue; }
                if (c == quote)
                {
                    if (quote == '\'' && i + 1 < content.Length && content[i + 1] == '\'') { i++; continue; }
                    quote = '\0';
                }
                continue;
            }

            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '#' && (i == 0 || content[i - 1] is ' ' or '\t')) return content[..i].TrimEnd();
        }

        return content.TrimEnd();
    }

    // ------------------------------------------------------------------ 语法

    private static JsonNode? ParseNode(List<Line> lines, ref int position, int indent, string source)
    {
        if (position >= lines.Count) return null;

        var line = lines[position];
        if (line.Indent < indent) return null;
        if (line.Text[0] is '{' or '[') return ParseFlow(line.Text, source, line);

        return IsSequenceItem(line.Text)
            ? ParseSequence(lines, ref position, line.Indent, source)
            : ParseMapping(lines, ref position, line.Indent, source, null);
    }

    private static JsonObject ParseMapping(List<Line> lines, ref int position, int indent, string source, Line? inline)
    {
        var result = new JsonObject();
        if (inline is not null) AddEntry(result, inline, lines, ref position, indent, source);

        while (position < lines.Count)
        {
            var line = lines[position];
            if (line.Indent != indent || IsSequenceItem(line.Text)) break;
            position++;
            AddEntry(result, line, lines, ref position, indent, source);
        }

        return result;
    }

    private static JsonArray ParseSequence(List<Line> lines, ref int position, int indent, string source)
    {
        var result = new JsonArray();

        while (position < lines.Count)
        {
            var line = lines[position];
            if (line.Indent != indent || !IsSequenceItem(line.Text)) break;
            position++;

            var offset = 1;
            while (offset < line.Text.Length && line.Text[offset] == ' ') offset++;
            var rest = line.Text[offset..];
            var contentColumn = line.Indent + offset;

            if (rest.Length == 0)
            {
                // "-\n  a: 1"：条目内容整体是下一级块
                if (position < lines.Count && lines[position].Indent > indent)
                    Append(result, ParseNode(lines, ref position, lines[position].Indent, source));
                else
                    Append(result, null);
                continue;
            }

            if (FindKeySeparator(rest) >= 0)
            {
                // "- key: value"：条目是一个映射，其条目列 = 短横线之后的第一个非空列
                Append(result, ParseMapping(lines, ref position, contentColumn, source, new Line(contentColumn, rest, line.Number)));
                continue;
            }

            Append(result, ParseInline(rest, source, line));
        }

        return result;
    }

    /// <summary>
    /// 追加数组元素。显式走 <see cref="IList{T}"/> 接口，避开带裁剪告警的
    /// <c>JsonArray.Add&lt;T&gt;</c> 泛型重载（NativeAOT 下不可用）。
    /// </summary>
    private static void Append(JsonArray array, JsonNode? item)
        => ((IList<JsonNode?>)array).Add(item);

    private static void AddEntry(JsonObject target, Line line, List<Line> lines, ref int position, int indent, string source)
    {
        var separator = FindKeySeparator(line.Text);
        if (separator < 0) throw Error(source, line, "缺少「键: 值」中的冒号");

        var rawKey = line.Text[..separator].Trim();
        if (rawKey.Length == 0) throw Error(source, line, "键名不能为空");

        var key = rawKey[0] is '"' or '\'' ? Unquote(rawKey, source, line) : rawKey;
        var rest = line.Text[(separator + 1)..].Trim();

        target[key] = ParseValue(rest, lines, ref position, indent, source, line);
    }

    private static JsonNode? ParseValue(string rest, List<Line> lines, ref int position, int indent, string source, Line line)
    {
        if (rest.Length > 0)
        {
            if (rest[0] is '|' or '>') throw Error(source, line, "不支持块标量（| 或 >），请改用带引号的字符串");
            if (rest[0] is '&' or '*') throw Error(source, line, "不支持锚点与别名");
            return ParseInline(rest, source, line);
        }

        if (position < lines.Count)
        {
            var next = lines[position];
            if (next.Indent > indent) return ParseNode(lines, ref position, next.Indent, source);
            // "items:\n- a\n- b"：序列可以与键同缩进
            if (next.Indent == indent && IsSequenceItem(next.Text)) return ParseSequence(lines, ref position, indent, source);
        }

        return null;
    }

    private static JsonNode? ParseInline(string text, string source, Line line)
        => text[0] is '{' or '[' ? ParseFlow(text, source, line) : ParseScalar(text, source, line);

    /// <summary>按键分隔符定位：冒号后必须是空白或行尾，且不在引号内。这样 <c>mysql:5.6</c> 不会被误判为映射。</summary>
    private static int FindKeySeparator(string text)
    {
        var quote = '\0';

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quote != '\0')
            {
                if (quote == '"' && c == '\\') { i++; continue; }
                if (c == quote)
                {
                    if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'') { i++; continue; }
                    quote = '\0';
                }
                continue;
            }

            if (c is '"' or '\'') { quote = c; continue; }
            if (c is '[' or '{') return -1;
            if (c == ':' && (i + 1 >= text.Length || text[i + 1] == ' ')) return i;
        }

        return -1;
    }

    private static bool IsSequenceItem(string text)
        => text.Length > 0 && text[0] == '-' && (text.Length == 1 || text[1] == ' ');

    // ------------------------------------------------------------------ 标量

    private static JsonNode? ParseScalar(string text, string source, Line line)
    {
        if (text.Length == 0) return null;

        if (text[0] == '"') return JsonValue.Create(ReadQuoted(text, '"', source, line));
        if (text[0] == '\'') return JsonValue.Create(ReadQuoted(text, '\'', source, line));
        if (text is "~" or "null" or "Null" or "NULL") return null;

        return Infer(text);
    }

    /// <summary>按 YAML 1.2 core schema 推断标量类型：只有 <c>true</c>/<c>false</c> 被当作布尔，<c>yes</c>/<c>no</c> 仍是字符串。</summary>
    private static JsonNode Infer(string text)
    {
        if (text is "true" or "True" or "TRUE") return JsonValue.Create(true)!;
        if (text is "false" or "False" or "FALSE") return JsonValue.Create(false)!;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l)!;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d)!;
        return JsonValue.Create(text)!;
    }

    private static string ReadQuoted(string text, char quote, string source, Line line)
    {
        if (text.Length < 2 || text[^1] != quote)
            throw Error(source, line, quote == '"' ? "双引号字符串未闭合" : "单引号字符串未闭合");

        var inner = text[1..^1];

        if (quote == '\'') return inner.Replace("''", "'", StringComparison.Ordinal);
        return Unescape(inner, source, line);
    }

    private static string Unescape(string text, string source, Line line)
    {
        var builder = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\') { builder.Append(text[i]); continue; }
            if (i + 1 >= text.Length) throw Error(source, line, "字符串以孤立的反斜杠结尾");

            var escaped = text[++i];
            builder.Append(escaped switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '0' => '\0',
                '\\' => '\\',
                '"' => '"',
                '\'' => '\'',
                _ => throw Error(source, line, $"不支持的转义字符：\\{escaped}"),
            });
        }

        return builder.ToString();
    }

    private static string Unquote(string text, string source, Line line)
        => text[0] is '"' or '\'' ? ReadQuoted(text, text[0], source, line) : text;

    // ------------------------------------------------------------------ 流式

    private static JsonNode? ParseFlow(string text, string source, Line line)
    {
        var index = 0;
        var node = ReadFlowNode(text, ref index, source, line);
        SkipSpaces(text, ref index);
        if (index < text.Length) throw Error(source, line, $"流式结构后存在多余内容：{text[index..]}");
        return node;
    }

    private static JsonNode? ReadFlowNode(string text, ref int index, string source, Line line)
    {
        SkipSpaces(text, ref index);
        if (index >= text.Length) return null;

        var current = text[index];
        if (current == '[') return ReadFlowSequence(text, ref index, source, line);
        if (current == '{') return ReadFlowMapping(text, ref index, source, line);

        if (current is '"' or '\'')
            return JsonValue.Create(ReadQuoted(ReadFlowToken(text, ref index, stopAtColon: false), current, source, line));

        return Infer(ReadFlowToken(text, ref index, stopAtColon: false));
    }

    private static JsonArray ReadFlowSequence(string text, ref int index, string source, Line line)
    {
        var result = new JsonArray();
        index++; // '['

        while (true)
        {
            SkipSpaces(text, ref index);
            if (index >= text.Length) throw Error(source, line, "流式数组未闭合（缺少 ]）");
            if (text[index] == ']') { index++; return result; }

            result.Add(ReadFlowNode(text, ref index, source, line));
            SkipSpaces(text, ref index);

            if (index < text.Length && text[index] == ',') { index++; continue; }
            if (index < text.Length && text[index] == ']') { index++; return result; }
            throw Error(source, line, "流式数组元素之间需要逗号分隔");
        }
    }

    private static JsonObject ReadFlowMapping(string text, ref int index, string source, Line line)
    {
        var result = new JsonObject();
        index++; // '{'

        while (true)
        {
            SkipSpaces(text, ref index);
            if (index >= text.Length) throw Error(source, line, "流式映射未闭合（缺少 }）");
            if (text[index] == '}') { index++; return result; }

            string key;
            if (text[index] is '"' or '\'')
            {
                var quote = text[index];
                key = ReadQuoted(ReadFlowToken(text, ref index, stopAtColon: true), quote, source, line);
            }
            else
            {
                key = ReadFlowToken(text, ref index, stopAtColon: true);
            }

            if (key.Length == 0) throw Error(source, line, "流式映射的键名不能为空");

            SkipSpaces(text, ref index);
            if (index >= text.Length || text[index] != ':') throw Error(source, line, $"流式映射的键「{key}」缺少冒号");
            index++;

            result[key] = ReadFlowNode(text, ref index, source, line);
            SkipSpaces(text, ref index);

            if (index < text.Length && text[index] == ',') { index++; continue; }
            if (index < text.Length && text[index] == '}') { index++; return result; }
            throw Error(source, line, "流式映射条目之间需要逗号分隔");
        }
    }

    /// <summary>
    /// 读取一个流式记号。<paramref name="stopAtColon"/> 为 true 时用于键（在 <c>:</c> 处截断），
    /// 为 false 时用于值（<c>:</c> 属于内容，如 <c>[https://a.com]</c>）。
    /// </summary>
    private static string ReadFlowToken(string text, ref int index, bool stopAtColon)
    {
        var start = index;
        var quote = '\0';

        while (index < text.Length)
        {
            var c = text[index];
            if (quote != '\0')
            {
                if (quote == '"' && c == '\\') { index += 2; continue; }
                if (c == quote) quote = '\0';
                index++;
                continue;
            }

            if (c is '"' or '\'') { quote = c; index++; continue; }
            if (c is ',' or ']' or '}') break;
            if (stopAtColon && c == ':') break;
            index++;
        }

        return text[start..index].Trim();
    }

    private static void SkipSpaces(string text, ref int index)
    {
        while (index < text.Length && text[index] == ' ') index++;
    }

    private static ConfigurationException Error(string source, Line line, string message)
        => new($"{source}:{line.Number}：{message}");
}

/// <summary>把 <see cref="JsonNode"/> 写回 YAML 文本（用于 <c>byxcr config</c> 输出与往返自检）。</summary>
public static class YamlWriter
{
    public static string Write(JsonNode? node)
    {
        var builder = new StringBuilder();

        switch (node)
        {
            case JsonObject obj:
                WriteObject(builder, obj, 0, string.Empty);
                break;
            case JsonArray array:
                WriteSequence(builder, array, 0);
                break;
            default:
                builder.Append(FormatScalar(node)).Append('\n');
                break;
        }

        return builder.ToString();
    }

    private static void WriteObject(StringBuilder builder, JsonObject obj, int indent, string firstPrefix)
    {
        var first = true;

        foreach (var pair in obj)
        {
            builder.Append(first ? firstPrefix : new string(' ', indent));
            first = false;
            builder.Append(FormatScalar(pair.Key)).Append(':');
            // 块状取值自带结尾换行，行内取值需要补一个
            if (!WriteValue(builder, pair.Value, indent)) builder.Append('\n');
        }
    }

    private static void WriteSequence(StringBuilder builder, JsonArray array, int indent)
    {
        var padding = new string(' ', indent);

        foreach (var item in array)
        {
            if (item is JsonObject { Count: > 0 } obj)
            {
                // "- key: value"：后续条目与短横线之后的内容左对齐
                WriteObject(builder, obj, indent + 2, padding + "- ");
                continue;
            }

            builder.Append(padding).Append("- ");
            if (item is JsonArray { Count: > 0 } || item is JsonObject)
            {
                builder.Append(FormatFlow(item)).Append('\n');
                continue;
            }

            builder.Append(FormatScalar(item)).Append('\n');
        }
    }

    /// <summary>写冒号之后的取值部分。返回 true 表示已输出块状结构（结尾换行也已写好）。</summary>
    private static bool WriteValue(StringBuilder builder, JsonNode? value, int indent)
    {
        switch (value)
        {
            case null:
                builder.Append(" null");
                return false;

            case JsonObject { Count: 0 }:
                builder.Append(" {}");
                return false;

            case JsonArray { Count: 0 }:
                builder.Append(" []");
                return false;

            case JsonArray array when IsFlat(array):
                builder.Append(' ').Append(FormatFlow(array));
                return false;

            case JsonObject obj:
                builder.Append('\n');
                WriteObject(builder, obj, indent + 2, new string(' ', indent + 2));
                return true;

            case JsonArray array:
                builder.Append('\n');
                WriteSequence(builder, array, indent + 2);
                return true;

            default:
                builder.Append(' ').Append(FormatScalar(value));
                return false;
        }
    }

    private static bool IsFlat(JsonArray array)
    {
        foreach (var item in array)
        {
            if (item is JsonObject or JsonArray) return false;
        }
        return true;
    }

    private static string FormatFlow(JsonNode node)
    {
        if (node is JsonArray array)
        {
            var parts = new List<string>(array.Count);
            foreach (var item in array)
            {
                if (item is JsonObject child) parts.Add(FormatFlowObject(child));
                else if (item is JsonArray inner) parts.Add(FormatFlow(inner));
                else parts.Add(FormatScalar(item));
            }
            return "[" + string.Join(", ", parts) + "]";
        }

        return node is JsonObject obj ? FormatFlowObject(obj) : FormatScalar(node);
    }

    private static string FormatFlowObject(JsonObject obj)
    {
        var parts = new List<string>(obj.Count);
        foreach (var pair in obj)
        {
            parts.Add(pair.Value is JsonObject or JsonArray
                ? $"{FormatScalar(pair.Key)}: {FormatFlow(pair.Value!)}"
                : $"{FormatScalar(pair.Key)}: {FormatScalar(pair.Value)}");
        }
        return "{" + string.Join(", ", parts) + "}";
    }

    private static string FormatScalar(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text)) return Quote(text);
            if (value.TryGetValue<bool>(out var flag)) return flag ? "true" : "false";
            if (value.TryGetValue<long>(out var integer)) return integer.ToString(CultureInfo.InvariantCulture);
            if (value.TryGetValue<double>(out var number)) return number.ToString("R", CultureInfo.InvariantCulture);
        }

        return "null";
    }

    private static string Quote(string text)
    {
        if (!NeedsQuotes(text)) return text;

        var builder = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            _ = c switch
            {
                '\\' => builder.Append("\\\\"),
                '"' => builder.Append("\\\""),
                '\n' => builder.Append("\\n"),
                '\r' => builder.Append("\\r"),
                '\t' => builder.Append("\\t"),
                _ => builder.Append(c),
            };
        }
        return builder.Append('"').ToString();
    }

    private static bool NeedsQuotes(string text)
    {
        if (text.Length == 0) return true;
        if (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])) return true;
        if (text.Contains('\n') || text.Contains('\r') || text.Contains('\t')) return true;
        if (text is "true" or "false" or "null" or "~") return true;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return true;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return true;
        if (text.Contains(": ") || text.Contains(" #") || text.EndsWith(':')) return true;
        return "-?:,[]{}#&*!|>'\"%@`".Contains(text[0]);
    }
}
