using System.Globalization;

namespace Byxcr.Core;

/// <summary>通用格式化工具（体积 / 时间 / 摘要），供 CLI、日志与原生下载器共用。</summary>
public static class Format
{
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>把字节数格式化为 1.2 MB 这样的可读文本。</summary>
    public static string Size(long bytes)
    {
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < SizeUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {SizeUnits[unit]}";
    }

    /// <summary>截断摘要（去掉算法前缀并保留前 12 位）。</summary>
    public static string ShortDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return "-";
        var index = digest.IndexOf(':', StringComparison.Ordinal);
        var value = index >= 0 ? digest[(index + 1)..] : digest;
        return value.Length > 12 ? value[..12] : value;
    }

    /// <summary>去掉摘要的算法前缀（sha256:abc → abc）。</summary>
    public static string DigestHex(string digest)
    {
        var index = digest.IndexOf(':', StringComparison.Ordinal);
        return index >= 0 ? digest[(index + 1)..] : digest;
    }

    /// <summary>毫秒转为 1.2s 形式。</summary>
    public static string Duration(long milliseconds)
        => (milliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s";

    /// <summary>按总大小与已下载大小拼出进度后缀，如「 42%（15.2 MB / 36.1 MB）」；总大小未知时返回空串。</summary>
    public static string Progress(long downloaded, long total)
    {
        if (total <= 0) return string.Empty;

        var percent = downloaded >= total ? 100 : (int)(downloaded * 100 / total);
        return $" {percent}%（{Size(downloaded)} / {Size(total)}）";
    }

    /// <summary>去重后的平台列表，保持出现顺序。</summary>
    public static List<string> DistinctPlatforms(IEnumerable<string> platforms)
        => platforms
            .Where(platform => !string.IsNullOrWhiteSpace(platform))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 平台摘要文本：去重后用「 · 」连接，超过 3 个时折叠为数量（如「8 个平台」）。没有平台信息时返回空串。
    /// 下载结果与 add 前的预检共用同一套写法。
    /// </summary>
    public static string PlatformText(IEnumerable<string> platforms)
    {
        var list = DistinctPlatforms(platforms);
        return list.Count switch
        {
            0 => string.Empty,
            <= 3 => string.Join(" · ", list),
            var count => $"{count} 个平台",
        };
    }
}
