using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Byxcr.Configuration;

/// <summary>
/// 环境变量覆盖层。环境变量优先级高于配置文件。
/// <para>两种写法：</para>
/// <list type="bullet">
/// <item>嵌套式：<c>BYXCR__Storage__DataDir=/data</c>（双下划线表示层级，可覆盖任意配置项）</item>
/// <item>扁平别名：<c>BYXCR_DATA_DIR=/data</c>、<c>BYXCR_REGISTRIES=docker.1ms.run,mirror.ccs.tencentyun.com</c></item>
/// </list>
/// </summary>
public static class EnvOverride
{
    public const string NestedPrefix = "BYXCR__";

    /// <summary>常用配置项的扁平别名（先应用，随后嵌套式覆盖会再次生效并优先）。</summary>
    private static readonly (string Env, string Path)[] Aliases =
    [
        ("BYXCR_DATA_DIR", "Storage.DataDir"),
        ("BYXCR_DB_FILE", "Storage.DatabaseFile"),
        ("BYXCR_IMAGE_ROOT", "Storage.ImageRoot"),
        ("BYXCR_TEMP_DIR", "Storage.TempDir"),
        ("BYXCR_KEEP_TAR", "Storage.KeepTar"),

        ("BYXCR_WEBDAV", "Storage.Webdav.Enabled"),
        ("BYXCR_WEBDAV_URL", "Storage.Webdav.Url"),
        ("BYXCR_WEBDAV_USERNAME", "Storage.Webdav.Username"),
        ("BYXCR_WEBDAV_PASSWORD", "Storage.Webdav.Password"),
        ("BYXCR_WEBDAV_KEEP_LOCAL", "Storage.Webdav.KeepLocalCopy"),

        ("BYXCR_PLATFORMS", "Download.Platforms"),
        ("BYXCR_DOWNLOAD_RETRIES", "Download.MaxRetries"),
        ("BYXCR_REQUEST_TIMEOUT", "Download.RequestTimeoutSeconds"),
        ("BYXCR_MAX_PARALLEL_DOWNLOADS", "Download.MaxParallelDownloads"),

        ("BYXCR_GZIP_MODE", "Compression.Mode"),
        ("BYXCR_GZIP_LEVEL", "Compression.Level"),
        ("BYXCR_GZIP_PATH", "Compression.GzipPath"),
        ("BYXCR_GZIP_THREADS", "Compression.Threads"),

        ("BYXCR_REGISTRIES", "Registry.Items"),
        ("BYXCR_USE_DEFAULT_REGISTRY_FIRST", "Registry.UseDefaultRegistryFirst"),
        ("BYXCR_VERIFY_REGISTRY", "Registry.VerifyBeforeUse"),

        ("BYXCR_SYNC_ENABLED", "Sync.Enabled"),
        ("BYXCR_SYNC_INTERVAL", "Sync.DefaultIntervalMinutes"),
        ("BYXCR_SCAN_INTERVAL", "Sync.ScanIntervalSeconds"),
        ("BYXCR_MAX_CONCURRENCY", "Sync.MaxConcurrency"),
        ("BYXCR_SCHEDULE_CONCURRENCY", "Sync.ScheduleConcurrency"),
        ("BYXCR_CHECK_STRATEGY", "Sync.CheckStrategy"),
        ("BYXCR_IGNORE_ARCHIVE_CHECK", "Sync.IgnoreArchiveCheck"),
        ("BYXCR_SYNC_ON_STARTUP", "Sync.SyncOnStartup"),

        ("BYXCR_API", "Api.Enabled"),
        ("BYXCR_API_ENABLED", "Api.Enabled"),
        ("BYXCR_API_HOST", "Api.Host"),
        ("BYXCR_API_PORT", "Api.Port"),
        ("BYXCR_API_TOKEN", "Api.Token"),

        ("BYXCR_LOG_LEVEL", "Log.Level"),
        ("BYXCR_LOG_FILE", "Log.File"),
        ("BYXCR_LOG_COLOR", "Log.Color"),
        ("BYXCR_TZ", "Log.TimeZone"),
        ("BYXCR_LOG_DIR", "Log.Directory"),
    ];

    /// <summary>已废弃的配置路径：跳过而非应用，避免被误报为「生效的环境变量」。</summary>
    private static readonly string[] DeprecatedPaths = ["Sync.Images"];

    /// <summary>返回实际生效的环境变量名列表（便于诊断）。</summary>
    public static List<string> Apply(JsonObject root)
    {
        var applied = new List<string>();

        foreach (var (envName, path) in Aliases)
        {
            var value = Environment.GetEnvironmentVariable(envName);
            if (value is null) continue;
            if (SetPath(root, path, value)) applied.Add(envName);
        }

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key || entry.Value is not string raw) continue;
            if (!key.StartsWith(NestedPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var path = key[NestedPrefix.Length..];
            if (path.Length == 0) continue;
            if (IsDeprecated(path)) continue;
            if (SetPath(root, path, raw)) applied.Add(key);
        }

        return applied;
    }

