namespace Byxcr.Logging;

/// <summary>
/// 后台下载子进程的控制台接管。<c>add</c> 会以 <c>BYXCR_BACKGROUND_LOG</c> 指定日志文件、拉一个完全独立的
/// <c>byxcr sync …</c> 子进程；子进程启动后立刻把 stdout/stderr 接到该文件上，
/// 这样它既不占用父进程的终端（父进程可以马上退出、用户继续输入下一条命令），
/// 也不会因为终端关闭而被一起带走。
/// </summary>
public static class BackgroundConsole
{
    /// <summary>父进程用来指定后台日志文件的环境变量名。</summary>
    public const string LogFileVariable = "BYXCR_BACKGROUND_LOG";

    /// <summary>当前进程是否由 add 拉起的后台下载子进程。</summary>
    public static bool IsBackground => LogPath is not null;

    /// <summary>后台日志文件路径；未设置时返回 null。</summary>
    public static string? LogPath
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(LogFileVariable);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    /// <summary>把控制台输出接到日志文件；返回是否接管成功。</summary>
    public static bool TryRedirect()
    {
        var path = LogPath;
        if (path is null) return false;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var sink = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true,
            };

            Console.SetOut(sink);
            Console.SetError(sink);

            // 输出已经去了日志文件：既无法回到行首刷进度，也不该在日志里堆百分比
            Log.ProgressEnabled = false;
            Log.BatchOutput = true;
            return true;
        }
        catch (Exception)
        {
            // 日志文件不可写时保持原样输出，不影响下载
            return false;
        }
    }
}
