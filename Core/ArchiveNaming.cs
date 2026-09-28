namespace Byxcr.Core;

/// <summary>
/// 归档文件落盘位置的计算：默认按「仓库主机名 + 仓库名 / tag.tar」的规则，
/// 任务上指定了输出位置（<see cref="ImageTask.Output"/>）时改用给定路径。
/// </summary>
public static class ArchiveNaming
{
    public const string Extension = ".tar";

    /// <summary>
    /// 解析归档相对路径（始终用 <c>/</c> 分隔，可直接交给 <c>IArchiveSink</c>）。
    /// 输出位置为空 → 默认规则；有输出位置时按它来，并在下面两种情况下补全文件名：
    /// 以 <c>/</c> 结尾（当成目录）补默认文件名；没有扩展名时补 <c>.tar</c>。
    /// <paramref name="asDirectory"/> 为 true 时（一次同步多个镜像），把输出位置**当成目录**，
    /// 其下沿用默认的「仓库/镜像/tag.tar」结构，避免多个镜像写到同一个文件。
    /// </summary>
    public static string Resolve(ImageReference image, string? output, bool asDirectory = false)
    {
        if (string.IsNullOrWhiteSpace(output)) return image.RelativeFilePath;

        var text = output.Trim().Replace('\\', '/').Trim();
        if (text.Length == 0) return image.RelativeFilePath;

        // 绝对路径 / 盘符：去掉根，避免拼到归档目录上时把 imageRoot 覆盖掉
        text = StripRoot(text);

        // 多镜像：输出位置是目录，每个镜像在其下按默认规则各占一个文件
        if (asDirectory) return Sanitize(text.TrimEnd('/') + "/" + image.RelativeFilePath);

        // 以 / 结尾视为目录：补上默认文件名
        if (text.EndsWith('/')) text += image.FileName;

        // 没有扩展名时补 .tar（有扩展名则原样保留，如 .tgz）
        if (Path.GetExtension(text).Length == 0) text += Extension;

        return Sanitize(text);
    }

    private static string StripRoot(string text)
    {
        if (text.Length >= 2 && text[1] == ':') text = text[2..];          // C:/xxx
        return text.TrimStart('/');
    }

    /// <summary>逐段清理路径：非法字符替换为 _，去掉空段与 . 段（保留目录结构）。</summary>
    private static string Sanitize(string path)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cleaned = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            var chars = segment.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] == '\\' || Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            }

            var value = new string(chars).Trim(' ', '.');
            if (value.Length == 0 || value == ".") continue;
            cleaned.Add(value);
        }

        return cleaned.Count == 0 ? path : string.Join('/', cleaned);
    }
}
