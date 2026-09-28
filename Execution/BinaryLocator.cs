namespace Byxcr.Execution;

/// <summary>在 PATH 中查找可执行文件（目前仅用于定位可选的 gzip / pigz 压缩程序）。</summary>
public static class BinaryLocator
{
    private static readonly string[] WindowsExtensions = [".exe", ".cmd", ".bat", ".com", ""];

    public static string? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var value = name.Trim().Trim('"');

        if (value.Contains('/') || value.Contains('\\'))
        {
            try
            {
                var full = Path.GetFullPath(value);
                return File.Exists(full) ? full : null;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        string[] extensions = OperatingSystem.IsWindows() ? WindowsExtensions : [""];
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = raw.Trim().Trim('"');
            if (directory.Length == 0) continue;
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory, value + extension);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // 忽略 PATH 中的非法路径片段
                }
            }
        }

        return null;
    }
}
