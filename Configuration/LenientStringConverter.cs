using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Byxcr.Configuration;

/// <summary>
/// 宽松的字符串读取器：允许把 YAML 中未加引号的数字 / 布尔字面量读成字符串。
/// <para>YAML 有隐式类型，手写 <c>password: 123456</c> 会被解析成数字，而配置项是字符串类型；
/// 这里按目标类型做一次宽松转换，免得用户必须给纯数字内容补引号。</para>
/// <para>手写实现，不使用反射，可在 NativeAOT 下工作。</para>
/// </summary>
public sealed class LenientStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();

            case JsonTokenType.Number:
                // 用 JsonDocument 取值，避免 long/double 转换丢失精度（如超长数字型口令）
                using (var document = JsonDocument.ParseValue(ref reader))
                {
                    return document.RootElement.GetRawText();
                }

            case JsonTokenType.True:
                return "true";

            case JsonTokenType.False:
                return "false";

            case JsonTokenType.Null:
                return null;

            default:
                throw new JsonException(string.Format(
                    CultureInfo.InvariantCulture,
                    "无法把 {0} 读取为字符串",
                    reader.TokenType));
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}
