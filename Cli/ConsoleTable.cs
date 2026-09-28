using System.Text;

namespace Byxcr.Cli;

/// <summary>控制台表格与文本输出辅助（按东亚字符宽度对齐）。</summary>
public static class ConsoleTable
{
    public static void Print(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        var widths = new int[headers.Count];
        for (var i = 0; i < headers.Count; i++) widths[i] = Width(headers[i]);

        foreach (var row in rows)
        {
            for (var i = 0; i < headers.Count && i < row.Length; i++)
            {
                widths[i] = Math.Max(widths[i], Width(row[i]));
            }
        }

        WriteRow(headers, widths);
        WriteRow(widths.Select(w => new string('-', w)).ToArray(), widths);
        foreach (var row in rows) WriteRow(row, widths);
    }

    private static void WriteRow(IReadOnlyList<string> cells, int[] widths)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < widths.Length; i++)
        {
            if (i > 0) builder.Append("  ");
            var cell = i < cells.Count ? cells[i] : string.Empty;
            builder.Append(cell);
            var padding = widths[i] - Width(cell);
            if (padding > 0) builder.Append(' ', padding);
        }
        Logging.Log.Raw(builder.ToString().TrimEnd());
    }

    public static int Width(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var width = 0;
        foreach (var c in text)
        {
            width += IsWide(c) ? 2 : 1;
        }
        return width;
    }

    private static bool IsWide(char c) =>
        c is >= '\u1100' and <= '\u115F'
            or >= '\u2E80' and <= '\uA4CF'
            or >= '\uAC00' and <= '\uD7A3'
            or >= '\uF900' and <= '\uFAFF'
            or >= '\uFE30' and <= '\uFE6F'
            or >= '\uFF00' and <= '\uFF60'
            or >= '\uFFE0' and <= '\uFFE6';

    public static string Pad(string? text, int totalWidth)
    {
        var value = text ?? string.Empty;
        var padding = totalWidth - Width(value);
        return padding > 0 ? value + new string(' ', padding) : value;
    }
}
