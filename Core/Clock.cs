using System.Globalization;

namespace Byxcr.Core;

/// <summary>
/// 统一的时间处理：落库一律存 UTC（ISO-8601，可直接做文本比较），**展示时换算成系统时区**。
/// 显示时区默认取系统时区（Windows 注册表 / Linux 的 TZ 与 /etc/localtime），
/// 也可由 `log.timeZone`（环境变量 `BYXCR_TZ`）显式指定。
/// </summary>
public static class Clock
{
    public const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private static TimeZoneInfo _zone = TimeZoneInfo.Local;

    /// <summary>当前用于展示的时区。</summary>
    public static TimeZoneInfo Zone => _zone;

    /// <summary>
    /// 常用 IANA 名称 → Windows 时区名。运行时为 InvariantGlobalization（不加载 ICU），
    /// Windows 上无法自行完成 IANA 映射，因此这里自备对照；查不到时再按固定偏移解析。
    /// </summary>
    private static readonly (string Iana, string System)[] IanaToSystem =
    [
        ("UTC", "UTC"), ("Etc/UTC", "UTC"), ("Etc/GMT", "UTC"), ("GMT", "UTC"), ("Zulu", "UTC"),
        ("Asia/Shanghai", "China Standard Time"),
        ("Asia/Chongqing", "China Standard Time"),
        ("Asia/Harbin", "China Standard Time"),
        ("Asia/Urumqi", "Central Asia Standard Time"),
        ("Asia/Hong_Kong", "Hong Kong Standard Time"),
        ("Asia/Macau", "Macao Standard Time"),
        ("Asia/Taipei", "Taipei Standard Time"),
        ("Asia/Tokyo", "Tokyo Standard Time"),
        ("Asia/Seoul", "Korea Standard Time"),
        ("Asia/Singapore", "Singapore Standard Time"),
        ("Asia/Manila", "Singapore Standard Time"),
        ("Asia/Bangkok", "SE Asia Standard Time"),
        ("Asia/Jakarta", "SE Asia Standard Time"),
        ("Asia/Kolkata", "India Standard Time"),
        ("Asia/Calcutta", "India Standard Time"),
        ("Asia/Karachi", "Pakistan Standard Time"),
        ("Asia/Dubai", "Arabian Standard Time"),
        ("Australia/Sydney", "AUS Eastern Standard Time"),
        ("Australia/Perth", "W. Australia Standard Time"),
        ("Europe/London", "GMT Standard Time"),
        ("Europe/Dublin", "GMT Standard Time"),
        ("Europe/Lisbon", "GMT Standard Time"),
        ("Europe/Paris", "Romance Standard Time"),
        ("Europe/Berlin", "W. Europe Standard Time"),
        ("Europe/Amsterdam", "W. Europe Standard Time"),
        ("Europe/Madrid", "Romance Standard Time"),
        ("Europe/Rome", "W. Europe Standard Time"),
        ("Europe/Zurich", "W. Europe Standard Time"),
        ("Europe/Stockholm", "W. Europe Standard Time"),
        ("Europe/Vienna", "W. Europe Standard Time"),
        ("Europe/Prague", "Central Europe Standard Time"),
        ("Europe/Warsaw", "Central European Standard Time"),
        ("Europe/Athens", "GTB Standard Time"),
        ("Europe/Moscow", "Russian Standard Time"),
        ("Europe/Kyiv", "FLE Standard Time"),
        ("America/New_York", "Eastern Standard Time"),
        ("America/Toronto", "Eastern Standard Time"),
        ("America/Chicago", "Central Standard Time"),
        ("America/Denver", "Mountain Standard Time"),
        ("America/Phoenix", "US Mountain Standard Time"),
        ("America/Los_Angeles", "Pacific Standard Time"),
        ("America/Vancouver", "Pacific Standard Time"),
        ("America/Sao_Paulo", "E. South America Standard Time"),
        ("America/Mexico_City", "Central Standard Time (Mexico)"),
    ];

    /// <summary>当前展示时区的标识，例如 China Standard Time 或 Asia/Shanghai。</summary>
    public static string ZoneId => _zone.Id;

    /// <summary>展示时区相对 UTC 的偏移，例如 +08:00。</summary>
    public static string ZoneOffset
    {
        get
        {
            var offset = _zone.GetUtcOffset(DateTime.UtcNow);
            var absolute = offset < TimeSpan.Zero ? offset.Negate() : offset;
            return $"{(offset < TimeSpan.Zero ? '-' : '+')}{absolute.Hours:00}:{absolute.Minutes:00}";
        }
    }

