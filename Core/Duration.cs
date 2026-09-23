using System.Globalization;

namespace Byxcr.Core;

/// <summary>
/// 时长文本解析：<c>30d</c>（天）、<c>24h</c>（小时）、<c>1440m</c>（分钟）、<c>86400s</c>（秒），
/// 也接受中文单位（<c>30天</c> / <c>24小时</c> / <c>1440分钟</c> / <c>86400秒</c>）；
/// 支持多段组合（<c>1d12h</c> / <c>3m30s</c>），与 <see cref="Format"/> 的输出可互相往返。
/// <para><b>不带单位的裸数字按秒理解</b>（如 <c>3600</c> = 1 小时），与数据库、JSON 的秒制保持一致。</para>
/// </summary>
public static class Duration
{
    /// <summary>允许的最大时长（秒）：10 年。既够用，又能挡住手滑写出的天文数字。</summary>
    public const int MaxSeconds = 315_360_000;

    /// <summary>默认值（秒）：1 天，与配置里的 <c>sync.defaultIntervalMinutes</c> 默认值一致。</summary>
    public const int DefaultSeconds = 86_400;

    /// <summary>写给用户看的单位说明。</summary>
    public const string Hint = "支持 30d / 24h / 1440m / 86400s，也可组合（1d12h、3m30s），不带单位按秒，如 3600";

    /// <summary>单位表：<c>分钟</c> 要排在 <c>分</c> 前、<c>minutes</c> 排在 <c>m</c> 前（这里用精确匹配，顺序只为可读）。</summary>
    private static readonly (string Suffix, long Factor)[] Units =
    [
        ("seconds", 1), ("second", 1), ("secs", 1), ("sec", 1), ("s", 1), ("秒", 1),
        ("minutes", 60), ("minute", 60), ("mins", 60), ("min", 60), ("m", 60), ("分钟", 60), ("分", 60),
        ("hours", 3600), ("hour", 3600), ("hrs", 3600), ("hr", 3600), ("h", 3600), ("小时", 3600), ("时", 3600),
        ("days", 86400), ("day", 86400), ("d", 86400), ("天", 86400),
    ];

    /// <summary>
    /// 把时长文本解析成秒，支持多段组合（<c>1d12h</c> / <c>3m30s</c> / <c>24小时</c>）。
    /// 每段 = 数字 + 单位，单位留空按秒；解析失败（空串、缺数字、单位未知、合计小于 1 秒或超出上限）返回 false。
    /// </summary>
    public static bool TryParseSeconds(string? text, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        var total = 0L;
        var index = 0;

        while (index < value.Length)
        {
            var digitsAt = index;
            while (index < value.Length && char.IsAsciiDigit(value[index])) index++;
            if (index == digitsAt) return false;    // 这一段前面没有数字

            if (!long.TryParse(value.AsSpan(digitsAt, index - digitsAt), NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return false;
            if (number <= 0) return false;

            var unitAt = index;
            while (index < value.Length && !char.IsAsciiDigit(value[index])) index++;

            var factor = UnitFactor(value[unitAt..index].Trim());
            if (factor is null) return false;

            // 单段就超上限直接失败：顺带挡住 number * factor 的溢出
            if (number > MaxSeconds / factor.Value) return false;

            total += number * factor.Value;
            if (total > MaxSeconds) return false;
        }

        if (total < 1) return false;

        seconds = (int)total;
        return true;
    }

    /// <summary>单位 → 秒倍数：空串按秒（裸数字），未知单位返回 null。</summary>
    private static long? UnitFactor(string suffix)
    {
        if (suffix.Length == 0) return 1;

        foreach (var (unit, factor) in Units)
        {
            if (string.Equals(unit, suffix, StringComparison.OrdinalIgnoreCase)) return factor;
        }

        return null;
    }

    /// <summary>把时长文本解析成秒（分钟配置项用）：成功返回秒，失败返回 null。</summary>
    public static int? ParseSeconds(string? text) => TryParseSeconds(text, out var seconds) ? seconds : null;

    /// <summary>
    /// 把秒数格式化成给人看的时长：<c>30d</c> / <c>3h</c> / <c>3m30s</c> / <c>45s</c>；
    /// 零值单位省略（如 86400+3600 → <c>1d1h</c>），0 秒返回 <c>0s</c>。
    /// <para>只用于人看的输出（<c>list</c> 的检查频率、命令行回执）；数据库与 JSON 仍是秒。</para>
    /// </summary>
    public static string Format(long seconds)
    {
        var value = seconds < 0 ? 0 : seconds;
        if (value == 0) return "0s";

        var text = string.Empty;

        var days = value / 86400;
        if (days > 0)
        {
            text += days + "d";
            value %= 86400;
        }

        var hours = value / 3600;
        if (hours > 0)
        {
            text += hours + "h";
            value %= 3600;
        }

        var minutes = value / 60;
        if (minutes > 0)
        {
            text += minutes + "m";
            value %= 60;
        }

        if (value > 0) text += value + "s";
        return text;
    }

    /// <summary>把分钟数换算成秒（配置里的 <c>defaultIntervalMinutes</c> → 数据库的秒）。</summary>
    public static int FromMinutes(int minutes) =>
        (int)Math.Clamp((long)Math.Max(1, minutes) * 60, 1, MaxSeconds);
}
