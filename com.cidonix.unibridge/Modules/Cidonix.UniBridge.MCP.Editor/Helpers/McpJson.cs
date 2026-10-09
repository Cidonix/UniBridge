using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    /// <summary>
    /// Owns Editor JSON contracts independently of a project's global Newtonsoft settings.
    /// A serializer is created for each operation because serializers are mutable and not
    /// safe to share between background connections and Editor tool execution.
    /// </summary>
    static class McpJson
    {
        public static JsonSerializer CreateSerializer(JsonSerializerSettings settings = null)
        {
            return JsonSerializer.Create(settings);
        }

        public static string SerializeObject(object value)
        {
            return SerializeObject(value, Formatting.None, null);
        }

        public static string SerializeObject(object value, Formatting formatting)
        {
            return SerializeObject(value, formatting, null);
        }

        public static string SerializeObject(object value, JsonSerializerSettings settings)
        {
            return SerializeObject(value, settings?.Formatting ?? Formatting.None, settings);
        }

        public static string SerializeObject(object value, Formatting formatting, JsonSerializerSettings settings)
        {
            var serializer = CreateSerializer(settings);
            serializer.Formatting = formatting;
            using var text = new StringWriter(CultureInfo.InvariantCulture);
            using var writer = new JsonTextWriter(text);
            serializer.Serialize(writer, value);
            return text.ToString();
        }

        public static T DeserializeObject<T>(string value, JsonSerializerSettings settings = null)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));

            var serializer = CreateSerializer(settings);
            // Unconfigured input admits one complete JSON value. A caller that
            // supplies settings owns this policy explicitly; never inspect or
            // inherit process-wide defaults to fill it in.
            serializer.CheckAdditionalContent = settings?.CheckAdditionalContent ?? true;
            using var text = new StringReader(value);
            using var reader = new JsonTextReader(text);
            return serializer.Deserialize<T>(reader);
        }

        public static JObject ObjectFromObject(object value)
        {
            return JObject.FromObject(value, CreateSerializer());
        }

        public static JArray ArrayFromObject(object value)
        {
            return JArray.FromObject(value, CreateSerializer());
        }

        public static JToken TokenFromObject(object value)
        {
            return JToken.FromObject(value, CreateSerializer());
        }

        public static T ToObjectIndependent<T>(this JToken value)
        {
            return value.ToObject<T>(CreateSerializer());
        }

        public static object ToObjectIndependent(this JToken value, Type objectType)
        {
            return value.ToObject(objectType, CreateSerializer());
        }
    }
}
