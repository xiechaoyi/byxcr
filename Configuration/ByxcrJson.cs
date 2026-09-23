using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Byxcr.Core;

namespace Byxcr.Configuration;

/// <summary>
/// System.Text.Json 源生成上下文：NativeAOT 下无反射序列化的唯一入口。
/// <para><see cref="JsonNumberHandling.AllowReadingFromString"/> 是为了配合 YAML 配置的隐式类型，
/// 允许把 <c>"3"</c> 这类字符串读成数字；反向（数字读成字符串）在个别易踩坑的属性上用
/// <see cref="LenientStringConverter"/> 单独标注，见 <see cref="WebdavConfig"/> 与 <see cref="RegistryCredential"/>。</para>
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(ImageTask))]
[JsonSerializable(typeof(List<ImageTask>))]
[JsonSerializable(typeof(SyncRecord))]
[JsonSerializable(typeof(List<SyncRecord>))]
[JsonSerializable(typeof(RegistryStatus))]
[JsonSerializable(typeof(List<RegistryStatus>))]
[JsonSerializable(typeof(StatusReport))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(RegistryCredential))]
[JsonSerializable(typeof(Dictionary<string, RegistryCredential>))]
[JsonSerializable(typeof(ApiRequest))]
[JsonSerializable(typeof(ApiResponse))]
[JsonSerializable(typeof(ApiEndpoint))]
[JsonSerializable(typeof(List<ApiEndpoint>))]
public partial class ByxcrJson : JsonSerializerContext
{
    /// <summary>
    /// 面向「给人看」的 JSON 输出（WebAPI 响应、<c>--json</c> 打印）：非 ASCII 字符（中文等）原样输出，
    /// 不编码成 <c>\uXXXX</c>。源生成选项里没有 Encoder 一项，只能靠 <see cref="Utf8JsonWriter"/> 指定。
    /// </summary>
    private static readonly JsonWriterOptions RelaxedWriter = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = true,
    };

    /// <summary>用宽松转义把对象序列化成 JSON 文本（中文不转义）。</summary>
    public static string ToJson<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, RelaxedWriter))
        {
            JsonSerializer.Serialize(writer, value, typeInfo);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