    /// <summary>
    /// 设定展示时区：优先显式配置（<c>log.timeZone</c> / <c>BYXCR_TZ</c>），其次环境变量 <c>TZ</c>，否则保持系统时区。
    /// 名称无法解析时（典型情况：Alpine 未安装 tzdata，系统里没有 zoneinfo）回退系统时区，并返回提示文本；
    /// 同时支持 <c>+08:00</c>、<c>UTC+8</c>、<c>GMT-5</c> 这类固定偏移写法，便于无 tzdata 的环境。
    /// </summary>
    public static string? UseTimeZone(string? configured)
    {
        var id = !string.IsNullOrWhiteSpace(configured)
            ? configured.Trim()
            : ReadEnvironment("TZ");

        if (string.IsNullOrWhiteSpace(id))
        {
            _zone = TimeZoneInfo.Local;
            return null;
        }

        if (TryResolve(id, out var zone))
        {
            _zone = zone!;
            return null;
        }

        _zone = TimeZoneInfo.Local;
        return $"时区 {id} 无法解析，已回退到系统时区 {TimeZoneInfo.Local.Id}（{ZoneOffset}）；"
             + "Alpine 等精简镜像请先安装 tzdata，或改用 log.timeZone 的固定偏移写法（如 +08:00）";
    }

    /// <summary>把时区名称解析成 <see cref="TimeZoneInfo"/>：先按系统时区库查，再查 IANA→系统名对照表，最后退化到固定偏移写法。</summary>
    public static bool TryResolve(string id, out TimeZoneInfo? zone)
    {
        zone = null;
        if (string.IsNullOrWhiteSpace(id)) return false;

        var name = id.Trim();

        if (TryFind(name, out zone)) return true;

        // 运行时是 InvariantGlobalization，Windows 上拿不到 ICU 的 IANA↔系统名映射表，这里自备常用对照
        foreach (var (iana, windows) in IanaToSystem)
        {
            if (!string.Equals(iana, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (TryFind(windows, out zone)) return true;
        }

        if (!TryParseOffset(name, out var offset)) return false;
        zone = TimeZoneInfo.CreateCustomTimeZone($"UTC{offset:hh\\:mm}", offset, name, name);
        return true;
    }

    private static bool TryFind(string id, out TimeZoneInfo? zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException or FormatException)
        {
            // InvariantGlobalization 下未知名称可能抛 FormatException，统一按「查不到」处理
            zone = null;
            return false;
        }
    }

    /// <summary>当前时间（展示时区）。</summary>
    public static DateTime NowLocal() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _zone);

    /// <summary>当前时间的本地展示文本。</summary>
    public static string NowLocalText() => NowLocal().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>当前时刻的 UTC 文本（落库用）。</summary>
    public static string Now() => Format(DateTime.UtcNow);

    public static string Format(DateTime utc) => utc.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);

    public static DateTime? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
            ? dt
            : null;
    }

    /// <summary>把库里的 UTC 文本换算成展示时区，便于人工阅读。</summary>
    public static string Local(string? value)
    {
        var parsed = Parse(value);
        return parsed is null ? "-" : Local(parsed.Value);
    }

    public static string Local(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(utc.ToUniversalTime(), _zone)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>解析 +08:00 / UTC+8 / GMT-5:30 这类固定偏移。</summary>
    private static bool TryParseOffset(string value, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;

        var text = value.Trim().ToUpperInvariant();
        if (text.StartsWith("UTC", StringComparison.Ordinal)) text = text[3..];
        else if (text.StartsWith("GMT", StringComparison.Ordinal)) text = text[3..];
        if (text.Length == 0) return false;

        var negative = text[0] is '-';
        if (text[0] is '+' or '-') text = text[1..];

        var parts = text.Split(':', 2);
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)) return false;

        var minutes = 0;
        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes)) return false;
        }
        else if (text.Length > 2)
        {
            // +0830 形式
            if (!int.TryParse(text[2..], NumberStyles.None, CultureInfo.InvariantCulture, out minutes)) return false;
            hours = int.Parse(text[..2], CultureInfo.InvariantCulture);
        }

        if (hours > 14 || minutes > 59) return false;

        offset = new TimeSpan(hours, minutes, 0);
        if (negative) offset = -offset;
        return true;
    }

    private static string? ReadEnvironment(string name)
    {
        try
        {
            return Environment.GetEnvironmentVariable(name)?.Trim();
        }
        catch (Exception)
        {
            // 环境变量不可读时按未设置处理
            return null;
        }
    }
}
