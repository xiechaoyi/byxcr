using System.Globalization;
using System.Text.Json;

namespace Byxcr.Core;

/// <summary>
/// 容器镜像引用解析。负责把 <c>mysql:5.6</c>、<c>alpine/socat</c>、
/// <c>ghcr.io/foo/bar:1.0</c> 等写法规范化为仓库 / 标签，并给出归档落盘路径。
/// </summary>
public sealed class ImageReference
{
    public const string DockerHub = "docker.io";

    private ImageReference() { }

    /// <summary>仓库主机名，Docker Hub 镜像固定为 docker.io。</summary>
    public string Registry { get; private init; } = DockerHub;

    /// <summary>仓库名，例如 library/mysql、alpine/socat。</summary>
    public string Repository { get; private init; } = "";

    /// <summary>标签，省略时为 latest。</summary>
    public string Tag { get; private init; } = "latest";

    /// <summary>摘要（形如 sha256:...），仅在按摘要引用时存在。</summary>
    public string Digest { get; private init; } = "";

    public bool IsDockerHub => string.Equals(Registry, DockerHub, StringComparison.OrdinalIgnoreCase);

    /// <summary>规范化镜像名，例如 mysql:5.6 或 ghcr.io/foo/bar:1.0。</summary>
    public string CanonicalName => Digest.Length > 0
        ? $"{RegistryPath}{Repository}@{Digest}"
        : $"{RegistryPath}{Repository}:{Tag}";

    /// <summary>归档目录（相对路径）：Docker Hub 直接用仓库名，其它仓库在前面带上主机名。</summary>
    public string RelativeDirectory => SanitizePath($"{RegistryPath}{Repository}");

    /// <summary>tag 作为文件名。</summary>
    public string FileName => Tag + ".tar";

    /// <summary>相对归档路径，如 library/mysql/5.6.tar、quay.io/prometheus/busybox/latest.tar。</summary>
    public string RelativeFilePath => $"{RelativeDirectory}/{FileName}";

    /// <summary>
    /// OCI 布局 index.json 中 org.opencontainers.image.ref.name 注解的取值，即 tag 本身。
    /// 与 skopeo 写 oci: 目标时的行为一致，便于 `oci-archive:xxx.tar:tag` 直接引用。
    /// </summary>
    public string OciRefName => Tag;

    /// <summary>
    /// docker-archive 兼容清单（manifest.json）里 RepoTags 的取值，形如 <c>library/mysql:5.6</c>、
    /// <c>quay.io/prometheus/busybox:latest</c>。Docker Hub 省略主机名，与 `docker save` 的写法一致
    /// （docker load 会用 reference.ParseNormalizedNamed 再规范化一次）。
    /// </summary>
    public string DockerRepoTag => $"{RegistryPath}{Repository}:{Tag}";

    private string RegistryPath => IsDockerHub ? "" : Registry + "/";

    public override string ToString() => CanonicalName;

    public static bool TryParse(string? value, out ImageReference reference)
    {
        try
        {
            reference = Parse(value!);
            return true;
        }
        catch (Exception)
        {
            reference = null!;
            return false;
        }
    }

    public static ImageReference Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("镜像名称不能为空。", nameof(value));

        var text = value.Trim();
        foreach (var prefix in (string[])["docker://", "https://", "http://"])
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = text[prefix.Length..];
        }
        text = text.Trim().TrimEnd('/');

        // 摘要引用：repo@sha256:xxx
        var digest = string.Empty;
        var at = text.IndexOf('@');
        if (at >= 0)
        {
            digest = text[(at + 1)..];
            text = text[..at];
        }

        // 是否存在显式仓库主机
        var registry = string.Empty;
        var firstSlash = text.IndexOf('/');
        if (firstSlash > 0)
        {
            var head = text[..firstSlash];
            if (head.Contains('.') || head.Contains(':') || string.Equals(head, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                registry = NormalizeRegistry(head);
                text = text[(firstSlash + 1)..];
            }
        }

        // 标签
        var tag = "latest";
        var lastSlash = text.LastIndexOf('/');
        var lastColon = text.LastIndexOf(':');
        if (lastColon > lastSlash)
        {
            tag = text[(lastColon + 1)..];
            text = text[..lastColon];
        }

        var repository = text.Trim().Trim('/').ToLowerInvariant();
        if (repository.Length == 0)
            throw new ArgumentException($"无法解析镜像名称：{value}", nameof(value));

        if (registry.Length == 0)
        {
            registry = DockerHub;
            if (!repository.Contains('/')) repository = "library/" + repository;
        }

        if (tag.Length == 0) tag = "latest";
        if (digest.Length > 0) tag = SanitizeTag(digest);

        return new ImageReference
        {
            Registry = registry,
            Repository = repository,
            Tag = tag,
            Digest = digest,
        };
    }

    private static string NormalizeRegistry(string host) => host.ToLowerInvariant() switch
    {
        "index.docker.io" or "registry-1.docker.io" or "registry.hub.docker.com" or "docker.io" => DockerHub,
        var other => other,
    };

    private static string SanitizeTag(string raw)
    {
        var length = Math.Min(raw.Length, 128);
        var buffer = new char[length];
        for (var i = 0; i < length; i++)
        {
            var c = raw[i];
            buffer[i] = char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-';
        }
        var tag = new string(buffer).Trim('-', '.');
        return tag.Length == 0 ? "latest" : tag;
    }

    /// <summary>逐段清理路径，避免 : * ? 等非法字符导致落盘失败（不破坏目录分隔符）。</summary>
    private static string SanitizePath(string path)
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
        return string.Join('/', cleaned);
    }
}
