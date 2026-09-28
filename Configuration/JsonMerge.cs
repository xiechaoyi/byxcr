using System.Text.Json.Nodes;

namespace Byxcr.Configuration;

/// <summary>JSON 节点大小写无关访问与深合并。</summary>
public static class JsonMerge
{
    /// <summary>大小写无关地查找真实键名，找不到返回 null。</summary>
    public static string? FindKey(JsonObject obj, string key)
    {
        foreach (var kv in obj)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Key;
        }
        return null;
    }

    public static JsonNode? GetValue(JsonObject obj, string key)
        => FindKey(obj, key) is { } k ? obj[k] : null;

    /// <summary>把 source 深度合并进 target（source 优先，数组整体替换）。</summary>
    public static void DeepMerge(JsonObject target, JsonObject source)
    {
        var snapshot = new List<KeyValuePair<string, JsonNode?>>(source.Count);
        foreach (var kv in source) snapshot.Add(kv);

        foreach (var kv in snapshot)
        {
            var key = FindKey(target, kv.Key) ?? kv.Key;
            var current = target[key];
            if (current is JsonObject to && kv.Value is JsonObject so)
            {
                DeepMerge(to, so);
            }
            else
            {
                target[key] = kv.Value?.DeepClone();
            }
        }
    }
}
