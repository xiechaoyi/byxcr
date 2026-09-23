using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Byxcr.Configuration;
using Byxcr.Core;
using Byxcr.Logging;

namespace Byxcr.Services;

/// <summary>
/// 内置 WebAPI 服务：为 add / sync / remove / records / enable / disable（外加 list / health）提供 HTTP 接口。
/// <para>路由与鉴权：<c>/</c> 返回内置的 Web 控制台页面、<c>/api</c> 返回可用接口清单、<c>/api/list</c> 返回同步任务列表，
/// 这三者（连同 <c>/api/health</c>）是<strong>公开接口，不需要 Authorization 令牌</strong>——控制台页面正是靠这一点
/// 在浏览器里直接读取任务列表的；其余写接口仍要求 <c>api.token</c> 非空且请求携带一致的令牌。</para>
/// <para>所有响应在写出前都会把记录的归档位置折叠成相对归档根的路径（见 <see cref="ArchivePath.Relative"/>），
/// 不把 <c>storage.webdav.url</c> 指向的 WebDAV 地址或本机绝对路径暴露给调用方。</para>
/// <para>刻意不依赖 ASP.NET / HttpListener，而是基于 <see cref="TcpListener"/> 直接解析 HTTP/1.1：</para>
/// <list type="bullet">
/// <item>NativeAOT 下无反射、无托管宿主，也不需要 <c>Microsoft.Extensions.Hosting</c>；</item>
/// <item>绕开 Windows 上 HttpListener 绑定非 localhost 前缀需要 <c>netsh</c> URL ACL 预留的限制。</item>
/// </list>
/// </summary>
public sealed class ApiServer
{
    private const string Prefix = "/api/";

    private const string JsonContentType = "application/json; charset=utf-8";

    private const string HtmlContentType = "text/html; charset=utf-8";

    /// <summary>/api/list 单页最大条数，避免分页参数被用来一次拉爆数据。</summary>
    private const int MaxPageSize = 1000;

    /// <summary>请求头（含首行）上限；超出直接拒绝，避免被超长头拖垮。</summary>
    private const int MaxHeaderBytes = 64 * 1024;

    /// <summary>请求体上限。</summary>
    private const int MaxBodyBytes = 1024 * 1024;

    /// <summary>单个连接的读写超时（秒）。</summary>
    private const int ConnectionTimeoutSeconds = 30;

    /// <summary>add 触发的后台下载使用的 trigger：受数据库同步租约约束，不会与定时任务重复下载同一镜像。</summary>
    private const string BackgroundTrigger = "add";

    private readonly ByxcrApplication _app;
    private readonly ApiConfig _config;
    private readonly IPAddress _address;

    public ApiServer(ByxcrApplication app)
    {
        _app = app;
        _config = app.Config.Api;
        _address = ResolveAddress(_config.Host, out var resolved);
        Address = resolved;
    }

    /// <summary>实际监听地址的文本（打印用）。</summary>
    public string Address { get; }

    public int Port => _config.Port;

    /// <summary>对外可访问的基地址，例如 http://0.0.0.0:5088/api。</summary>
    public string BaseUrl => $"http://{Address}:{_config.Port.ToString(CultureInfo.InvariantCulture)}{Prefix}";

    /// <summary>是否要求调用方携带 Authorization 令牌。</summary>
    public bool RequiresToken => _config.Token.Length > 0;

