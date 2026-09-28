using System.Globalization;
using Byxcr.Core;

namespace Byxcr.Logging;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
    None = 9,
}

/// <summary>轻量级线程安全日志：控制台（带颜色）+ 可选文件。</summary>
public static class Log
{
    /// <summary>原地刷新进度行的最小间隔（毫秒），避免高频刷新拖慢输出。</summary>
    private const int ProgressMinIntervalMs = 200;

    /// <summary>无法原地刷新时（输出被重定向）每多少秒补一条完整进度行。</summary>
    private const int ProgressFallbackSeconds = 5;

    private static readonly object Gate = new();
    private static LogLevel _min = LogLevel.Info;
    private static bool _color = true;
    private static StreamWriter? _writer;

    private static string? _progressText;
    private static object? _progressOwner;
    private static long _progressTick;
    private static long _progressFallbackTick;

    public static LogLevel MinLevel => _min;

    /// <summary>是否允许原地刷新进度行；把控制台整体重定向到日志文件时置为 false。</summary>
    public static bool ProgressEnabled { get; set; } = true;

    /// <summary>
    /// 输出是否被接到「无人实时观看」的目标：后台同步日志文件、或 shell 重定向的文件/管道。
    /// 为 true 时**完全不输出**进度行与完成行——既无法原地刷新，也不该在日志里留下一串百分比；
    /// 结果请以 SQLite 里的同步记录为准（<c>byxcr records</c>）。
    /// </summary>
    public static bool BatchOutput { get; set; } = Console.IsOutputRedirected;

