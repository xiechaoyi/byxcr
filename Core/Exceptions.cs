namespace Byxcr.Core;

/// <summary>byxcr 业务异常基类。</summary>
public class ByxcrException : Exception
{
    public ByxcrException(string message) : base(message) { }

    public ByxcrException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>配置无效。</summary>
public sealed class ConfigurationException : ByxcrException
{
    public ConfigurationException(string message) : base(message) { }
}

/// <summary>
/// 归档目标（本地目录或 WebDAV）写入失败。
/// 与镜像源无关，换源重试只会白白重新下载，调用方应立即终止本轮同步。
/// </summary>
public sealed class ArchiveTargetException : ByxcrException
{
    public ArchiveTargetException(string message) : base(message) { }

    public ArchiveTargetException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// 与镜像源无关的确定性失败（例如清单中找不到任何平台、blob 摘要校验不一致）。
/// 换镜像源也不会成功，调用方应立即终止重试。
/// </summary>
public sealed class UnrecoverableImageException : ByxcrException
{
    public UnrecoverableImageException(string message) : base(message) { }
}
