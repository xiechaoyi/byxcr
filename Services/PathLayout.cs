using Byxcr.Configuration;
using Byxcr.Core;

namespace Byxcr.Services;

/// <summary>统一的路径布局。归档按「仓库（非 Docker Hub 时含主机名）+ tag 文件名」存放。</summary>
public sealed class PathLayout
{
    /// <summary>Linux/macOS 上日志目录的默认位置（Windows 用 {数据目录}/logs）。</summary>
    public const string UnixLogDirectory = "/var/log/byxcr";

    public PathLayout(StorageConfig storage, LogConfig? log = null)
    {
        DataDir = storage.DataDir;
        DatabaseFile = storage.DatabaseFile;
        ImageRoot = storage.ImageRoot;
        TempDir = storage.TempDir;
        KeepTar = storage.KeepTar;
        LogRoot = ResolveLogRoot(storage.DataDir, log?.Directory);
    }

    public string DataDir { get; }

    public string DatabaseFile { get; }

    public string ImageRoot { get; }

    public string TempDir { get; }

    public bool KeepTar { get; }

    /// <summary>日志输出目录（后台下载日志等）：Linux 默认 /var/log/byxcr，Windows 默认 {数据目录}/logs。</summary>
    public string LogRoot { get; }

    /// <summary>
    /// 解析日志目录：显式配置 <c>log.directory</c> 优先（相对路径按数据目录解析）；
    /// 未配置时 Linux/macOS 用 <see cref="UnixLogDirectory"/>，Windows 用 <c>{数据目录}/logs</c>。
    /// </summary>
    public static string ResolveLogRoot(string dataDir, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var directory = configured.Trim();
            return Path.IsPathRooted(directory) ? directory : Path.GetFullPath(Path.Combine(dataDir, directory));
        }

        return OperatingSystem.IsWindows() ? Path.Combine(dataDir, "logs") : UnixLogDirectory;
    }

    /// <summary>最终归档路径，例如 {ImageRoot}/library/mysql/5.6.tar。</summary>
    public string ArchiveFile(ImageReference image) => Path.Combine(ImageRoot, ToNative(image.RelativeFilePath));

    /// <summary>
    /// 中间 tar 路径，例如 {TempDir}/library/mysql/5.6.12345.tar。
    /// 末尾是进程号：手动 sync 不受同步租约限制，可能与其它进程同时下载同一镜像，各自的中间文件必须分开。
    /// </summary>
    public string TempTarFile(ImageReference image)
        => Path.Combine(TempDir, ToNative(image.RelativeDirectory), $"{image.Tag}.{ProcessTag}.tar");

    /// <summary>OCI 下载的临时工作目录，例如 {TempDir}/library/mysql/5.6.12345.oci。</summary>
    public string PullWorkDirectory(ImageReference image)
        => Path.Combine(TempDir, ToNative(image.RelativeDirectory), $"{image.Tag}.{ProcessTag}.oci");

    private static string ProcessTag => Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string EnsureDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        return directory ?? string.Empty;
    }

    /// <summary>创建数据目录、归档根目录、临时目录与日志目录。</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ImageRoot);
        Directory.CreateDirectory(TempDir);
        var databaseDirectory = Path.GetDirectoryName(DatabaseFile);
        if (!string.IsNullOrEmpty(databaseDirectory)) Directory.CreateDirectory(databaseDirectory);

        try
        {
            Directory.CreateDirectory(LogRoot);
        }
        catch (Exception)
        {
            // 日志目录不可写（如容器里没有权限）不影响主流程，写入时再退化为数据目录下的 logs
        }
    }

    /// <summary>
    /// 清理临时目录里用完的空目录骨架：从 <paramref name="path"/> 的父目录向上回溯，
    /// 逐个删除空目录，直到 <see cref="TempDir"/> 为止（不删除 TempDir 本身）。
    /// 属于尽力而为的清理，失败时静默忽略。
    /// </summary>
    public void PruneEmptyDirectories(string path)
    {
        try
        {
            var root = Path.GetFullPath(TempDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = Path.GetDirectoryName(Path.GetFullPath(path));

            while (!string.IsNullOrEmpty(current)
                   && current.Length > root.Length
                   && current.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.EnumerateFileSystemEntries(current).Any()) break;
                Directory.Delete(current, recursive: false);
                current = Path.GetDirectoryName(current);
            }
        }
        catch (Exception)
        {
            // 忽略：临时目录清理失败不影响同步结果
        }
    }

    private static string ToNative(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);
}
