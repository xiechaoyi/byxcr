namespace Byxcr.Registry;

/// <summary>一个可尝试的下载来源（官方源或镜像源）及其对应的拉取引用。</summary>
public sealed class RegistryCandidate
{
    /// <summary>镜像源主机名；官方源为 docker.io。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>探测 /v2/ 时使用的主机名。</summary>
    public string ProbeHost { get; init; } = string.Empty;

    public bool Insecure { get; init; }

    /// <summary>是否为官方源（docker.io）。</summary>
    public bool IsDefault { get; init; }

    /// <summary>是否确为 Docker 官方源（docker.io）。非 Hub 仓库虽标记 IsDefault，但不属于官方源。</summary>
    public bool IsDockerHubRegistry => Name.Equals(Core.ImageReference.DockerHub, StringComparison.OrdinalIgnoreCase);

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Core.ImageReference.DockerHub : Name;
}