    private static bool IsDeprecated(string path)
    {
        var normalized = path.Replace("__", ".", StringComparison.Ordinal);
        return DeprecatedPaths.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static bool SetPath(JsonObject root, string path, string raw)
    {
        var parts = path
            .Replace("__", ".", StringComparison.Ordinal)
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        JsonNode? current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            current = Descend(current, parts[i]);
            if (current is null) return false;
        }

        var last = parts[^1];

        if (current is JsonArray array)
        {
            if (!int.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) return false;
            if (index < 0 || index >= array.Count) return false;
            array[index] = ConvertValue(array[index], raw);
            return true;
        }

        if (current is JsonObject obj)
        {
            var key = JsonMerge.FindKey(obj, last) ?? last;
            obj[key] = ConvertValue(JsonMerge.GetValue(obj, last), raw);
            return true;
        }

        return false;
    }

    private static JsonNode? Descend(JsonNode? node, string segment)
    {
        switch (node)
        {
            case JsonArray array:
                if (!int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) return null;
                if (index < 0 || index >= array.Count) return null;
                array[index] ??= new JsonObject();
                return array[index];

            case JsonObject obj:
            {
                var key = JsonMerge.FindKey(obj, segment);
                if (key is null)
                {
                    var created = new JsonObject();
                    obj[segment] = created;
                    return created;
                }
                obj[key] ??= new JsonObject();
                return obj[key];
            }

            default:
                return null;
        }
    }

    private static JsonNode? ConvertValue(JsonNode? existing, string raw)
    {
        var value = raw.Trim();

        if (existing is JsonArray template) return ParseArray(value, template);

        if (existing is JsonObject) return TryParseJson(value) ?? Str(value);

        if (value.StartsWith('[') || value.StartsWith('{'))
        {
            var parsed = TryParseJson(value);
            if (parsed is not null) return parsed;
        }

        if (existing is JsonValue boolNode && boolNode.TryGetValue<bool>(out _)) return JsonValue.Create(ParseBool(value));

        if (existing is JsonValue numNode && (numNode.TryGetValue<long>(out _) || numNode.TryGetValue<double>(out _))) return ParseNumber(value);

        return Infer(value);
    }

    private static JsonNode ParseArray(string value, JsonArray template)
    {
        // 模板元素是对象时（如 Registry.Items），标量元素需要补全为对象。
        // 通过拼接 JSON 文本再解析，避免走 JsonArray.Add<T> 的反射/AOT 受限路径。
        string? nameKey = null;
        if (template.Count > 0 && template[0] is JsonObject tpl)
        {
            nameKey = JsonMerge.FindKey(tpl, "name")
                      ?? (tpl.Count > 0 ? tpl.First().Key : "name");
        }

        if (value.StartsWith('['))
        {
            var parsed = TryParseJson(value);
            if (parsed is JsonArray parsedArray)
            {
                if (nameKey is null) return parsedArray;

                var normalized = new StringBuilder("[");
                var appended = 0;
                foreach (var item in parsedArray)
                {
                    string? text = null;
                    if (item is JsonObject) text = item.ToJsonString();
                    else if (item is JsonValue scalar && scalar.TryGetValue<string>(out var raw)) text = $"{{{Encode(nameKey)}:{Encode(raw)}}}";
                    if (text is null) continue;

                    if (appended > 0) normalized.Append(',');
                    normalized.Append(text);
                    appended++;
                }
                normalized.Append(']');
                return TryParseJson(normalized.ToString()) ?? parsedArray;
            }

            if (parsed is not null) return parsed;
        }

        var items = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var builder = new StringBuilder("[");
        for (var i = 0; i < items.Length; i++)
        {
            if (i > 0) builder.Append(',');
            if (nameKey is null)
            {
                builder.Append(Encode(items[i]));
            }
            else
            {
                builder.Append('{').Append(Encode(nameKey)).Append(':').Append(Encode(items[i])).Append('}');
            }
        }
        builder.Append(']');

        return TryParseJson(builder.ToString()) ?? new JsonArray();
    }

    private static string Encode(string value) => JsonSerializer.Serialize(value, ByxcrJson.Default.String);

    private static JsonNode Infer(string value)
    {
        if (bool.TryParse(value, out var b)) return JsonValue.Create(b)!;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l)!;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d)!;
        return Str(value);
    }

    private static JsonNode ParseNumber(string value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l)!;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d)!;
        return Str(value);
    }

    private static bool ParseBool(string value)
    {
        if (bool.TryParse(value, out var b)) return b;
        return value.Trim().ToLowerInvariant() is "1" or "yes" or "y" or "on" or "enable" or "enabled";
    }

    private static JsonNode Str(string value) => JsonValue.Create(value)!;

    private static JsonNode? TryParseJson(string value)
    {
        try
        {
            return JsonNode.Parse(value, null, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
