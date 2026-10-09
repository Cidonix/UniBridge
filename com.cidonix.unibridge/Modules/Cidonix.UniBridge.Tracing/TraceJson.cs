using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.Tracing
{
    /// <summary>
    /// Keeps trace records and persisted trace configuration independent of
    /// application-wide Newtonsoft defaults, without an Editor dependency.
    /// </summary>
    static class TraceJson
    {
        public static string SerializeObject(object value)
        {
            return SerializeObject(value, Formatting.None, null);
        }

        public static string SerializeObject(object value, Formatting formatting, JsonSerializerSettings settings)
        {
            var serializer = JsonSerializer.Create(settings);
            serializer.Formatting = formatting;
            using var text = new StringWriter(CultureInfo.InvariantCulture);
            using var writer = new JsonTextWriter(text);
            serializer.Serialize(writer, value);
            return text.ToString();
        }

        public static T DeserializeObject<T>(string value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            var serializer = JsonSerializer.Create();
            serializer.CheckAdditionalContent = true;
            using var text = new StringReader(value);
            using var reader = new JsonTextReader(text);
            return serializer.Deserialize<T>(reader);
        }

        public static JObject ObjectFromObject(object value)
        {
            return JObject.FromObject(value, JsonSerializer.Create());
        }
    }
}
