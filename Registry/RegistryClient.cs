using System.Diagnostics;
using System.Text.Json;
using Byxcr.Configuration;
using Byxcr.Core;

namespace Byxcr.Registry;

/// <summary>
/// 极简 OCI/Docker Registry V2 客户端：镜像源可用性探测 + 远端摘要查询。
/// 只依赖 HttpClient 与源生成 JSON，NativeAOT 下无反射。
/// </summary>
public sealed class RegistryClient : IDisposable
{
    private static readonly string[] ManifestAccept =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.docker.distribution.manifest.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.v1+json",
    ];

    private readonly HttpClient _http;
    private readonly RegistryConfig _config;
    private readonly Dictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public RegistryClient(RegistryConfig config, TimeSpan? totalTimeout = null)
    {
        _config = config;
        _http = new HttpClient
        {
            Timeout = totalTimeout ?? TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("byxcr/1.0");
    }

    /// <summary>探测镜像源是否可用（/v2/ 返回 200/401/403 等均视为可用）。</summary>
    public async Task<RegistryStatus> ProbeAsync(RegistryCandidate candidate, CancellationToken cancellationToken)
    {
        var scheme = candidate.Insecure ? "http" : "https";
        var url = $"{scheme}://{candidate.ProbeHost}/v2/";
        var stopwatch = Stopwatch.StartNew();

        var timeout = TimeSpan.FromSeconds(Math.Clamp(_config.ProbeTimeoutSeconds, 2, 60));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                stopwatch.Stop();
                var code = (int)response.StatusCode;
                var available = code is 200 or 401 or 403 or 405 or 429;
                return new RegistryStatus
                {
                    Name = candidate.DisplayName,
                    Available = available,
                    StatusCode = code,
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    IsDefault = candidate.IsDefault,
                    Message = available ? null : $"HTTP {code}",
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == 0)
                {
                    try { await Task.Delay(300, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    continue;
                }

                stopwatch.Stop();
                return new RegistryStatus
                {
                    Name = candidate.DisplayName,
                    Available = false,
                    StatusCode = 0,
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    IsDefault = candidate.IsDefault,
                    Message = ex is TaskCanceledException or OperationCanceledException ? "超时" : ex.Message,
                };
            }
        }

        stopwatch.Stop();
        return new RegistryStatus
        {
            Name = candidate.DisplayName,
            Available = false,
            StatusCode = 0,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            IsDefault = candidate.IsDefault,
        };
    }

    /// <summary>查询远端 manifest 摘要；失败时返回 null（调用方回退为直接下载）。</summary>
    public async Task<string?> GetRemoteDigestAsync(RegistryCandidate candidate, ImageReference image, CancellationToken cancellationToken)
    {
        var scheme = candidate.Insecure ? "http" : "https";
        var url = $"{scheme}://{candidate.ProbeHost}/v2/{image.Repository}/manifests/{Uri.EscapeDataString(image.Tag)}";

        var token = _tokens.TryGetValue(candidate.ProbeHost, out var cached) ? cached : null;
        var (status, digest, challenge) = await SendManifestAsync(url, token, cancellationToken).ConfigureAwait(false);
        if (digest is not null) return digest;

        if (status == 401)
        {
            var parsed = RegistryAuth.Parse(challenge);
            if (parsed is not null)
            {
                var fetched = await RegistryAuth
                    .FetchBearerTokenAsync(_http, parsed, image.Repository, image.IsDockerHub, ResolveCredential(candidate), cancellationToken)
                    .ConfigureAwait(false);
                if (fetched is not null)
                {
                    _tokens[candidate.ProbeHost] = fetched;
                    var retry = await SendManifestAsync(url, fetched, cancellationToken).ConfigureAwait(false);
                    return retry.Digest;
                }
            }

            if (token is not null)
            {
                _tokens.Remove(candidate.ProbeHost);
            }
        }

        return null;
    }

    private RegistryCredential? ResolveCredential(RegistryCandidate candidate)
    {
        foreach (var (host, credential) in _config.Credentials ?? [])
        {
            if (string.Equals(host, candidate.ProbeHost, StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, candidate.Name, StringComparison.OrdinalIgnoreCase))
            {
                return credential;
            }
        }

        return null;
    }

    private async Task<(int Status, string? Digest, string? Challenge)> SendManifestAsync(
        string url,
        string? token,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var accept in ManifestAccept) request.Headers.Accept.ParseAdd(accept);
            if (token is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;
            string? digest = null;
            if (response.Headers.TryGetValues("Docker-Content-Digest", out var values))
            {
                foreach (var value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value)) { digest = value.Trim(); break; }
                }
            }

            string? challenge = null;
            if (status == 401 && response.Headers.TryGetValues("WWW-Authenticate", out var auths))
            {
                foreach (var value in auths)
                {
                    if (!string.IsNullOrWhiteSpace(value)) { challenge = value.Trim(); break; }
                }
            }

            return (status, digest, challenge);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (0, null, null);
        }
    }

    public void Dispose() => _http.Dispose();
}
