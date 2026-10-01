using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace WeiboAlbumDownloader.Converters
{
    /// <summary>
    /// 兼容微博把列表字段序列化成"以数字字符串为键的 JSON 对象"的变形。
    /// 支持四种形状：标准数组 <c>[..]</c>、映射对象 <c>{"0":..,"1":..}</c>、空对象 <c>{}</c> 与 <c>null</c>。
    /// </summary>
    /// <typeparam name="TItem">列表元素类型。</typeparam>
    public class JsonArrayOrObjectConverter<TItem> : JsonConverter<List<TItem>>
    {
        public override List<TItem>? ReadJson(JsonReader reader, Type objectType, List<TItem>? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            switch (reader.TokenType)
            {
                case JsonToken.Null:
                    return null;

                //标准数组：交给 Newtonsoft 默认逻辑，行为与未挂转换器时完全一致
                case JsonToken.StartArray:
                    return serializer.Deserialize<List<TItem>>(reader);

                //变形对象：{"0":..,"1":..}，按数字键升序还原发布顺序（顺序决定 _1/_2 编号与实况封面配对）
                case JsonToken.StartObject:
                    Dictionary<string, TItem>? map = serializer.Deserialize<Dictionary<string, TItem>>(reader);
                    if (map == null)
                        return new List<TItem>();

                    return map
                        .Where(pair => pair.Value != null)
                        .OrderBy(pair => ParseKey(pair.Key))
                        .Select(pair => pair.Value)
                        .ToList();

                default:
                    throw new JsonSerializationException($"无法把 JSON 的 {reader.TokenType} 值反序列化为 {typeof(List<TItem>)}。Path '{reader.Path}'。");
            }
        }

        public override void WriteJson(JsonWriter writer, List<TItem>? value, JsonSerializer serializer)
        {
            //本项目不序列化这些模型；此处手写数组而不用 serializer.Serialize，避免再次命中本转换器造成无限递归
            writer.WriteStartArray();
            if (value != null)
            {
                foreach (TItem item in value)
                {
                    serializer.Serialize(writer, item);
                }
            }
            writer.WriteEndArray();
        }

        /// <summary>
        /// 把映射对象的键解析为排序序号：数字键按数值排序，非数字键排到最后且保持原有相对顺序。
        /// </summary>
        private static long ParseKey(string key)
        {
            return long.TryParse(key, out long index) ? index : long.MaxValue;
        }
    }
}
