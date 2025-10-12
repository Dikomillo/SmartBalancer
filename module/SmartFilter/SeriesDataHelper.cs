using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SmartFilter
{
    internal static class SeriesDataHelper
    {
        internal sealed class SeriesDataPayload
        {
            public SeriesDataPayload(JArray items, JArray voice, string maxQuality, JToken original, JObject container, JObject metadata)
            {
                Items = items ?? new JArray();
                Voice = voice;
                MaxQuality = maxQuality;
                Original = original;
                Container = container;
                Metadata = metadata;
            }

            public JArray Items { get; }
            public JArray Voice { get; }
            public string MaxQuality { get; }
            public JToken Original { get; }
            public JObject Container { get; }
            public JObject Metadata { get; }
        }

        public static SeriesDataPayload Extract(string type, JToken payload)
        {
            var clone = payload?.DeepClone();
            var voice = ExtractVoice(clone);
            var maxQuality = ExtractQuality(clone);
            var items = ExtractItems(clone);

            if (string.IsNullOrWhiteSpace(maxQuality) && items != null)
            {
                maxQuality = items
                    .OfType<JObject>()
                    .Select(i => i.Value<string>("maxquality") ?? i.Value<string>("quality"))
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            }
            var container = BuildContainer(type, clone, items, voice, maxQuality);
            var metadata = BuildMetadata(items, voice, maxQuality);

            return new SeriesDataPayload(items, voice, maxQuality, clone, container, metadata);
        }

        private static JArray ExtractItems(JToken payload)
        {
            if (payload is JObject obj)
            {
                if (TryExtractArray(obj, "data", out var fromData))
                    return fromData;

                if (TryExtractArray(obj, "results", out var fromResults))
                    return fromResults;

                if (TryExtractArray(obj, "episodes", out var fromEpisodes))
                    return fromEpisodes;

                if (TryExtractArray(obj, "seasons", out var fromSeasons))
                    return fromSeasons;

                var aggregated = new JArray();
                foreach (var property in obj.Properties())
                {
                    if (property.Value == null || property.Value.Type == JTokenType.Null)
                        continue;

                    foreach (var item in EnumerateItems(property.Value))
                        aggregated.Add(item.DeepClone());
                }

                return aggregated;
            }

            if (payload is JArray array)
                return (JArray)array.DeepClone();

            return new JArray();
        }

        private static bool TryExtractArray(JObject obj, string key, out JArray result)
        {
            result = null;
            if (obj == null || string.IsNullOrWhiteSpace(key))
                return false;

            if (!obj.TryGetValue(key, out var token) || token == null || token.Type == JTokenType.Null)
                return false;

            if (token is JArray array)
            {
                if (array.Count == 0)
                    return false;

                result = (JArray)array.DeepClone();
                return true;
            }

            if (token is JObject nested)
            {
                var aggregated = new JArray();
                foreach (var property in nested.Properties())
                {
                    if (property.Value is JArray propertyArray)
                    {
                        foreach (var item in propertyArray)
                            aggregated.Add(item.DeepClone());
                    }
                    else if (property.Value is JObject propertyObject)
                    {
                        aggregated.Add(propertyObject.DeepClone());
                    }
                }

                if (aggregated.Count > 0)
                {
                    result = aggregated;
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<JToken> EnumerateItems(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                yield break;

            if (token is JArray array)
            {
                foreach (var item in array)
                    yield return item;

                yield break;
            }

            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    foreach (var item in EnumerateItems(property.Value))
                        yield return item;
                }
            }
        }

        private static JArray ExtractVoice(JToken payload)
        {
            if (payload is not JObject obj)
                return null;

            var voiceToken = obj["voice"] ?? obj["voices"] ?? obj["voice_list"] ?? obj["translations"];
            if (voiceToken is JArray voiceArray && voiceArray.Count > 0)
                return (JArray)voiceArray.DeepClone();

            return null;
        }

        private static string ExtractQuality(JToken payload)
        {
            if (payload is not JObject obj)
                return null;

            foreach (var property in obj.Properties())
            {
                if (!string.Equals(property.Name, "maxquality", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(property.Name, "quality", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string value = property.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        private static JObject BuildContainer(string type, JToken payload, JArray items, JArray voice, string maxQuality)
        {
            var container = payload as JObject ?? new JObject();

            if (!string.IsNullOrWhiteSpace(type))
                container["type"] = type;

            if (voice != null)
                container["voice"] = voice.DeepClone();

            if (!string.IsNullOrWhiteSpace(maxQuality))
                container["maxquality"] = maxQuality;

            if (items != null && items.Count > 0)
            {
                if (string.Equals(type, "episode", StringComparison.OrdinalIgnoreCase))
                    container["episodes"] = items.DeepClone();
                else
                    container["seasons"] = items.DeepClone();
            }

            var grouped = ExtractGroupedSeasons(container);
            if (grouped != null)
                container["groupedSeasons"] = grouped;

            return container;
        }

        private static JObject ExtractGroupedSeasons(JObject container)
        {
            if (container == null)
                return null;

            foreach (var key in new[] { "providers", "grouped", "playlists" })
            {
                if (!container.TryGetValue(key, out var token) || token is not JObject obj || !obj.Properties().Any())
                    continue;

                var grouped = new JObject();
                foreach (var property in obj.Properties())
                {
                    if (property.Value is JArray array && array.Count > 0)
                        grouped[property.Name] = array.DeepClone();
                }

                if (grouped.Properties().Any())
                    return grouped;
            }

            return null;
        }

        private static JObject BuildMetadata(JArray items, JArray voice, string maxQuality)
        {
            var metadata = new JObject();

            var voicesMap = new JObject();
            foreach (var label in CollectVoiceLabels(items, voice))
            {
                var key = NormalizeKey(label);
                if (!voicesMap.TryGetValue(key, out var existing) || existing is not JObject entry)
                {
                    entry = new JObject
                    {
                        ["label"] = label,
                        ["count"] = 0
                    };
                    voicesMap[key] = entry;
                }

                entry["count"] = entry.Value<int>("count") + 1;
            }

            if (voicesMap.Properties().Any())
                metadata["voices"] = voicesMap;

            var qualityMap = new JObject();
            foreach (var label in CollectQualityLabels(items, maxQuality))
            {
                var key = NormalizeKey(label);
                if (!qualityMap.TryGetValue(key, out var existing) || existing is not JObject entry)
                {
                    entry = new JObject
                    {
                        ["label"] = label,
                        ["count"] = 0
                    };
                    qualityMap[key] = entry;
                }

                entry["count"] = entry.Value<int>("count") + 1;
            }

            if (qualityMap.Properties().Any())
                metadata["qualities"] = qualityMap;

            return metadata.HasValues ? metadata : null;
        }

        private static IEnumerable<string> CollectVoiceLabels(JArray items, JArray voice)
        {
            var labels = new List<string>();

            if (voice != null)
            {
                foreach (var token in voice.OfType<JObject>())
                {
                    string label = token.Value<string>("name") ?? token.Value<string>("title") ?? token.Value<string>("voice") ?? token.Value<string>("translation");
                    if (!string.IsNullOrWhiteSpace(label))
                        labels.Add(label);
                }
            }

            if (items != null)
            {
                foreach (var token in items.OfType<JObject>())
                {
                    string label = token.Value<string>("voice") ??
                                   token.Value<string>("voice_name") ??
                                   token.Value<string>("translate") ??
                                   token.Value<string>("details");

                    if (!string.IsNullOrWhiteSpace(label))
                        labels.Add(label);
                }
            }

            return labels;
        }

        private static IEnumerable<string> CollectQualityLabels(JArray items, string maxQuality)
        {
            var labels = new List<string>();

            if (!string.IsNullOrWhiteSpace(maxQuality))
                labels.Add(maxQuality);

            if (items != null)
            {
                foreach (var token in items.OfType<JObject>())
                {
                    string label = token.Value<string>("maxquality") ?? token.Value<string>("quality");
                    if (!string.IsNullOrWhiteSpace(label))
                        labels.Add(label);
                }
            }

            return labels;
        }

        private static string NormalizeKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var builder = new StringBuilder();
            foreach (var ch in value.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(ch);
                }
                else if (char.IsWhiteSpace(ch) || ch == '-' || ch == '_' || ch == '.')
                {
                    builder.Append('_');
                }
            }

            return builder.Length > 0 ? builder.ToString() : value.Trim().ToLowerInvariant();
        }
    }
}