    public static void Configure(string? level, string? file, bool color)
    {
        lock (Gate)
        {
            _min = ParseLevel(level);
            _color = color;

            if (_writer is not null)
            {
                try { _writer.Flush(); _writer.Dispose(); } catch { /* ignore */ }
                _writer = null;
            }

            if (!string.IsNullOrWhiteSpace(file))
            {
                var path = Path.GetFullPath(file);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true,
                };
            }
        }
    }

    public static LogLevel ParseLevel(string? value) => (value ?? "info").Trim().ToLowerInvariant() switch
    {
        "debug" or "verbose" or "trace" => LogLevel.Debug,
        "warn" or "warning" => LogLevel.Warn,
        "error" or "err" => LogLevel.Error,
        "none" or "off" or "silent" => LogLevel.None,
        _ => LogLevel.Info,
    };

    public static bool IsEnabled(LogLevel level) => level >= _min;

    public static void Debug(string message) => Write(LogLevel.Debug, message, ConsoleColor.DarkGray);
    public static void Info(string message) => Write(LogLevel.Info, message, ConsoleColor.Gray);
    public static void Ok(string message) => Write(LogLevel.Info, message, ConsoleColor.Green);
    public static void Warn(string message) => Write(LogLevel.Warn, message, ConsoleColor.Yellow);
    public static void Error(string message) => Write(LogLevel.Error, message, ConsoleColor.Red);
    public static void Note(string message) => Write(LogLevel.Info, message, ConsoleColor.Cyan);

    /// <summary>输出不带前缀的原文行，用于表格等结构化内容。</summary>
    public static void Raw(string message)
    {
        lock (Gate)
        {
            ClearProgressLocked();
            Console.Out.WriteLine(message);
            _writer?.WriteLine(message);
        }
    }

    /// <summary>
    /// 开始一行可就地刷新的进度：交互式终端下**只写出首行文本且不换行**，随后
    /// <see cref="ProgressLine.Update"/> 用 <c>\r</c> 回到行首原地覆盖，因此日志里不会出现
    /// 「无进度的首行 + 带进度的第二行」这种重复；<see cref="ProgressLine.Dispose"/> 擦除该行，
    /// 让后续输出落在干净的一行上。
    /// 同一时刻只允许一行原地刷新（并行同步时其余退化为完整行）；输出被重定向（后台下载 / 管道）时
    /// 按 <see cref="ProgressFallbackSeconds"/> 秒节流打印完整行。文件日志不记录进度。
    /// </summary>
    public static ProgressLine BeginProgress(string message, ConsoleColor color = ConsoleColor.Gray)
    {
        var line = new ProgressLine(BuildPrefix(LogLevel.Info), message);

        // 面向文件/管道的输出：整行都不写，避免日志里留下一串百分比（见 BatchOutput 说明）
        if (BatchOutput) return line;

        string? inline = null;

        lock (Gate)
        {
            if (_progressOwner is null)
            {
                _progressOwner = line;
                if (ProgressEnabled && !Console.IsOutputRedirected)
                {
                    var text = FitToConsole(line.Prefix + message);
                    var useColor = _color;
                    var prev = ConsoleColor.Gray;
                    try
                    {
                        if (useColor)
                        {
                            prev = Console.ForegroundColor;
                            Console.ForegroundColor = color;
                        }
                        Console.Out.Write(text);
                        inline = text;
                    }
                    catch (IOException)
                    {
                        // 控制台不可用时退回整行输出
                    }
                    finally
                    {
                        if (useColor)
                        {
                            try { Console.ForegroundColor = prev; } catch (IOException) { /* ignore */ }
                        }
                    }
                }
            }
        }

        if (inline is null)
        {
            Write(LogLevel.Info, message, color);
            // 重定向场景从此刻起算 5 秒节流，避免开局就打一条「0%」的假进度
            _progressFallbackTick = Environment.TickCount64;
            return line;
        }

        var now = Environment.TickCount64;
        _progressText = inline;
        _progressTick = now;
        _progressFallbackTick = now;
        return line;
    }

    /// <summary>收尾当前进度行：补一个换行把它固定下来，后续输出从下一行开始。</summary>
    public static void EndProgress()
    {
        lock (Gate)
        {
            ClearProgressLocked(commit: true);
        }
    }

    internal static void UpdateProgress(ProgressLine owner, string prefix, string message)
    {
        if (BatchOutput || !IsEnabled(LogLevel.Info)) return;

        var now = Environment.TickCount64;

        // 可交互终端：回到行首重写同一行（时间戳保持首次输出时的值）
        if (ProgressEnabled && !Console.IsOutputRedirected && ReferenceEquals(_progressOwner, owner))
        {
            if (now - _progressTick < ProgressMinIntervalMs) return;
            _progressTick = now;

            var text = FitToConsole(prefix + message);
            lock (Gate)
            {
                try
                {
                    var pad = Math.Max(0, (_progressText?.Length ?? 0) - text.Length);
                    Console.Out.Write('\r');
                    Console.Out.Write(text);
                    if (pad > 0) Console.Out.Write(new string(' ', pad));
                    _progressText = text;
                }
                catch (IOException)
                {
                    // 控制台不可用时静默忽略
                }
            }

            return;
        }

        // 无法原地刷新：每隔几秒补一条完整行，便于日志里也能看到进度
        if (now - _progressFallbackTick < ProgressFallbackSeconds * 1000) return;
        _progressFallbackTick = now;
        Write(LogLevel.Info, message, ConsoleColor.Gray);
    }

    /// <summary>
    /// 用一句结论文本**原地替换**当前进度行（沿用该行的时间戳与前缀），随后补换行收尾；
    /// 无法原地刷新（输出被重定向）时退化为普通整行输出。调用后该进度行即结束。
    /// </summary>
    internal static void CompleteProgress(ProgressLine owner, string prefix, string message, ConsoleColor color)
    {
        if (!IsEnabled(LogLevel.Info)) return;

        // 批量输出（后台日志 / 重定向）：进度帧全部抑制，但完成结论必须留一条普通整行，
        // 否则日志里只有「开始下载」没有结果
        if (BatchOutput)
        {
            Write(LogLevel.Info, message, color);
            return;
        }

        var inline = false;
        lock (Gate)
        {
            if (_progressText is not null && ProgressEnabled && !Console.IsOutputRedirected && ReferenceEquals(_progressOwner, owner))
            {
                var text = FitToConsole(prefix + message);
                var useColor = _color;
                var prev = ConsoleColor.Gray;
                try
                {
                    if (useColor)
                    {
                        prev = Console.ForegroundColor;
                        Console.ForegroundColor = color;
                    }

                    var pad = Math.Max(0, _progressText.Length - text.Length);
                    Console.Out.Write('\r');
                    Console.Out.Write(text);
                    if (pad > 0) Console.Out.Write(new string(' ', pad));
                    Console.Out.Write('\n');
                    inline = true;
                }
                catch (IOException)
                {
                    // 控制台不可用时退回整行输出
                }
                finally
                {
                    if (useColor)
                    {
                        try { Console.ForegroundColor = prev; } catch (IOException) { /* ignore */ }
                    }
                }

                _progressText = null;
            }
        }

        if (!inline) Write(LogLevel.Info, message, color);
    }

    private static void Write(LogLevel level, string message, ConsoleColor color)
    {
        if (!IsEnabled(level)) return;

        var now = Clock.NowLocal();
        var tag = Tag(level);

        lock (Gate)
        {
            // 进度行尚未结束就先擦掉，避免新日志叠在半行上
            ClearProgressLocked();

            var useColor = _color && !Console.IsOutputRedirected;
            var prev = ConsoleColor.Gray;
            try
            {
                if (useColor)
                {
                    prev = Console.ForegroundColor;
                    Console.ForegroundColor = color;
                }
                Console.Out.Write('[');
                Console.Out.Write(now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                Console.Out.Write(' ');
                Console.Out.Write(tag);
                Console.Out.Write("] ");
                Console.Out.WriteLine(message);
            }
            catch (IOException)
            {
                // 控制台不可用时静默忽略
            }
            finally
            {
                if (useColor)
                {
                    try { Console.ForegroundColor = prev; } catch (IOException) { /* ignore */ }
                }
            }

            _writer?.WriteLine($"[{now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {tag}] {message}");
        }
    }

    /// <summary>擦掉未结束的进度行（调用方须持有 <see cref="Gate"/>）。<paramref name="commit"/> 为 true 时保留该行并补换行。</summary>
    private static void ClearProgressLocked(bool commit = false)
    {
        if (_progressText is null) return;

        try
        {
            if (commit)
            {
                Console.Out.Write('\n');
            }
            else
            {
                Console.Out.Write('\r');
                Console.Out.Write(new string(' ', _progressText.Length));
                Console.Out.Write('\r');
            }
        }
        catch (IOException)
        {
            // 忽略
        }

        _progressText = null;
    }

    private static void ReleaseOwner(ProgressLine line)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_progressOwner, line)) _progressOwner = null;
        }
    }

    private static string BuildPrefix(LogLevel level)
        => $"[{Clock.NowLocal().ToString("HH:mm:ss", CultureInfo.InvariantCulture)} {Tag(level)}] ";

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "INF",
    };

    /// <summary>按终端宽度裁剪，避免折行后 <c>\r</c> 擦不净上一行（宽字符按 2 列计）。</summary>
    private static string FitToConsole(string text)
    {
        int width;
        try { width = Console.BufferWidth; }
        catch (IOException) { return text; }

        if (width <= 8) return text;

        var limit = width - 1;
        if (DisplayWidth(text) <= limit) return text;

        var length = text.Length;
        while (length > 0 && DisplayWidth(text[..length]) > limit) length--;
        return text[..length];
    }

    private static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var ch in text) width += ch >= 0x1100 ? 2 : 1;
        return width;
    }

    /// <summary>一行可就地刷新的进度输出。</summary>
    public sealed class ProgressLine : IDisposable
    {
        private readonly string _prefix;
        private readonly string _message;
        private bool _finished;

        internal ProgressLine(string prefix, string message)
        {
            _prefix = prefix;
            _message = message;
        }

        /// <summary>日志前缀（含时间戳与级别），原地刷新时用它保证时间戳不变。</summary>
        internal string Prefix => _prefix;

        /// <summary>刷新进度后缀（如「 42%（15.2 MB / 36.1 MB）」）。</summary>
        public void Update(string suffix)
        {
            if (_finished) return;
            UpdateProgress(this, _prefix, _message + suffix);
        }

        /// <summary>
        /// 用一句结论文本**原地替换**这一行（如把进度行换成「完成下载 → …」），随后收尾；
        /// 该行的时间戳保持不变，不会再多打一行。
        /// </summary>
        public void Complete(string message, ConsoleColor color = ConsoleColor.Gray)
        {
            if (_finished) return;
            _finished = true;
            CompleteProgress(this, _prefix, message, color);
            ReleaseOwner(this);
            EndProgress();
        }

        public void Dispose()
        {
            if (_finished) return;
            _finished = true;
            ReleaseOwner(this);
            EndProgress();
        }
    }
}
