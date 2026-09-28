using System.Text;
using Byxcr.Cli;
using Byxcr.Logging;

namespace Byxcr;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (Exception)
        {
            // 输出被重定向或控制台不可用时忽略
        }

        // add 拉起的后台下载子进程：先把输出整体接到日志文件，进程可在父进程/终端退出后继续下载
        BackgroundConsole.TryRedirect();

        try
        {
            return await CliApp.RunAsync(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"致命错误：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
