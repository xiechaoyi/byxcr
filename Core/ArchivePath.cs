namespace Byxcr.Core;

/// <summary>
/// 归档位置的<strong>对外</strong>表示：把数据库里记录的原始位置（本地归档的绝对路径，或 WebDAV 的完整地址）
/// 折叠成「相对归档根的路径」，例如 <c>library/alpine/3.20.tar</c>。
/// <para>WebAPI / Web 控制台等对外输出一律走这里，避免把 WebDAV 集合地址（含主机名与集合路径）
/// 或本机目录结构一并暴露给调用方；数据库与命令行仍保留原始位置，便于本机排障。</para>
/// </summary>
public static class ArchivePath
{
    /// <summary>
    /// 把 <paramref name="location"/> 折叠成相对归档根的路径（始终用 <c>/</c> 分隔）。
    /// <list type="bullet">
    /// <item><c>http(s)</c> 地址：去掉 <paramref name="webdavBase"/>（<c>storage.webdav.url</c>）前缀，
    /// 配置变更导致前缀对不上时退化为「只保留 URL 的路径部分」，同样不含主机名；</item>
    /// <item>本机路径：去掉 <paramref name="imageRoot"/>（<c>storage.imageRoot</c>）前缀；</item>
    /// <item>两者都不匹配时只回吐文件名——宁可少给信息，也不回吐绝对路径。</item>
    /// </list>
    /// 空值返回 null。
    /// </summary>
    public static string? Relative(string? location, string? webdavBase, string? imageRoot)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;

        var value = location.Trim();

        if (IsHttpUrl(value, out var uri))
        {
            var prefix = (webdavBase ?? string.Empty).Trim().TrimEnd('/');
            if (prefix.Length > 0
                && value.Length > prefix.Length
                && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return Clean(Unescape(value[prefix.Length..]));
            }

            // 集合地址改过（前缀对不上）：退化为只保留路径部分，主机名/凭据依然不外泄
            return Clean(Unescape(uri.AbsolutePath));
        }

        // 本机路径：去掉归档根前缀。两侧都要把分隔符统一成 '/' —— 库里的值可能来自 Windows（反斜杠）
        // 或 Linux / 容器内（正斜杠），不能直接按字符串比前缀。
        var normalized = value.Replace('\\', '/');
        var root = (imageRoot ?? string.Empty).Trim().TrimEnd('\\', '/').Replace('\\', '/');
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (root.Length > 0
            && normalized.Length > root.Length + 1
            && normalized.StartsWith(root, comparison)
            && normalized[root.Length] == '/')
        {
            return Clean(normalized[(root.Length + 1)..]);
        }

        // 归档根也变更过：只给文件名（按 '/' 取，避免在 Linux 上把反斜杠路径整串吐出来）
        return Clean(normalized[(normalized.LastIndexOf('/') + 1)..]);
    }

    private static bool IsHttpUrl(string value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;

        uri = parsed;
        return true;
    }

    private static string Unescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (Exception)
        {
            return value;
        }
    }

    /// <summary>统一分隔符为 <c>/</c>、去掉首尾斜杠与空白；空结果按「无路径」处理。</summary>
    private static string? Clean(string value)
    {
        var text = value.Replace('\\', '/').Trim().Trim('/');
        while (text.Contains("//", StringComparison.Ordinal)) text = text.Replace("//", "/");
        return text.Length == 0 ? null : text;
    }
}
