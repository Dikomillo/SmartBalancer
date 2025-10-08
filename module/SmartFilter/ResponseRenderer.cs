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
        public static string BuildHtml(string type, JArray data, string title, string originalTitle)
        {
            if (data == null || data.Count == 0)
                return string.Empty;

            return type switch
            {
                "season" => BuildSeasonHtml(data),
                "episode" => BuildEpisodeHtml(data),
                _ => BuildMovieHtml(data, title, originalTitle)
            };
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

                html.Append("<div class=\"videos__item videos__movie selector ");
                if (first)
                {
                    html.Append("focused ");
                    first = false;
                }

                html.Append("\" media=\"\" data-json='");
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
            EnsureStream(token);
            NormalizeHeaders(token);
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

        private static string BuildSeasonHtml(JArray data)
        {
            var html = new StringBuilder();
            html.Append("<div class=\"videos__line\" data-smartfilter=\"true\">");
            bool first = true;

            foreach (var token in data.OfType<JObject>())
            {
                string url = token.Value<string>("url") ?? token.Value<string>("link");
                if (string.IsNullOrEmpty(url))
                    continue;

                string name = token.Value<string>("name") ?? token.Value<string>("title") ?? "Сезон";
                string provider = token.Value<string>("provider") ?? token.Value<string>("balanser");

                if (!string.IsNullOrWhiteSpace(provider) && !name.Contains(provider, StringComparison.OrdinalIgnoreCase))
                    name = $"{provider} - {name}";

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

                html.Append("\" data-json='");
                html.Append(WebUtility.HtmlEncode(serialized));
                html.Append("'><div class=\"videos__season-layers\"></div><div class=\"videos__item-imgbox videos__season-imgbox\"><div class=\"videos__item-title videos__season-title\">");
                html.Append(WebUtility.HtmlEncode(name));
                html.Append("</div></div></div>");
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
                var serialized = SerializeEpisodeItem(token);
                if (serialized == null)
                    continue;

                int? season = TryParseInt(token["season"]) ?? TryParseInt(token["s"]);
                int? episode = TryParseInt(token["episode"]) ?? TryParseInt(token["e"]);

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

                html.Append(" data-json='");
                html.Append(WebUtility.HtmlEncode(serialized));
                html.Append("'><div class=\"videos__item-imgbox videos__movie-imgbox\"></div><div class=\"videos__item-title\">");
                html.Append(WebUtility.HtmlEncode(token.Value<string>("name") ?? token.Value<string>("title") ?? "Серия"));
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
            EnsureStream(token);
            NormalizeHeaders(token);
            EnsureTranslate(token);

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

        private static void EnsureMethod(JObject obj)
        {
            var method = obj.Value<string>("method");
            if (string.IsNullOrWhiteSpace(method))
                obj["method"] = "play";
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
