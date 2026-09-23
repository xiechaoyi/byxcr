using Byxcr.Configuration;
using Byxcr.Core;
using Byxcr.Logging;

namespace Byxcr.Registry;

/// <summary>
/// 按配置顺序构建镜像源候选列表，并在启用探测时挑出当前可用的源。
/// 非 Docker Hub 的镜像（如 ghcr.io）直接访问原始仓库，不套用镜像源。
/// </summary>
public sealed class RegistryResolver
{
    private readonly RegistryConfig _config;
    private readonly RegistryClient _client;

    public RegistryResolver(RegistryConfig config, RegistryClient client)
    {
        _config = config;
        _client = client;
    }

    public List<RegistryCandidate> Build(ImageReference image, IReadOnlyList<string>? overrideMirrors)
    {
        var candidates = new List<RegistryCandidate>();

        if (!image.IsDockerHub)
        {
            candidates.Add(new RegistryCandidate
            {
                Name = image.Registry,
                ProbeHost = image.Registry,
                IsDefault = true,
            });
            return candidates;
        }

        if (_config.UseDefaultRegistryFirst)
        {
            candidates.Add(new RegistryCandidate
            {
                Name = ImageReference.DockerHub,
                ProbeHost = "registry-1.docker.io",
                IsDefault = true,
            });
        }

        var mirrors = overrideMirrors is { Count: > 0 }
            ? overrideMirrors.Select(name => new RegistryItem { Name = name }).ToList()
            : _config.Items.Where(item => item.Enabled && !string.IsNullOrWhiteSpace(item.Name)).ToList();

        foreach (var mirror in mirrors)
        {
            var host = mirror.Name.Trim().TrimEnd('/');
            if (host.Length == 0) continue;
            var probeHost = string.IsNullOrWhiteSpace(mirror.ProbeHost) ? host : mirror.ProbeHost!.Trim();

            candidates.Add(new RegistryCandidate
            {
                Name = host,
                ProbeHost = probeHost,
                Insecure = mirror.Insecure,
                IsDefault = false,
            });
        }

        return candidates;
    }

    /// <summary>
    /// 返回按序尝试的候选源；VerifyBeforeUse 为真时先并发探测，
    /// 可用源保持原有顺序，若全部不可用则退回完整列表继续尝试。
    /// </summary>
    public async Task<(List<RegistryCandidate> Candidates, List<RegistryStatus> Probes)> ResolveAsync(
        ImageReference image,
        IReadOnlyList<string>? overrideMirrors,
        CancellationToken cancellationToken,
        bool forceProbe = false)
    {
        var candidates = Build(image, overrideMirrors);
        if (candidates.Count == 0) return (candidates, []);

        if (!_config.VerifyBeforeUse && !forceProbe) return (candidates, []);

        var tasks = candidates.Select(candidate => _client.ProbeAsync(candidate, cancellationToken)).ToArray();
        var probes = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();

        var available = new List<RegistryCandidate>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (probes[i].Available) available.Add(candidates[i]);
        }

        if (available.Count == 0)
        {
            Log.Warn($"镜像源探测均失败，将按列表顺序逐个尝试：{string.Join(", ", candidates.Select(c => c.DisplayName))}");
            return (candidates, probes);
        }

        Log.Debug($"可用镜像源（按优先级）：{string.Join(", ", available.Select(c => c.DisplayName))}");
        return (available, probes);
    }
}
