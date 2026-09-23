using Byxcr.Core;

namespace Byxcr.Cli;

/// <summary>命令行参数解析结果。</summary>
public sealed class CliOptions
{
    public string Command { get; private set; } = "run";

    /// <summary>用户是否显式给出了子命令；为 false 时主流程只打印用法。</summary>
    public bool HasCommand => _commandSet;

    public List<string> Positionals { get; } = [];

    public string? ConfigPath { get; private set; }

    public bool Json { get; private set; }

    public bool Verbose { get; private set; }

    public bool Force { get; private set; }

    /// <summary>sync -l：本次同步强制归档到本地目录，忽略 WebDAV 设置。</summary>
    public bool Local { get; private set; }

    public bool Help { get; private set; }

    /// <summary>add --no-check：跳过「仓库与标签能否拉取」的预检，直接写入数据库。</summary>
    public bool NoCheck { get; private set; }

    /// <summary>records --clear：清除 3 天前的同步记录。</summary>
    public bool Clear { get; private set; }

    /// <summary>检查频率（秒）：-i/--interval 支持 30d / 24h / 1440m / 86400s，不带单位按秒。</summary>
    public int? IntervalSeconds { get; private set; }

    /// <summary>输出位置：归档文件保存路径（相对归档根，无扩展名自动补 .tar）。</summary>
    public string? Output { get; private set; }

    public int? Limit { get; private set; }

    public bool? Enabled { get; private set; }

    private bool _commandSet;

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // 支持 --key=value
            string? inlineValue = null;
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var equals = arg.IndexOf('=');
                if (equals > 2)
                {
                    inlineValue = arg[(equals + 1)..];
                    arg = arg[..equals];
                }
            }

            switch (arg.ToLowerInvariant())
            {
                case "-h" or "--help" or "help" or "-?":
                    options.Help = true;
                    continue;

                case "-c" or "--config":
                    options.ConfigPath = inlineValue ?? TakeValue(args, ref i, arg);
                    continue;

                case "-j" or "--json":
                    options.Json = true;
                    continue;

                case "-v" or "--verbose":
                    options.Verbose = true;
                    continue;

                case "-f" or "--force":
                    options.Force = true;
                    continue;

                case "-l" or "--local":
                    options.Local = true;
                    continue;

                case "--no-check" or "--skip-check":
                    options.NoCheck = true;
                    continue;

                case "--clear":
                    options.Clear = true;
                    continue;

                case "-i" or "--interval":
                    options.IntervalSeconds = ParseDuration(inlineValue ?? TakeValue(args, ref i, arg), arg);
                    continue;

                case "-o" or "--output" or "--out":
                    options.Output = inlineValue ?? TakeValue(args, ref i, arg);
                    continue;

                case "--limit":
                    options.Limit = ParseInt(inlineValue ?? TakeValue(args, ref i, arg), arg);
                    continue;

                case "--enable" or "--enabled":
                    options.Enabled = true;
                    continue;

                case "--disable" or "--disabled":
                    options.Enabled = false;
                    continue;

                case "--version":
                    options.Help = false;
                    if (!options._commandSet)
                    {
                        options.Command = "version";
                        options._commandSet = true;
                    }
                    continue;

                default:
                    if (arg.StartsWith('-') && arg.Length > 1)
                        throw new ConfigurationException($"未知选项：{arg}");

                    if (!options._commandSet)
                    {
                        options.Command = arg.ToLowerInvariant();
                        options._commandSet = true;
                    }
                    else
                    {
                        options.Positionals.Add(arg);
                    }
                    continue;
            }
        }

        return options;
    }

    private static string TakeValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
            throw new ConfigurationException($"选项 {option} 缺少取值");

        index++;
        return args[index];
    }

    private static int ParseDuration(string value, string option)
    {
        if (Duration.TryParseSeconds(value, out var seconds)) return seconds;

        throw new ConfigurationException($"选项 {option} 的时长无效：{value}（{Duration.Hint}）");
    }

    private static int ParseInt(string value, string option)
    {
        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var result))
            return result;

        throw new ConfigurationException($"选项 {option} 需要整数，实际为：{value}");
    }
}