    /// <summary>启动监听并持续处理请求，直到 <paramref name="cancellationToken"/> 取消。</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(_address, _config.Port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new ByxcrException($"WebAPI 无法监听 {BaseUrl}：{ex.Message}（端口可能被占用，请用 api.port 或 BYXCR_API_PORT 换个端口）");
        }

        var portText = _config.Port.ToString(CultureInfo.InvariantCulture);
        Log.Ok($"WebAPI 已启动 → {BaseUrl} · Web 控制台 http://{Address}:{portText}/（/、/api、/api/list、/api/health 无需令牌）");
        Log.Info(RequiresToken
            ? "写接口鉴权：请求需携带 Authorization 令牌（与 api.token 一致）"
            : "未配置 api.token：add / sync / remove / enable / disable 将返回「请先配置Token」");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    Log.Debug($"WebAPI 接受连接失败：{ex.Message}");
                    continue;
                }

                // 每个连接一个任务：请求之间互不影响，异常也不会打断监听循环
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
            }
        }
        finally
        {
            try { listener.Stop(); } catch (Exception) { /* 忽略停止异常 */ }
            Log.Info("WebAPI 已停止");
        }
    }

    // ------------------------------------------------------------ 连接处理

    private async Task HandleClientAsync(TcpClient client, CancellationToken parentToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ConnectionTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, parentToken);

        try
        {
            using (client)
            {
                client.ReceiveTimeout = ConnectionTimeoutSeconds * 1000;
                client.SendTimeout = ConnectionTimeoutSeconds * 1000;

                await using var stream = client.GetStream();
                await using var _ = stream;

                var request = await ReadRequestAsync(stream, linked.Token).ConfigureAwait(false);
                if (request is null) return;

                var reply = await DispatchAsync(request).ConfigureAwait(false);
                var payload = Encoding.UTF8.GetBytes(reply.Body);
                await WriteResponseAsync(stream, reply.Status, reply.Reason, reply.ContentType, payload, linked.Token).ConfigureAwait(false);

                Log.Info($"API {request.Method} {request.Path} → {reply.Status}");
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"WebAPI 连接处理失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed class Request
    {
        public string Method { get; init; } = "GET";
        public string Path { get; init; } = "/";
        public Dictionary<string, string> Query { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string Body { get; init; } = "";
    }

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes];
        var filled = 0;
        var headEnd = -1;

        while (headEnd < 0)
        {
            if (filled >= buffer.Length) return null;

            var read = await stream.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), ct).ConfigureAwait(false);
            if (read <= 0) return null;

            var searchStart = Math.Max(0, filled - 3);
            filled += read;
            headEnd = IndexOf(buffer, searchStart, filled - searchStart, "\r\n\r\n"u8);
        }

        var headLength = headEnd + 4;
        var head = Encoding.UTF8.GetString(buffer, 0, headLength);
        var lines = head.Split("\r\n", StringSplitOptions.None);

        var requestLine = lines.Length > 0 ? lines[0].Split(' ') : [];
        if (requestLine.Length < 2) return null;

        var method = requestLine[0].ToUpperInvariant();
        var target = requestLine[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var queryIndex = target.IndexOf('?');
        var path = queryIndex >= 0 ? target[..queryIndex] : target;
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (queryIndex >= 0 && queryIndex < target.Length - 1)
        {
            foreach (var pair in target[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equal = pair.IndexOf('=');
                if (equal <= 0) continue;
                query[Uri.UnescapeDataString(pair[..equal])] = Uri.UnescapeDataString(pair[(equal + 1)..]);
            }
        }

        var body = "";
        if (headers.TryGetValue("Content-Length", out var lengthText)
            && int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
            && length > 0)
        {
            if (length > MaxBodyBytes) return null;

            var already = filled - headLength;
            var bodyBytes = new byte[length];
            if (already > 0) Array.Copy(buffer, headLength, bodyBytes, 0, Math.Min(already, length));
            var offset = Math.Min(already, length);
            while (offset < length)
            {
                var read = await stream.ReadAsync(bodyBytes.AsMemory(offset, length - offset), ct).ConfigureAwait(false);
                if (read <= 0) break;
                offset += read;
            }

            body = Encoding.UTF8.GetString(bodyBytes, 0, offset);
        }

        return new Request
        {
            Method = method,
            Path = path,
            Query = query,
            Headers = headers,
            Body = body,
        };
    }

    private static int IndexOf(byte[] buffer, int offset, int count, ReadOnlySpan<byte> pattern)
    {
        var limit = offset + count - pattern.Length;
        for (var i = offset; i <= limit; i++)
        {
            var matched = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (buffer[i + j] != pattern[j]) { matched = false; break; }
            }

            if (matched) return i;
        }

        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string reason, string contentType, byte[] body, CancellationToken ct)
    {
        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Connection: close\r\n\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    // --------------------------------------------------------------- 路由

    /// <summary>
    /// 分发一次请求。路由与鉴权在这里统一处理，业务处理仍是原来的三元组（状态码 / 原因 / 正文），
    /// 只有内置的 Web 控制台页面用 <c>text/html</c>，其余一律 JSON。
    /// <para>整条链路是异步的：add 要先向镜像源预检、sync 要真的下载，都不是能同步等待的量级，
    /// 早先在这里用 <c>GetAwaiter().GetResult()</c> 会白占一个线程池线程。</para>
    /// </summary>
    private async Task<(int Status, string Reason, string Body, string ContentType)> DispatchAsync(Request request)
    {
        var path = request.Path;
        if (path.Length > 1) path = path.TrimEnd('/');

        // 根路由：内置 Web 控制台。页面自身调用公开接口 /api/list，因此不需要任何令牌。
        if (path is "/" or "/index.html" or "/console" or "/console.html")
        {
            return (200, "OK", WebConsole.Html, HtmlContentType);
        }

        // 页面图标用 data: URI 内嵌，这里只把浏览器默认会请求的 /favicon.ico 收掉，免得日志里刷 404
        if (path is "/favicon.ico")
        {
            return (204, "No Content", "", JsonContentType);
        }

        if (!path.StartsWith(Prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return AsJson(NotFound(path));
        }

        // /api 与 /api/ 都算索引（path 已去掉尾斜杠，这里再挡一次越界切片）
        var route = path.Length > Prefix.Length ? path[Prefix.Length..].ToLowerInvariant() : "";

        // 鉴权：公开接口（索引、list、health）不需要令牌；其余接口必须携带一致且非空的 Authorization 令牌。
        // 未配置 api.token 时一律拒绝，提示先配置令牌，而不是放行。
        if (!IsPublic(route))
        {
            if (_config.Token.Length == 0)
            {
                return AsJson(Json(401, "Unauthorized", new ApiResponse { Ok = false, Message = "请先配置Token" }));
            }

            if (!Authorized(request))
            {
                return AsJson(Json(401, "Unauthorized", new ApiResponse { Ok = false, Message = "缺少或错误的 Authorization 令牌" }));
            }
        }

        try
        {
            // 所有接口同时接受 GET 与 POST（以及 HEAD/PUT 等）：参数既能放查询串也能放 JSON 请求体，
            // 方法只影响「有没有请求体」，不再作为路由条件，避免 GET 调写接口被 404。
            var reply = route switch
            {
                "health" => Ok("byxcr 已就绪"),
                "list" => List(request),
                "records" => Records(request),
                "add" => await AddAsync(request).ConfigureAwait(false),
                "sync" => await SyncAsync(request).ConfigureAwait(false),
                "remove" => Remove(request),
                "enable" => Toggle(request, true),
                "disable" => Toggle(request, false),
                "" or "index" or "help" => Index(),
                _ => NotFound(path),
            };

            return AsJson(reply);
        }
        catch (ConfigurationException ex)
        {
            return AsJson(Json(400, "Bad Request", new ApiResponse { Ok = false, Message = ex.Message }));
        }
        catch (ByxcrException ex)
        {
            return AsJson(Json(400, "Bad Request", new ApiResponse { Ok = false, Message = ex.Message }));
        }
        catch (Exception ex)
        {
            return AsJson(Json(500, "Internal Server Error", new ApiResponse { Ok = false, Message = $"{ex.GetType().Name}: {ex.Message}" }));
        }
    }

    /// <summary>公开接口（无需 Authorization 令牌）：/api 索引、/api/list、/api/health。</summary>
    private static bool IsPublic(string route) => route is "" or "index" or "help" or "list" or "health";

    /// <summary>业务处理返回的三元组补上 JSON 的 Content-Type。</summary>
    private static (int Status, string Reason, string Body, string ContentType) AsJson((int Status, string Reason, string Body) reply)
        => (reply.Status, reply.Reason, reply.Body, JsonContentType);

    /// <summary>
    /// /api：返回可用接口清单。故意不鉴权——没配 <c>api.token</c> 时也能先看清有哪些接口、哪些需要令牌。
    /// </summary>
    private (int Status, string Reason, string Body) Index()
    {
        var endpoints = new List<ApiEndpoint>
        {
            new() { Method = "GET", Path = "/", Description = "Web 控制台：同步任务列表（筛选 + 分页，页面内部调用 /api/list）", Public = true },
            new() { Method = "GET", Path = "/api", Description = "本接口清单", Public = true },
            new() { Method = "GET/POST", Path = "/api/health", Description = "健康检查", Public = true },
            new()
            {
                Method = "GET/POST", Path = "/api/list", Public = true,
                Description = "镜像同步任务列表（等同 list --json）；filter 按镜像名筛选、enabled 按启用状态筛选、page + limit 分页（省略 limit 返回全部）"
                              + "；每条任务的 lastFile 只给相对归档根的路径，不含 WebDAV 地址与本机绝对路径",
                Parameters = "filter（别名 keyword / q）、enabled、page、limit",
            },
            new()
            {
                Method = "GET/POST", Path = "/api/records",
                Description = "同步记录；clear=true 会先删掉 3 天前的记录；filePath 同样只给相对归档根的路径",
                Parameters = "image、limit、clear",
            },
            new() { Method = "GET/POST", Path = "/api/add", Description = "添加镜像并后台下载（写库前先校验仓库与标签能否拉取）；任务已存在时不重复入库，显式给出的 interval / output 与库中不一致则更新对应配置", Parameters = "image、interval、disable、output、nocheck" },
            new() { Method = "GET/POST", Path = "/api/sync", Description = "立即同步；省略镜像时同步全部启用项", Parameters = "image / images、force、output" },
            new() { Method = "GET/POST", Path = "/api/remove", Description = "从同步列表移除", Parameters = "image" },
            new() { Method = "GET/POST", Path = "/api/enable", Description = "启用任务", Parameters = "image" },
            new() { Method = "GET/POST", Path = "/api/disable", Description = "停用任务", Parameters = "image" },
        };

        return Json(200, "OK", new ApiResponse
        {
            Ok = true,
            Message = RequiresToken
                ? $"byxcr WebAPI 共 {endpoints.Count} 个接口（public=true 的无需令牌，其余需 Authorization 令牌）"
                : $"byxcr WebAPI 共 {endpoints.Count} 个接口（当前未配置 api.token：除 public=true 外一律返回「请先配置Token」）",
            Endpoints = endpoints,
        });
    }

    private bool Authorized(Request request)
    {
        if (!RequiresToken) return true;

        if (!request.Headers.TryGetValue("Authorization", out var header)) return false;

        var value = header.Trim();
        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) value = value["Bearer ".Length..].Trim();

        return FixedTimeEquals(value, _config.Token);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    // ------------------------------------------------------------- 处理器

    private (int Status, string Reason, string Body) Ok(string message)
        => Json(200, "OK", new ApiResponse { Ok = true, Message = message });

    private (int Status, string Reason, string Body) NotFound(string path)
        => Json(404, "Not Found", new ApiResponse { Ok = false, Message = $"未知接口：{path}（可用接口清单见 GET /api；Web 控制台见 GET /）" });

    /// <summary>
    /// 输出 JSON。所有接口的响应都从这里出去，所以归档位置的对外折叠统一收在这一处：
    /// 任务与同步记录里的 <c>lastFile</c> / <c>filePath</c> 写出前会被换成相对归档根的路径
    /// （<c>library/alpine/3.20.tar</c>），不把 <c>storage.webdav.url</c> 指向的 WebDAV 地址
    /// 或本机绝对路径暴露给调用方；数据库与命令行仍保留原始位置。
    /// </summary>
    private (int Status, string Reason, string Body) Json(int status, string reason, ApiResponse response)
    {
        RedactLocations(response);
        return (status, reason, ByxcrJson.ToJson(response, ByxcrJson.Default.ApiResponse));
    }

    /// <summary>把响应里所有归档位置折叠成相对路径（就地修改，对象都是本次请求刚从数据库取出的）。</summary>
    private void RedactLocations(ApiResponse response)
    {
        if (response.Task is not null) RedactLocation(response.Task);

        if (response.Tasks is { Count: > 0 } tasks)
        {
            foreach (var task in tasks) RedactLocation(task);
        }

        if (response.Records is { Count: > 0 } records)
        {
            foreach (var record in records) RedactLocation(record);
        }
    }

    private void RedactLocation(ImageTask task) => task.LastFile = RelativeLocation(task.LastFile);

    private void RedactLocation(SyncRecord record) => record.FilePath = RelativeLocation(record.FilePath);

    /// <summary>归档位置 → 相对归档根的路径（WebDAV 地址与本机绝对路径都不外泄）。</summary>
    private string? RelativeLocation(string? location)
        => ArchivePath.Relative(location, _app.Config.Storage.Webdav.Url, _app.Config.Storage.ImageRoot);

    /// <summary>按顺序取第一个非空白值并去掉首尾空白（用于「请求体优先、查询串兜底」的多别名参数）。</summary>
    private static string? NormalizeText(params string?[] candidates)
    {
        foreach (var item in candidates)
        {
            if (!string.IsNullOrWhiteSpace(item)) return item.Trim();
        }

        return null;
    }

    /// <summary>查询串里的整数参数（interval、limit 等），缺失或非法返回 null。</summary>
    private static int? QueryInt(Request request, string key)
        => request.Query.TryGetValue(key, out var raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>查询串里的字符串参数（output 等），缺失或空白返回 null。</summary>
    private static string? QueryString(Request request, string key)
        => request.Query.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw.Trim() : null;

    /// <summary>
    /// 请求体里的 interval：既接受 <c>1440m</c> 这样的时长字符串，也接受裸数字（按秒）。缺失或非法返回 null。
    /// </summary>
    private static int? ReadInterval(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is not System.Text.Json.Nodes.JsonValue value) return null;

        // 裸数字：直接按秒理解（与 CLI 的「不带单位按秒」一致）
        if (value.TryGetValue<int>(out var seconds)) return seconds > 0 ? Math.Min(seconds, Duration.MaxSeconds) : null;

        return value.TryGetValue<string>(out var text) ? Duration.ParseSeconds(text) : null;
    }

    /// <summary>输出位置：空白视为未指定（回退默认归档规则）。</summary>
    private static string? NormalizeOutput(string? output)
        => string.IsNullOrWhiteSpace(output) ? null : output.Trim();

    /// <summary>查询串里的布尔参数（force、clear、disable 等）：1/true 为真，其余为假，缺失返回 null。</summary>
    private static bool? QueryBool(Request request, string key)
        => request.Query.TryGetValue(key, out var raw)
            ? string.Equals(raw, "1", StringComparison.Ordinal) || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
            : null;

    /// <summary>取出镜像名：优先请求体，其次 query 的 image 参数。</summary>
    private static string? PickImage(Request request, ApiRequest? payload)
    {
        if (!string.IsNullOrWhiteSpace(payload?.Image)) return payload!.Image.Trim();
        if (payload?.Images is { Count: > 0 } images)
        {
            foreach (var item in images)
            {
                if (!string.IsNullOrWhiteSpace(item)) return item.Trim();
            }
        }

        return request.Query.TryGetValue("image", out var fromQuery) && !string.IsNullOrWhiteSpace(fromQuery) ? fromQuery.Trim() : null;
    }

    private static ApiRequest ParsePayload(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.Body)) return new ApiRequest();

        try
        {
            return JsonSerializer.Deserialize(request.Body, ByxcrJson.Default.ApiRequest) ?? new ApiRequest();
        }
        catch (JsonException ex)
        {
            throw new ConfigurationException($"请求体不是合法 JSON：{ex.Message}");
        }
    }

    /// <summary>
    /// /api/list：镜像任务列表。<b>公开接口，无需 Authorization 令牌</b>（内置 Web 控制台页面就靠它取数据）。
    /// <para>筛选与分页：<c>filter</c>（别名 <c>keyword</c> / <c>q</c>）按镜像名做忽略大小写的子串筛选，
    /// <c>enabled</c> 按启用状态筛选，<c>page</c> + <c>limit</c> 分页（页码从 1 开始）。
    /// 不传 <c>limit</c> 时一次返回全部（与 <c>list --json</c> 行为一致），供脚本直接消费。</para>
    /// </summary>
    private (int Status, string Reason, string Body) List(Request request)
    {
        var payload = ParsePayload(request);

        var filter = NormalizeText(
            payload.Filter,
            QueryString(request, "filter"),
            QueryString(request, "keyword"),
            QueryString(request, "q"));
        var enabled = payload.Enabled ?? QueryBool(request, "enabled");
        var requested = payload.Limit ?? QueryInt(request, "limit");
        var requestedPage = payload.Page ?? QueryInt(request, "page") ?? 1;

        var total = _app.Images.CountMatching(filter, enabled);

        List<ImageTask> tasks;
        int page = 1;
        int pageCount = 1;
        int? pageSize = null;

        if (requested is > 0)
        {
            pageSize = Math.Clamp(requested.Value, 1, MaxPageSize);
            pageCount = Math.Max(1, (total + pageSize.Value - 1) / pageSize.Value);
            page = Math.Clamp(requestedPage, 1, pageCount);
            tasks = _app.Images.Query(filter, pageSize.Value, (page - 1) * pageSize.Value, enabled);
        }
        else
        {
            // 未指定 limit：不分页，一次返回全部（脚本友好）
            tasks = _app.Images.Query(filter, Math.Max(1, total), 0, enabled);
        }

        var scope = new StringBuilder();
        if (filter is not null) scope.Append($"｜筛选「{filter}」");
        if (enabled is not null) scope.Append(enabled.Value ? "｜仅已启用" : "｜仅已停用");

        return Json(200, "OK", new ApiResponse
        {
            Ok = true,
            Message = pageSize is null
                ? $"共 {total} 个镜像任务{scope}"
                : $"第 {page}/{pageCount} 页 · 共 {total} 个镜像任务{scope}",
            Tasks = tasks,
            Total = total,
            Page = pageSize is null ? null : page,
            Limit = pageSize,
            PageCount = pageSize is null ? null : pageCount,
            Filter = filter,
        });
    }

    private (int Status, string Reason, string Body) Records(Request request)
    {
        var payload = ParsePayload(request);
        var image = PickImage(request, payload);
        var limit = payload.Limit ?? QueryInt(request, "limit") ?? 20;

        var clear = payload.Clear ?? QueryBool(request, "clear") ?? false;

        int? removed = null;
        if (clear) removed = _app.Records.DeleteOlderThan(3, image);

        var records = _app.Records.Recent(Math.Clamp(limit, 1, 10000), image);
        return Json(200, "OK", new ApiResponse
        {
            Ok = true,
            Message = removed is null ? $"返回 {records.Count} 条记录" : $"已清除 {removed} 条 3 天前的记录，返回 {records.Count} 条",
            Records = records,
            Removed = removed,
        });
    }

    private async Task<(int Status, string Reason, string Body)> AddAsync(Request request)
    {
        var payload = ParsePayload(request);
        var name = PickImage(request, payload);
        if (string.IsNullOrWhiteSpace(name))
        {
            return Json(400, "Bad Request", new ApiResponse { Ok = false, Message = "缺少镜像名：请用 image 字段（或 ?image=）指定" });
        }

        if (!ImageReference.TryParse(name, out var image))
        {
            return Json(400, "Bad Request", new ApiResponse { Ok = false, Message = $"镜像名称无效：{name}" });
        }

        var output = NormalizeOutput(payload.Output ?? QueryString(request, "output"));

        // 显式给出的 interval（JSON 体的 interval 或查询串的 interval，裸数字按秒），没给时为 null。
        // 必须与「回落 sync.defaultIntervalMinutes」区分开：只有显式给出且与库中不一致才更新已存在任务的频率。
        var explicitInterval = ReadInterval(payload.Interval) ?? Duration.ParseSeconds(QueryString(request, "interval"));

        var existing = _app.Images.Find(image.CanonicalName);
        if (existing is not null)
        {
            // 改动项都记下来，统一拼进回执的 message
            var changes = new List<string>();

            // 带 output 时单独更新输出位置（下次同步起生效）
            if (output is not null)
            {
                _app.Images.SetOutput(image.CanonicalName, output);
                existing.Output = output;
                changes.Add($"已更新输出位置 {output}");
            }

            // 带 interval 且与库中不一致时更新检查频率；值相同则静默跳过，避免重复调用刷出无意义的「已更新」
            if (explicitInterval is { } nextInterval && nextInterval != existing.IntervalSeconds)
            {
                var previous = existing.IntervalSeconds;
                _app.Images.SetInterval(image.CanonicalName, nextInterval);
                existing.IntervalSeconds = Math.Max(1, nextInterval);
                changes.Add($"已更新检查频率 {Duration.Format(previous)} → {Duration.Format(existing.IntervalSeconds)}");
            }

            // 已存在：与 CLI 的 add 一致——已到期（含从未同步）才补一次后台下载
            // （频率刚改成更短的值时会因此变「已到期」，这里顺带补一次下载）
            var due = IsDue(existing);
            if (due) StartBackground(existing);

            var detail = changes.Count > 0 ? string.Join("｜", changes) + "｜" : string.Empty;
            return Json(200, "OK", new ApiResponse
            {
                Ok = true,
                Message = $"任务已存在：{image.CanonicalName}｜{detail}"
                          + (due ? "已到期，已在后台开始下载" : $"下次同步 {NextCheckText(existing)}"),
                Task = existing,
            });
        }

        // 与 CLI 的 add 一致：写入数据库前先确认仓库与标签真的能拉取（只取一次清单，不下载图层）；
        // 预检不过直接拒绝入库，?nocheck=true 可跳过。
        var note = string.Empty;
        var noCheck = payload.NoCheck ?? QueryBool(request, "nocheck") ?? QueryBool(request, "skipCheck") ?? false;
        if (!noCheck)
        {
            var probe = await _app.ProbeImageAsync(image, CancellationToken.None).ConfigureAwait(false);
            if (!probe.Ok)
            {
                Log.Warn($"add 预检未通过：{image.CanonicalName}｜{probe.Error}");
                return Json(400, "Bad Request", new ApiResponse
                {
                    Ok = false,
                    Message = $"无法拉取，未加入同步列表：{image.CanonicalName}｜{probe.Error}",
                });
            }

            note = $"｜校验通过 {probe.Summary}";
        }

        // 查询串与 JSON 体等价：GET /api/add?image=…&interval=720m&disable=true 与 POST 的 JSON 体一样生效
        // interval 支持 30d / 24h / 1440m / 86400s、多段组合（1d12h、3m30s）或裸数字（按秒）；没给才回落默认值
        var interval = explicitInterval ?? Duration.FromMinutes(_app.Config.Sync.DefaultIntervalMinutes);
        var enabled = !(payload.Disable ?? QueryBool(request, "disable") ?? false);
        var task = new ImageTask
        {
            Image = image.CanonicalName,
            Repository = image.Repository,
            Tag = image.Tag,
            IntervalSeconds = interval,
            Enabled = enabled,
            Output = output,
        };

        _app.Images.Upsert(task, overwriteSettings: true);
        var saved = _app.Images.Find(image.CanonicalName) ?? task;
        if (enabled) StartBackground(saved);

        return Json(200, "OK", new ApiResponse
        {
            Ok = true,
            // 归档位置只给相对路径：WebDAV 启用时 _app.Archive.Locate(...) 会带上集合地址，不适合对外输出
            Message = $"已加入同步列表：{image.CanonicalName}｜频率 {Duration.Format(interval)}｜{(enabled ? "启用" : "停用")}｜归档 {ArchiveNaming.Resolve(image, output)}{note}"
                     + (enabled ? "｜已在后台开始下载" : "｜未触发下载"),
            Task = saved,
        });
    }

    private async Task<(int Status, string Reason, string Body)> SyncAsync(Request request)
    {
        var payload = ParsePayload(request);
        var targets = new List<ImageTask>();

        var names = new List<string>();
        if (payload.Images is { Count: > 0 }) names.AddRange(payload.Images);
        else if (!string.IsNullOrWhiteSpace(payload.Image)) names.Add(payload.Image);
        else if (request.Query.TryGetValue("image", out var fromQuery)) names.Add(fromQuery);
        else if (request.Query.TryGetValue("images", out var csv)) names.AddRange(csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!ImageReference.TryParse(name, out var image))
            {
                return Json(400, "Bad Request", new ApiResponse { Ok = false, Message = $"镜像名称无效：{name}" });
            }

            var existing = _app.Images.Find(image.CanonicalName) ?? new ImageTask
            {
                Image = image.CanonicalName,
                Repository = image.Repository,
                Tag = image.Tag,
                IntervalSeconds = Duration.FromMinutes(_app.Config.Sync.DefaultIntervalMinutes),
                Enabled = true,
            };
            targets.Add(existing);
        }

        if (targets.Count == 0)
        {
            targets = _app.Images.GetAll().Where(task => task.Enabled).ToList();
            if (targets.Count == 0)
            {
                return Json(200, "OK", new ApiResponse { Ok = true, Message = "同步列表为空，没有可同步的镜像" });
            }
        }

        var force = payload.Force ?? QueryBool(request, "force") ?? false;

        // output：本次同步专用的归档位置（与 `sync -o` 一致）。多镜像时当成目录，其下按默认结构存放。
        var output = NormalizeOutput(payload.Output ?? QueryString(request, "output"));
        var outputAsDirectory = targets.Count > 1;

        string? OverrideFor(ImageTask task)
        {
            if (output is null) return null;
            return ImageReference.TryParse(task.Image, out var parsed)
                ? ArchiveNaming.Resolve(parsed, output, outputAsDirectory)
                : null;
        }

        var concurrency = Math.Clamp(_app.Config.Sync.MaxConcurrency, 1, 32);
        using var semaphore = new SemaphoreSlim(concurrency, concurrency);
        var results = new SyncRecord?[targets.Count];

        var jobs = targets.Select(async (task, index) =>
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                // API 触发的同步与手动 sync 一样使用 cli trigger：不受调度租约限制，立即执行
                results[index] = await _app.Sync.SyncAsync(task, ImageSyncService.ManualTrigger, force, CancellationToken.None, OverrideFor(task)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($"[{task.Image}] 同步异常：{ex.Message}");
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        await Task.WhenAll(jobs).ConfigureAwait(false);

        var records = results.Where(record => record is not null).Select(record => record!).ToList();
        var failed = records.Count(record => record.Status == "failed");

        return Json(200, "OK", new ApiResponse
        {
            Ok = failed == 0,
            Message = (failed == 0
                ? $"已同步 {records.Count} 个镜像"
                : $"已同步 {records.Count} 个镜像，其中 {failed} 个失败")
                + (output is null ? "" : $"｜输出位置 {output}{(outputAsDirectory ? "（目录）" : "")}"),
            Records = records,
        });
    }

    private (int Status, string Reason, string Body) Remove(Request request)
    {
        var payload = ParsePayload(request);
        var name = PickImage(request, payload);
        if (string.IsNullOrWhiteSpace(name))
        {
            return Json(400, "Bad Request", new ApiResponse { Ok = false, Message = "缺少镜像名：请用 image 字段（或 ?image=）指定" });
        }

        return _app.Images.Delete(name)
            ? Json(200, "OK", new ApiResponse { Ok = true, Message = $"已从同步列表移除：{name}" })
            : Json(404, "Not Found", new ApiResponse { Ok = false, Message = $"同步列表中不存在：{name}" });
    }

    private (int Status, string Reason, string Body) Toggle(Request request, bool enabled)
    {
        var payload = ParsePayload(request);
        var name = PickImage(request, payload);
        if (string.IsNullOrWhiteSpace(name))
        {
            return Json(400, "Bad Request", new ApiResponse { Ok = false, Message = "缺少镜像名：请用 image 字段（或 ?image=）指定" });
        }

        if (!_app.Images.SetEnabled(name, enabled))
        {
            return Json(404, "Not Found", new ApiResponse { Ok = false, Message = $"同步列表中不存在：{name}" });
        }

        var task = _app.Images.Find(name);
        return Json(200, "OK", new ApiResponse
        {
            Ok = true,
            Message = $"{(enabled ? "已启用" : "已停用")}：{name}",
            Task = task,
        });
    }

    /// <summary>add 后在本进程内启动后台下载（服务是长驻进程，无需再拉子进程）。</summary>
    private void StartBackground(ImageTask task)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _app.Sync.SyncAsync(task, BackgroundTrigger, false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($"[{task.Image}] 后台同步异常：{ex.Message}");
            }
        });
    }

    private static bool IsDue(ImageTask task)
    {
        if (!task.Enabled) return false;

        var last = Clock.Parse(task.LastCheckedAt);
        return last is null || DateTime.UtcNow - last.Value >= TimeSpan.FromSeconds(Math.Max(1, task.IntervalSeconds));
    }

    private static string NextCheckText(ImageTask task)
    {
        var last = Clock.Parse(task.LastCheckedAt) ?? DateTime.UtcNow;
        return Clock.Local(last + TimeSpan.FromSeconds(Math.Max(1, task.IntervalSeconds)));
    }

    private static IPAddress ResolveAddress(string host, out string text)
    {
        if (string.IsNullOrWhiteSpace(host) || host is "*" or "+" or "0.0.0.0" or "::")
        {
            text = "0.0.0.0";
            return IPAddress.Any;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            text = "127.0.0.1";
            return IPAddress.Loopback;
        }

        if (IPAddress.TryParse(host, out var address))
        {
            text = address.ToString();
            return address;
        }

        text = host;
        Log.Warn($"api.host 不是合法 IP（{host}），已回退到监听所有地址；请填 127.0.0.1 或具体网卡地址");
        return IPAddress.Any;
    }
}
