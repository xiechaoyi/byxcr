using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Byxcr.Configuration;
using Byxcr.Core;
using Byxcr.Logging;

namespace Byxcr.Services;

/// <summary>
/// WebDAV 归档目标：把归档产物（gzip 压缩、文件名统一为 <c>.tar</c>）通过 HTTP PUT 上传到远端集合，
/// 归档相对路径（仓库名/tag.tar，非 Docker Hub 的镜像还带主机名）原样拼接在 <c>storage.webdav.url</c> 之后。
/// <para>上传前可用 MKCOL 逐级创建集合；HEAD 用于判断目标是否已存在。</para>
/// </summary>
public sealed class WebDavArchiveSink : IArchiveSink, IDisposable
{
    private static readonly HttpMethod MkCol = new("MKCOL");

    private readonly WebdavConfig _config;
    private readonly Uri _baseUri;
    private readonly HttpClient _http;
    private readonly LocalArchiveSink? _localCopy;
    private readonly HashSet<string> _ensured = new(StringComparer.OrdinalIgnoreCase);

    public WebDavArchiveSink(WebdavConfig config, LocalArchiveSink localSink)
    {
        _config = config;
        _baseUri = new Uri(config.Url.TrimEnd('/') + "/", UriKind.Absolute);
        _localCopy = config.KeepLocalCopy ? localSink : null;

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };

        if (config.AllowInvalidCertificate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        _http = new HttpClient(handler, disposeHandler: true)
        {
            // 超时由每次请求自行控制（大文件上传可能持续很久）
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("byxcr/1.0");
    }

    public string Describe => _baseUri.ToString();

    public string Locate(string relativePath) => BuildUri(relativePath).ToString();

    public async Task<ArchiveProbe> ProbeAsync(string relativePath, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CreateTimeout(cancellationToken);
            using var request = CreateRequest(HttpMethod.Head, BuildUri(relativePath));
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return new ArchiveProbe(false, null);

            var length = response.Content.Headers.ContentLength ?? 0;
            return new ArchiveProbe(true, length > 0 ? length : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug($"WebDAV 存在性检查失败：{ex.Message}");
            return new ArchiveProbe(false, null);
        }
    }

    public async Task<ArchiveWriteResult> WriteAsync(string localFile, string relativePath, Action<string>? log, CancellationToken cancellationToken)
    {
        var uri = BuildUri(relativePath);
        var length = new FileInfo(localFile).Length;

        if (_config.CreateCollections) await EnsureCollectionsAsync(relativePath, log, cancellationToken).ConfigureAwait(false);

        var attempts = Math.Max(1, _config.Retries + 1);
        string? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1)
            {
                Log.Warn($"WebDAV 上传失败（{lastError}），第 {attempt} 次重试 …");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 3 * attempt)), cancellationToken).ConfigureAwait(false);
            }

            var (ok, status, error) = await TryUploadAsync(uri, localFile, length, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                log?.Invoke($"已上传 {Format.Size(length)} → {uri}");
                await CopyToLocalAsync(localFile, relativePath, log, cancellationToken).ConfigureAwait(false);
                return new ArchiveWriteResult(uri.ToString(), length);
            }

            lastError = error;

            // 集合缺失时补建后重试
            if (status is 404 or 409 && _config.CreateCollections)
            {
                await EnsureCollectionsAsync(relativePath, log, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new ArchiveTargetException($"WebDAV 上传失败（{uri}）：{lastError}");
    }

    /// <summary>单次 PUT；返回 (是否成功, 状态码, 错误信息)。</summary>
    private async Task<(bool Ok, int Status, string? Error)> TryUploadAsync(
        Uri uri,
        string localFile,
        long length,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CreateTimeout(cancellationToken);

            // 独立的 using 作用域：请求结束后必须立刻释放文件句柄，否则后续本地副本搬运会失败
            await using (var file = new FileStream(
                             localFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                             1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var request = CreateRequest(HttpMethod.Put, uri))
            {
                request.Content = new StreamContent(file, 1 << 20);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
                request.Content.Headers.ContentLength = length;

                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                var code = (int)response.StatusCode;
                return response.IsSuccessStatusCode
                    ? (true, code, null)
                    : (false, code, $"HTTP {code} {response.ReasonPhrase}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    /// <summary>远端上传成功后再落本地副本；副本失败只告警，不影响主目标结果。</summary>
    private async Task CopyToLocalAsync(string localFile, string relativePath, Action<string>? log, CancellationToken cancellationToken)
    {
        if (_localCopy is null) return;
        try
        {
            await _localCopy.WriteAsync(localFile, relativePath, log, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"本地副本写入失败（远端已上传成功）：{ex.Message}");
        }
    }

    /// <summary>逐级 MKCOL：先补基础集合，再补归档相对路径上的各级目录（已建过的直接跳过）。</summary>
    private async Task EnsureCollectionsAsync(string relativePath, Action<string>? log, CancellationToken cancellationToken)
    {
        await CreateCollectionAsync(_baseUri, log, cancellationToken).ConfigureAwait(false);

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 1) return;

        var current = _baseUri;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = new Uri(current, Uri.EscapeDataString(segments[i]) + "/");
            await CreateCollectionAsync(current, log, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CreateCollectionAsync(Uri uri, Action<string>? log, CancellationToken cancellationToken)
    {
        var key = uri.ToString();
        if (!_ensured.Add(key)) return;

        try
        {
            using var cts = CreateTimeout(cancellationToken);
            using var request = CreateRequest(MkCol, uri);
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            var code = (int)response.StatusCode;
            if (code is 200 or 201)
            {
                log?.Invoke($"已创建 WebDAV 集合 {uri}");
            }
            else if (code is 301 or 302 or 405)
            {
                // 集合已存在
            }
            else
            {
                Log.Debug($"WebDAV MKCOL {uri} 返回 HTTP {code}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _ensured.Remove(key);
            Log.Debug($"WebDAV MKCOL {uri} 失败：{ex.Message}");
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        if (_config.Username.Length > 0)
        {
            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(_config.Username + ":" + _config.Password));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw);
        }
        return request;
    }

    private Uri BuildUri(string relativePath)
        => new(_baseUri, string.Join('/', relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString)));

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));
        return cts;
    }

    public void Dispose() => _http.Dispose();
}
