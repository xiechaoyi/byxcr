using Byxcr.Core;

namespace Byxcr.Registry;

/// <summary>
/// add 之前的「能否拉取」预检结果：只取一次顶层清单（不下载任何图层），
/// 用来确认仓库与标签在某个镜像源上真实可用，并且当前平台策略下有可下载的平台。
/// </summary>
public sealed record ImageProbeResult
{
    /// <summary>预检是否通过。</summary>
    public bool Ok { get; init; }

    /// <summary>实际命中的镜像源显示名（官方源为 docker.io）；失败时为空串。</summary>
    public string Registry { get; init; } = string.Empty;

    /// <summary>顶层清单摘要（sha256:…）；成功时非空。</summary>
    public string? Digest { get; init; }

    /// <summary>本次实际会拉取的平台（已按 download.platforms 过滤，单平台清单为一项或空）。</summary>
    public List<string> Platforms { get; init; } = [];

    /// <summary>失败原因，可直接展示给用户。</summary>
    public string? Error { get; init; }

    /// <summary>
    /// 该源的失败是「确定性结论」（仓库/标签不存在、平台不匹配、返回的不是清单），
    /// 而不是网络超时、限流这类环境原因。汇总失败原因时优先展示这类结论，避免被网络噪音淹没。
    /// </summary>
    public bool Definitive { get; init; }

    public static ImageProbeResult Success(string registry, string? digest, IEnumerable<string> platforms)
        => new()
        {
            Ok = true,
            Registry = registry,
            Digest = digest,
            Platforms = Format.DistinctPlatforms(platforms),
        };

    public static ImageProbeResult Failure(string error, bool definitive = false)
        => new() { Ok = false, Error = error, Definitive = definitive };

    /// <summary>展示用的一句话结论，如「sha256:9f2c… · 3 个平台 · docker.1panel.live」。</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { Format.ShortDigest(Digest) };
            var platforms = Format.PlatformText(Platforms);
            if (platforms.Length > 0) parts.Add(platforms);
            if (!string.IsNullOrWhiteSpace(Registry)) parts.Add(Registry);
            return string.Join(" · ", parts);
        }
    }
}
