using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace SmartFilter
{
    internal static class ResponseRenderer
    {
        public static string BuildHtml(string type, JToken data, string title, string originalTitle)
        {
            if (data == null)
                return string.Empty;

            bool isSeason = string.Equals(type, "season", StringComparison.OrdinalIgnoreCase);
            bool isEpisode = string.Equals(type, "episode", StringComparison.OrdinalIgnoreCase);

            string voiceHtml = null;
            string maxQuality = null;
            JArray seasonData = null;
            JArray episodeData = null;
            JObject groupedSeasons = null;

            if (isSeason || isEpisode)
            {
                var (seriesData, voiceData, quality) = SeriesDataHelper.Unpack(data);
                data = seriesData;
                maxQuality = quality;

                if (voiceData != null && voiceData.Count > 0)
                    voiceHtml = BuildVoiceHtml(voiceData);

                if (seriesData is JObject container)
                {
                    seasonData = container["seasons"] as JArray;
                    episodeData = container["episodes"] as JArray;
                    groupedSeasons = container["groupedSeasons"] as JObject;
                }
                else if (seriesData is JArray seriesArray)
                {
                    seasonData = seriesArray;
                }
            }

            if (isSeason)
            {
                if (groupedSeasons != null && groupedSeasons.Properties().Any())
                {
                    var groupedHtml = BuildGroupedSeasonHtml(groupedSeasons, maxQuality);
                    if (string.IsNullOrEmpty(groupedHtml))
                        return voiceHtml ?? string.Empty;

                    return string.Concat(voiceHtml ?? string.Empty, groupedHtml);
                }

                data = seasonData ?? new JArray();
            }
            else if (isEpisode)
            {
                data = episodeData ?? new JArray();
            }
            else if (data is JObject grouped)
            {
                if (!grouped.Properties().Any())
                    return voiceHtml ?? string.Empty;

                data = Flatten(grouped);
            }

            if (data is not JArray items || items.Count == 0)
                return voiceHtml ?? string.Empty;

            string content = type switch
            {
                "similar" => BuildSimilarHtml(items),
                "season" => BuildSeasonHtml(items, maxQuality),
                "episode" => BuildEpisodeHtml(items),
                _ => BuildMovieHtml(items, title, originalTitle)
            };

            if (string.IsNullOrEmpty(content))
                return voiceHtml ?? string.Empty;

            return string.Concat(voiceHtml ?? string.Empty, content);
        }

        private static string BuildSimilarHtml(JArray data)
        {
            var html = new StringBuilder();
            html.Append("<div class=\"videos__line\" data-smartfilter=\"true\">");
            bool first = true;

            foreach (var token in data.OfType<JObject>())
            {
                string url = token.Value<string>("url") ?? token.Value<string>("link");
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                string title = token.Value<string>("title") ?? token.Value<string>("name") ?? "Источник";
                string method = token.Value<string>("method") ?? "link";
                string details = token.Value<string>("details");

                var payload = new JObject
                {
                    ["method"] = method,
                    ["url"] = url
                };

                string provider = token.Value<string>("provider");
                if (!string.IsNullOrWhiteSpace(provider))
                    payload["provider"] = provider;

                string serialized = JsonConvert.SerializeObject(payload, Formatting.None);

                html.Append("<div class=\"videos__item videos__season selector ");
                if (first)
                {
                    html.Append("focused ");
                    first = false;
                }

                html.Append("\" data-json='");
                html.Append(WebUtility.HtmlEncode(serialized));
                html.Append("'><div class=\"videos__season-layers\"></div><div class=\"videos__item-imgbox videos__season-imgbox\"><div class=\"videos__item-title videos__season-title\">");
                html.Append(WebUtility.HtmlEncode(title));

                if (!string.IsNullOrWhiteSpace(details))
                {
                    html.Append("<div class=\"smartfilter-meta\">");
                    html.Append(WebUtility.HtmlEncode(details));
                    html.Append("</div>");
                }

                html.Append("</div></div></div>");
            }

            html.Append("</div>");
            return html.ToString();
        }

        private static JArray Flatten(JObject grouped)
        {
            var flattened = new JArray();

            foreach (var property in grouped.Properties())
            {
                if (property.Value is not JArray providerArray)
                    continue;

                foreach (var item in providerArray)
                    flattened.Add(item);
            }

            return flattened;
        }

        private static string BuildMovieHtml(JArray data, string title, string originalTitle)
        {
            var html = new StringBuilder();
            html.Append("<div class=\"videos__line\" data-smartfilter=\"true\">");
            bool first = true;
            string baseTitle = !string.IsNullOrWhiteSpace(title) ? title : originalTitle;

            foreach (var token in data.OfType<JObject>())
            {
                var serialized = SerializeMovieItem(token, baseTitle);
                if (serialized == null)
                    continue;

                string translate = token.Value<string>("translate") ??
                                   token.Value<string>("voice") ??
                                   token.Value<string>("voice_name") ??
                                   token.Value<string>("quality") ??
                                   "Оригинал";
                string provider = token.Value<string>("provider") ?? token.Value<string>("balanser");

                html.Append("<div class=\"videos__item videos__movie selector ");
                if (first)
                {
                    html.Append("focused ");
                    first = false;
                }

                html.Append("\" media=\"\"");
                html.Append(" data-folder=\"false\"");
                if (!string.IsNullOrWhiteSpace(provider))
                {
                    html.Append(" data-provider=\"");
                    html.Append(WebUtility.HtmlEncode(provider));
                    html.Append("\"");
                }
                html.Append(" data-json='");
                html.Append(WebUtility.HtmlEncode(serialized));
                html.Append("'><div class=\"videos__item-imgbox videos__movie-imgbox\"></div><div class=\"videos__item-title\">");
                html.Append(WebUtility.HtmlEncode(translate));
                html.Append("</div></div>");

                string quality = token.Value<string>("maxquality") ?? token.Value<string>("quality");
                if (!string.IsNullOrEmpty(quality))
                {
                    html.Append("<!--");
                    html.Append(WebUtility.HtmlEncode(quality));
                    html.Append("-->");
                }
            }

            html.Append("</div>");
            return html.ToString();
        }

        private static string SerializeMovieItem(JObject token, string baseTitle)
        {
            if (token == null)
                return null;

            if (!EnsureUrl(token, "link", "file", "stream", "src"))
                return null;

            EnsureMethod(token);
            EnsureType(token, "movie");
            EnsureStream(token);
            NormalizeHeaders(token);
            EnsureProvider(token);
            EnsureTranslate(token);
            EnsureMaxQuality(token);
            EnsureDetails(token);
            EnsureYear(token);
            EnsureTitle(token, baseTitle);

            return JsonConvert.SerializeObject(token, Formatting.None, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
        }

        private static string BuildSeasonHtml(JArray data, string maxQuality = null)
        {
            var html = new StringBuilder();
            html.Append("<div class=\"videos__line\" data-smartfilter=\"true\">");
            bool first = true;

            if (!string.IsNullOrWhiteSpace(maxQuality))
            {
                html.Append("<!--q:");
                html.Append(WebUtility.HtmlEncode(maxQuality));
                html.Append("-->");
            }

            foreach (var token in data.OfType<JObject>())
            {
                var type = token.Value<string>("type");
                if (!string.IsNullOrWhiteSpace(type) && !string.Equals(type, "season", StringComparison.OrdinalIgnoreCase))
                    continue;

                string url = token.Value<string>("url") ?? token.Value<string>("link");
                if (string.IsNullOrEmpty(url))
                    continue;

                string name = token.Value<string>("name") ?? token.Value<string>("title") ?? "Сезон";
                string provider = token.Value<string>("provider") ?? token.Value<string>("balanser");
                if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(provider))
                    name = provider;

                string metadata = BuildMetadata(token, includeProvider: true);

                var dataObj = new JObject
                {
                    ["method"] = "link",
                    ["url"] = url
                };

                string serialized = JsonConvert.SerializeObject(dataObj, Formatting.None);

                html.Append("<div class=\"videos__item videos__season selector ");
                if (first)
                {
                    html.Append("focused ");
                    first = false;
                }

                html.Append("\" data-folder=\"false\"");
                if (!string.IsNullOrWhiteSpace(provider))
                {
                    html.Append(" data-provider=\"");
                    html.Append(WebUtility.HtmlEncode(provider));
                    html.Append("\"");
                }
                html.Append(" data-json='");
                html.Append(WebUtility.HtmlEncode(serialized));
                html.Append("'><div class=\"videos__season-layers\"></div><div class=\"videos__item-imgbox videos__season-imgbox\"><div class=\"videos__item-title videos__season-title\">");
                html.Append(WebUtility.HtmlEncode(name));

                if (!string.IsNullOrEmpty(metadata))
                {
                    html.Append("<div class=\"smartfilter-meta\">");
                    html.Append(WebUtility.HtmlEncode(metadata));
                    html.Append("</div>");
                }

                html.Append("</div></div></div>");
            }

            html.Append("</div>");
            return html.ToString();
        }

        private static string BuildVoiceHtml(JArray voiceData)
        {
            if (voiceData == null || voiceData.Count == 0)
                return string.Empty;

            var html = new StringBuilder();
            html.Append("<div class=\"videos__line\" data-smartfilter=\"true\">");

            foreach (var token in voiceData.OfType<JObject>())
            {
                string url = token.Value<string>("url") ?? token.Value<string>("link");
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                string method = token.Value<string>("method") ?? "link";
                bool active = token.Value<bool?>("active") ?? false;
                string name = token.Value<string>("name") ?? token.Value<string>("title") ?? "Перевод";
                string details = token.Value<string>("details") ?? token.Value<string>("provider");

                var payload = new JObject
                {
                    ["method"] = method,
                    ["url"] = url
                };

                string serialized = JsonConvert.SerializeObject(payload, Formatting.None);

                html.Append("<div class=\"videos__button selector");
                if (active)
                    html.Append(" active");
                html.Append("\" data-json='");
                html.Append(WebUtility.HtmlEncode(serialized));
                html.Append("'>");
                html.Append(WebUtility.HtmlEncode(name));

                if (!string.IsNullOrWhiteSpace(details))
                {
                    html.Append("<div class=\"smartfilter-meta\">");
                    html.Append(WebUtility.HtmlEncode(details));
                    html.Append("</div>");
                }

                html.Append("</div>");
            }

            html.Append("</div>");
            return html.ToString();
        }

        private static string BuildGroupedSeasonHtml(JObject groupedData, string maxQuality)
        {
            var html = new StringBuilder();
            html.Append("<div class=\"videos__line\" data-smartfilter=\"true\">");
            bool firstProvider = true;

            if (!string.IsNullOrWhiteSpace(maxQuality))
            {
                html.Append("<!--q:");
                html.Append(WebUtility.HtmlEncode(maxQuality));
                html.Append("-->");
            }

            foreach (var property in groupedData.Properties().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (property.Value is not JArray seasons || seasons.Count == 0)
                    continue;

                string providerName = property.Name;
                if (string.Equals(providerName, "default", StringComparison.OrdinalIgnoreCase))
                    providerName = "Каталог";
                bool expand = firstProvider;

                var folderPayload = new JObject
                {
                    ["method"] = "folder",
                    ["provider"] = providerName,
                    ["count"] = seasons.Count
                };

                string folderJson = JsonConvert.SerializeObject(folderPayload, Formatting.None);

                html.Append("<div class=\"videos__item videos__season selector");
                if (firstProvider)
                {
                    html.Append(" focused");
                }

                html.Append("\" data-folder=\"true\" data-provider=\"");
                html.Append(WebUtility.HtmlEncode(providerName));
                html.Append("\" data-expanded=\"");
                html.Append(expand ? "true" : "false");
                html.Append("\" data-json='");
                html.Append(WebUtility.HtmlEncode(folderJson));
                html.Append("'><div class=\"videos__season-layers\"></div><div class=\"videos__item-imgbox videos__season-imgbox\"><div class=\"videos__item-title videos__season-title\">");
                html.Append(WebUtility.HtmlEncode($"{providerName} ({seasons.Count})"));
                html.Append("</div></div></div>");

                foreach (var token in seasons.OfType<JObject>())
                {
                    string url = token.Value<string>("url") ?? token.Value<string>("link");
                    if (string.IsNullOrEmpty(url))
                        continue;

                    string name = token.Value<string>("name") ?? token.Value<string>("title") ?? "Сезон";
                    if (string.IsNullOrWhiteSpace(name))
                        name = providerName;

                    string metadata = BuildMetadata(token, includeProvider: true);

                    var dataObj = new JObject
                    {
                        ["method"] = "link",
                        ["url"] = url,
                        ["provider"] = providerName
                    };

                    string serialized = JsonConvert.SerializeObject(dataObj, Formatting.None);

                    html.Append("<div class=\"videos__item videos__season selector\" data-folder=\"false\" data-provider=\"");
                    html.Append(WebUtility.HtmlEncode(providerName));
                    html.Append("\" data-json='");
                    html.Append(WebUtility.HtmlEncode(serialized));
                    html.Append("'");
                    if (!expand)
                        html.Append(" style=\"display:none;\"");
                    html.Append("><div class=\"videos__season-layers\"></div><div class=\"videos__item-imgbox videos__season-imgbox\"><div class=\"videos__item-title videos__season-title\">");
                    html.Append(WebUtility.HtmlEncode(name));

                    if (!string.IsNullOrEmpty(metadata))
                    {
                        html.Append("<div class=\"smartfilter-meta\">");
                        html.Append(WebUtility.HtmlEncode(metadata));
                        html.Append("</div>");
                    }

                    html.Append("</div></div></div>");
                }

                firstProvider = false;
            }

            html.Append("</div>");
            return html.ToString();
        }

        private static string BuildEpisodeHtml(JArray data)
        {
            var html = new StringBuilder();
            html.Append("<div class=\"videos__line\" data-smartfilter=\"true\">");
            bool first = true;

            foreach (var token in data.OfType<JObject>())
            {
                var type = token.Value<string>("type");
                if (!string.IsNullOrWhiteSpace(type) && !string.Equals(type, "episode", StringComparison.OrdinalIgnoreCase))
                    continue;

                var serialized = SerializeEpisodeItem(token);
                if (serialized == null)
                    continue;

                int? season = TryParseInt(token["season"]) ?? TryParseInt(token["s"]);
                int? episode = TryParseInt(token["episode"]) ?? TryParseInt(token["e"]);
                string provider = token.Value<string>("provider") ?? token.Value<string>("balanser");

                html.Append("<div class=\"videos__item videos__movie selector ");
                if (first)
                {
                    html.Append("focused ");
                    first = false;
                }

                html.Append("\" media=\"\"");

                if (season.HasValue)
                    html.Append(" s=\"" + season.Value + "\"");
                if (episode.HasValue)
                    html.Append(" e=\"" + episode.Value + "\"");

                html.Append(" data-folder=\"false\"");
                if (!string.IsNullOrWhiteSpace(provider))
                {
                    html.Append(" data-provider=\"");
                    html.Append(WebUtility.HtmlEncode(provider));
                    html.Append("\"");
                }

                html.Append(" data-json='");
                html.Append(WebUtility.HtmlEncode(serialized));
                html.Append("'><div class=\"videos__item-imgbox videos__movie-imgbox\"></div><div class=\"videos__item-title\">");

                string title = token.Value<string>("name") ?? token.Value<string>("title") ?? "Серия";
                html.Append(WebUtility.HtmlEncode(title));

                string metadata = BuildMetadata(token, includeProvider: true);
                if (!string.IsNullOrEmpty(metadata))
                {
                    html.Append("<div class=\"smartfilter-meta\">");
                    html.Append(WebUtility.HtmlEncode(metadata));
                    html.Append("</div>");
                }

                html.Append("</div></div>");
            }

            html.Append("</div>");
            return html.ToString();
        }

        private static string SerializeEpisodeItem(JObject token)
        {
            if (token == null)
                return null;

            if (!EnsureUrl(token, "link", "stream", "file"))
                return null;

            EnsureMethod(token);
            EnsureType(token, "episode");
            EnsureStream(token);
            NormalizeHeaders(token);
            EnsureProvider(token);
            EnsureTranslate(token);
            EnsureMaxQuality(token);
            EnsureDetails(token);

            if (string.IsNullOrWhiteSpace(token.Value<string>("title")))
            {
                var title = token.Value<string>("name");
                if (!string.IsNullOrWhiteSpace(title))
                    token["title"] = title;
            }

            EnsureYear(token);
            EnsureIntProperty(token, "season", "s");
            EnsureIntProperty(token, "episode", "e");

            if (token.TryGetValue("vast", out var vast) && vast.Type == JTokenType.String && string.IsNullOrWhiteSpace(vast.ToString()))
                token.Remove("vast");

            return JsonConvert.SerializeObject(token, Formatting.None, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
        }

        private static string BuildMetadata(JObject token, bool includeProvider)
        {
            if (token == null)
                return null;

            var parts = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddPart(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return;

                value = value.Trim();
                if (value.Length == 0)
                    return;

                if (seen.Add(value))
                    parts.Add(value);
            }

            AddPart(token.Value<string>("translate") ?? token.Value<string>("voice") ?? token.Value<string>("voice_name"));
            AddPart(token.Value<string>("maxquality") ?? token.Value<string>("quality"));

            if (includeProvider)
                AddPart(token.Value<string>("provider") ?? token.Value<string>("balanser") ?? token.Value<string>("details"));

            return parts.Count == 0 ? null : string.Join(" • ", parts);
        }

        private static void EnsureMethod(JObject obj)
        {
            var method = obj.Value<string>("method");
            if (string.IsNullOrWhiteSpace(method))
                obj["method"] = "play";
        }

        private static void EnsureType(JObject obj, string type)
        {
            if (obj == null || string.IsNullOrWhiteSpace(type))
                return;

            var existing = obj.Value<string>("type");
            if (!string.IsNullOrWhiteSpace(existing))
                return;

            obj["type"] = type;
        }

        private static bool EnsureUrl(JObject obj, params string[] fallbackKeys)
        {
            var url = obj.Value<string>("url");
            if (!string.IsNullOrWhiteSpace(url))
                return true;

            foreach (var key in fallbackKeys ?? Array.Empty<string>())
            {
                var value = obj.Value<string>(key);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    obj["url"] = value;
                    return true;
                }
            }

            return false;
        }

        private static void EnsureStream(JObject obj)
        {
            if (obj.TryGetValue("stream", out var stream))
            {
                if (stream.Type == JTokenType.Null || (stream.Type == JTokenType.String && string.IsNullOrWhiteSpace(stream.ToString())))
                    obj.Remove("stream");
            }

            if (!obj.ContainsKey("stream"))
            {
                var streamLink = obj.Value<string>("streamlink") ?? obj.Value<string>("file");
                if (!string.IsNullOrWhiteSpace(streamLink))
                    obj["stream"] = streamLink;
            }
        }

        private static void NormalizeHeaders(JObject obj)
        {
            if (!obj.TryGetValue("headers", out var headers) || headers == null)
                return;

            if (headers is JObject headerObj)
            {
                if (!headerObj.Properties().Any())
                    obj.Remove("headers");
                return;
            }

            if (headers is JArray array)
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in array.OfType<JObject>())
                {
                    var name = entry.Value<string>("name");
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var value = entry.Value<string>("value") ?? entry.Value<string>("val") ?? string.Empty;
                    dict[name] = value;
                }

                if (dict.Count > 0)
                    obj["headers"] = JObject.FromObject(dict);
                else
                    obj.Remove("headers");
            }
            else if (headers.Type == JTokenType.String && string.IsNullOrWhiteSpace(headers.ToString()))
            {
                obj.Remove("headers");
            }
        }

        private static void EnsureProvider(JObject obj)
        {
            if (obj == null)
                return;

            var provider = obj.Value<string>("provider");
            if (!string.IsNullOrWhiteSpace(provider))
                return;

            provider = obj.Value<string>("balanser") ?? obj.Value<string>("details");
            if (!string.IsNullOrWhiteSpace(provider))
                obj["provider"] = provider;
        }

        private static void EnsureTranslate(JObject obj)
        {
            var translate = obj.Value<string>("translate");
            if (!string.IsNullOrWhiteSpace(translate))
                return;

            translate = obj.Value<string>("voice");
            if (!string.IsNullOrWhiteSpace(translate))
            {
                obj["translate"] = translate;
                return;
            }

            translate = obj.Value<string>("voice_name");
            if (!string.IsNullOrWhiteSpace(translate))
                obj["translate"] = translate;
        }

        private static void EnsureMaxQuality(JObject obj)
        {
            var maxQuality = obj.Value<string>("maxquality");
            if (!string.IsNullOrWhiteSpace(maxQuality))
                return;

            var qualityToken = obj["quality"];
            if (qualityToken == null)
                return;

            if (qualityToken.Type == JTokenType.String)
            {
                var quality = qualityToken.ToString();
                if (!string.IsNullOrWhiteSpace(quality))
                    obj["maxquality"] = quality;
            }
            else if (qualityToken is JArray qualityArray && qualityArray.Count > 0)
            {
                var quality = qualityArray.First!.ToString();
                if (!string.IsNullOrWhiteSpace(quality))
                    obj["maxquality"] = quality;
            }
        }

        private static void EnsureDetails(JObject obj)
        {
            var details = obj.Value<string>("details");
            if (!string.IsNullOrWhiteSpace(details))
                return;

            var provider = obj.Value<string>("provider") ?? obj.Value<string>("balanser");
            if (!string.IsNullOrWhiteSpace(provider))
                obj["details"] = provider;
        }

        private static void EnsureYear(JObject obj)
        {
            if (!obj.TryGetValue("year", out var yearToken) || yearToken == null)
                return;

            if (yearToken.Type == JTokenType.Integer)
                return;

            if (int.TryParse(yearToken.ToString(), out int parsed))
                obj["year"] = parsed;
            else
                obj.Remove("year");
        }

        private static void EnsureTitle(JObject obj, string baseTitle)
        {
            var title = obj.Value<string>("title");
            if (!string.IsNullOrWhiteSpace(title))
                return;

            var translate = obj.Value<string>("translate");
            if (!string.IsNullOrWhiteSpace(baseTitle))
            {
                obj["title"] = string.IsNullOrWhiteSpace(translate)
                    ? baseTitle
                    : $"{baseTitle} ({translate})";
                return;
            }

            if (!string.IsNullOrWhiteSpace(translate))
                obj["title"] = translate;
        }

        private static void EnsureIntProperty(JObject obj, string key, params string[] aliases)
        {
            if (TrySetInt(obj, key, obj[key]))
                return;

            foreach (var alias in aliases ?? Array.Empty<string>())
            {
                if (TrySetInt(obj, key, obj[alias]))
                    return;
            }
        }

        private static bool TrySetInt(JObject obj, string key, JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return false;

            if (token.Type == JTokenType.Integer)
            {
                obj[key] = token.Value<int>();
                return true;
            }

            if (int.TryParse(token.ToString(), out int parsed))
            {
                obj[key] = parsed;
                return true;
            }

            return false;
        }

        private static int? TryParseInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            if (token.Type == JTokenType.Integer)
                return token.Value<int>();

            if (int.TryParse(token.ToString(), out int value))
                return value;

            return null;
        }
    }
}
