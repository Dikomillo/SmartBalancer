using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
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
            string url = token.Value<string>("url") ?? token.Value<string>("link");
            if (string.IsNullOrEmpty(url))
                return null;

            var obj = new JObject
            {
                ["method"] = token.Value<string>("method") ?? "play",
                ["url"] = url
            };

            CopyIfPresent(obj, token, "stream", "stream", "streamlink");
            CopyIfPresent(obj, token, "headers");
            CopyIfPresent(obj, token, "quality");
            CopyIfPresent(obj, token, "subtitles");

            string translate = token.Value<string>("translate") ?? token.Value<string>("voice") ?? token.Value<string>("voice_name");
            if (!string.IsNullOrWhiteSpace(translate))
                obj["translate"] = translate;

            string maxQuality = token.Value<string>("maxquality") ?? token.Value<string>("quality");
            if (!string.IsNullOrWhiteSpace(maxQuality))
                obj["maxquality"] = maxQuality;

            string details = token.Value<string>("details") ?? token.Value<string>("provider");
            if (!string.IsNullOrWhiteSpace(details))
                obj["details"] = details;

            string vast = token.Value<string>("vast");
            if (!string.IsNullOrWhiteSpace(vast))
                obj["vast"] = vast;

            if (token.TryGetValue("hls_manifest_timeout", out var hls))
                obj["hls_manifest_timeout"] = hls;

            if (token.TryGetValue("headers", out var headersToken) && headersToken is JArray headersArray)
                obj["headers"] = new JObject(headersArray.OfType<JObject>().Where(o => o["name"] != null && o["value"] != null).ToDictionary(o => o.Value<string>("name"), o => (JToken)o.Value<string>("value")));

            if (token.TryGetValue("year", out var yearToken))
            {
                if (yearToken.Type == JTokenType.Integer)
                    obj["year"] = yearToken;
                else if (int.TryParse(yearToken.ToString(), out int parsedYear))
                    obj["year"] = parsedYear;
            }

            string itemTitle = token.Value<string>("title");
            if (string.IsNullOrWhiteSpace(itemTitle))
                itemTitle = baseTitle;

            if (!string.IsNullOrWhiteSpace(itemTitle))
                obj["title"] = itemTitle;

            return JsonConvert.SerializeObject(obj, Formatting.None, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
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
                string url = token.Value<string>("url") ?? token.Value<string>("link");
                if (string.IsNullOrEmpty(url))
                    continue;

                var obj = new JObject
                {
                    ["method"] = token.Value<string>("method") ?? "play",
                    ["url"] = url
                };

                CopyIfPresent(obj, token, "stream", "stream", "streamlink");
                CopyIfPresent(obj, token, "headers");
                CopyIfPresent(obj, token, "quality");
                CopyIfPresent(obj, token, "subtitles");

                if (token.TryGetValue("hls_manifest_timeout", out var hls))
                    obj["hls_manifest_timeout"] = hls;

                if (token.TryGetValue("vast", out var vast))
                    obj["vast"] = vast;

                string title = token.Value<string>("title") ?? token.Value<string>("name");
                if (!string.IsNullOrWhiteSpace(title))
                    obj["title"] = title;

                if (token.TryGetValue("translate", out var translate))
                    obj["translate"] = translate;

                if (token.TryGetValue("voice", out var voice))
                    obj["voice"] = voice;

                int? season = TryParseInt(token["season"]) ?? TryParseInt(token["s"]);
                int? episode = TryParseInt(token["episode"]) ?? TryParseInt(token["e"]);

                var serialized = JsonConvert.SerializeObject(obj, Formatting.None, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

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

        private static void CopyIfPresent(JObject target, JObject source, string targetKey, params string[] sourceKeys)
        {
            if (sourceKeys == null || sourceKeys.Length == 0)
                sourceKeys = new[] { targetKey };

            foreach (var key in sourceKeys)
            {
                if (!source.TryGetValue(key, out var value) || value == null || value.Type == JTokenType.Null)
                    continue;

                target[targetKey] = value.Type switch
                {
                    JTokenType.Object => value.DeepClone(),
                    JTokenType.Array => value.DeepClone(),
                    _ => JToken.FromObject(value.ToObject<object>())
                };
                return;
            }
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
