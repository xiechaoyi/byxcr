using System.Net;
using System.Net.Http.Headers;
using Byxcr.Configuration;

namespace Byxcr.Registry;

/// <summary>
/// 一次 Registry V2 请求的结果。持有响应与内容流，使用后必须释放。
/// </summary>
public sealed class RegistryFetchResult : IDisposable
{
    private readonly HttpResponseMessage? _response;

    private RegistryFetchResult(
        int statusCode,
        HttpResponseMessage? response,
        Stream? content,
        string? digest,
        string? contentType,
        long? contentLength,
        string? challenge,
        string? error)
    {
        StatusCode = statusCode;
        _response = response;
        Content = content;
        Digest = digest;
        ContentType = contentType;
        ContentLength = contentLength;
        Challenge = challenge;
        Error = error;
    }

    public int StatusCode { get; }

    /// <summary>响应体流（仅 200 时非空）。</summary>
    public Stream? Content { get; }

    /// <summary>Docker-Content-Digest 响应头。</summary>
    public string? Digest { get; }

    public string? ContentType { get; }

    public long? ContentLength { get; }

    /// <summary>401 时的 WWW-Authenticate 原文。</summary>
    public string? Challenge { get; }

    public string? Error { get; }

    public bool Success => StatusCode == 200 && Content is not null;

    public static RegistryFetchResult Ok(HttpResponseMessage response, Stream content)
    {
        var length = response.Content.Headers.ContentLength;
        return new RegistryFetchResult(
            200,
            response,
            content,
            Header(response, "Docker-Content-Digest"),
            response.Content.Headers.ContentType?.MediaType,
            length,
            null,
            null);
    }

    public static RegistryFetchResult Status(int statusCode, string? challenge, string? error = null)
        => new(statusCode, null, null, null, null, null, challenge, error);

    public static RegistryFetchResult Failure(string error)
        => new(0, null, null, null, null, null, null, error);

    public void Dispose()
    {
        try { Content?.Dispose(); } catch (Exception) { /* 忽略 */ }
        try { _response?.Dispose(); } catch (Exception) { /* 忽略 */ }
    }

    private static string? Header(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values)) return null;
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }
}

/// <summary>
/// 原生 Registry V2 传输层：统一的 URL 组装、匿名/具名 Bearer 鉴权与重定向处理。
/// 不启用自动解压，保证 blob 字节与摘要一一对应。
/// </summary>
public sealed class RegistryTransport : IDisposable
{
    private readonly HttpClient _http;
    private readonly IReadOnlyDictionary<string, RegistryCredential> _credentials;
    private readonly Dictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public RegistryTransport(IReadOnlyDictionary<string, RegistryCredential>? credentials)
    {
        _credentials = credentials ?? new Dictionary<string, RegistryCredential>(StringComparer.OrdinalIgnoreCase);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            MaxAutomaticRedirections = 10,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };

        _http = new HttpClient(handler, disposeHandler: true)
        {
            // 超时由每次请求自行控制（仅约束响应头阶段，避免中断大图层下载）
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("byxcr/1.0");
    }

    /// <summary>发起一次带鉴权的 GET 请求；401 时自动走 token 换取流程并重试一次。</summary>
    public async Task<RegistryFetchResult> FetchAsync(
        RegistryCandidate candidate,
        string repository,
        string relativePath,
        IReadOnlyList<string>? accept,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var scheme = candidate.Insecure ? "http" : "https";
        var url = $"{scheme}://{candidate.ProbeHost}/{relativePath.TrimStart('/')}";
        var cacheKey = candidate.ProbeHost + "|" + repository;
        var credential = ResolveCredential(candidate);

        try
        {
            var hasToken = _tokens.TryGetValue(cacheKey, out var token);
            if (credential is { Token.Length: > 0 })
            {
                token = credential.Token;
                hasToken = true;
            }

            var first = await SendAsync(url, hasToken ? token : null, credential, hasToken, accept, timeout, cancellationToken).ConfigureAwait(false);
            if (first.StatusCode != 401 || string.IsNullOrWhiteSpace(first.Challenge))
            {
                if (first.StatusCode == 401 && hasToken) _tokens.Remove(cacheKey);
                return first;
            }

            var challenge = RegistryAuth.Parse(first.Challenge);
            first.Dispose();
            if (challenge is null) return RegistryFetchResult.Status(401, null, "鉴权失败");

            var fetched = await RegistryAuth
                .FetchBearerTokenAsync(_http, challenge, repository, IsDockerHub(candidate), credential, cancellationToken)
                .ConfigureAwait(false);

            if (fetched is null) return RegistryFetchResult.Status(401, null, "无法获取访问令牌");

            _tokens[cacheKey] = fetched;
            return await SendAsync(url, fetched, credential, hasToken: true, accept, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return RegistryFetchResult.Failure(ex is TaskCanceledException or OperationCanceledException
                ? $"请求超时（{timeout.TotalSeconds:0} 秒）"
                : ex.Message);
        }
    }

    private async Task<RegistryFetchResult> SendAsync(
        string url,
        string? token,
        RegistryCredential? credential,
        bool hasToken,
        IReadOnlyList<string>? accept,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // 超时只覆盖「拿到响应头」这一段；随后取消计时器，仅保留调用方的取消信号
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (accept is not null)
        {
            foreach (var value in accept) request.Headers.Accept.ParseAdd(value);
        }

        if (hasToken && token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else if (credential is { Username.Length: > 0 })
        {
            var raw = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(credential.Username + ":" + credential.Password));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RegistryFetchResult.Failure($"请求超时（{timeout.TotalSeconds:0} 秒）：{url}");
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            var challenge = response.Headers.TryGetValues("WWW-Authenticate", out var values)
                ? values.FirstOrDefault()
                : null;
            var status = (int)response.StatusCode;
            response.Dispose();
            return RegistryFetchResult.Status(status, challenge, status == 401 ? null : $"HTTP {status}");
        }

        var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return RegistryFetchResult.Ok(response, content);
    }

    private RegistryCredential? ResolveCredential(RegistryCandidate candidate)
    {
        foreach (var (host, credential) in _credentials)
        {
            if (string.Equals(host, candidate.ProbeHost, StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, candidate.Name, StringComparison.OrdinalIgnoreCase))
            {
                return credential;
            }
        }

        return null;
    }

    private static bool IsDockerHub(RegistryCandidate candidate)
        => candidate.IsDockerHubRegistry
           || candidate.ProbeHost.Equals("registry-1.docker.io", StringComparison.OrdinalIgnoreCase)
           || candidate.ProbeHost.Equals("docker.io", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _http.Dispose();
}
