using Byxcr.Core;

namespace Byxcr.Services;

/// <summary>一次归档写入的结果。</summary>
public sealed record ArchiveWriteResult(string Location, long Size);

/// <summary>归档目标探测结果。Size 为已知体积，未知时为 null。</summary>
public readonly record struct ArchiveProbe(bool Exists, long? Size);

/// <summary>
/// 归档目的地。默认写本地目录（<c>storage.imageRoot</c>），
/// 也可以配置为 WebDAV 集合（<c>storage.webdav</c>），此时最终产物通过 HTTP PUT 上传。
/// </summary>
public interface IArchiveSink
{
    /// <summary>目标描述（本地目录或 WebDAV 地址），用于日志与自检输出。</summary>
    string Describe { get; }

    /// <summary>相对路径对应的最终位置。</summary>
    string Locate(string relativePath);

    /// <summary>探测目标是否已有可用归档（用于摘要比对前的短路判断）。</summary>
    Task<ArchiveProbe> ProbeAsync(string relativePath, CancellationToken cancellationToken);

    /// <summary>把本地 tar 写入目标；失败时抛 <see cref="ArchiveTargetException"/>。</summary>
    Task<ArchiveWriteResult> WriteAsync(string localFile, string relativePath, Action<string>? log, CancellationToken cancellationToken);
}

/// <summary>本地目录归档：把生成的 tar 移动到 {imageRoot}/{仓库}/{tag}.tar。</summary>
public sealed class LocalArchiveSink(string imageRoot) : IArchiveSink
{
    public string ImageRoot { get; } = imageRoot;

    public string Describe => ImageRoot;

    public string Locate(string relativePath) => Path.Combine(ImageRoot, ToNative(relativePath));

    public Task<ArchiveProbe> ProbeAsync(string relativePath, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        try
        {
            var path = Locate(relativePath);
            if (!File.Exists(path)) return Task.FromResult(new ArchiveProbe(false, null));

            var length = new FileInfo(path).Length;
            return Task.FromResult(new ArchiveProbe(length > 0, length));
        }
        catch (Exception)
        {
            return Task.FromResult(new ArchiveProbe(false, null));
        }
    }

    public Task<ArchiveWriteResult> WriteAsync(string localFile, string relativePath, Action<string>? log, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var target = Locate(relativePath);
        try
        {
            PathLayout.EnsureDirectory(target);
            File.Move(localFile, target, overwrite: true);
            var size = new FileInfo(target).Length;
            log?.Invoke($"归档写入 {target}（{Format.Size(size)}）");
            return Task.FromResult(new ArchiveWriteResult(target, size));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArchiveTargetException($"归档写入本地目录失败（{target}）：{ex.Message}", ex);
        }
    }

    private static string ToNative(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);
}
