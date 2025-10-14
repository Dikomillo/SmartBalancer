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
        public static string BuildHtml(
            string type,
            JObject payload,
            string title,
            string originalTitle,
            string host = null,
            IReadOnlyDictionary<string, string> query = null)
        {
            payload ??= new JObject();
            var items = payload.Value<JArray>("items") ?? new JArray();
            var voices = payload.Value<JArray>("voice");
            string maxQuality = payload.Value<string>("maxquality");

            var builder = new StringBuilder();

            if (voices != null && voices.Count > 0)
                builder.Append(BuildVoiceHtml(voices));

            if (items.Count == 0)
                return builder.ToString();

            type = string.IsNullOrWhiteSpace(type) ? "movie" : type.ToLowerInvariant();

            switch (type)
            {
                case "season":
                    builder.Append(BuildSeasonHtml(items));
                    break;
                case "episode":
                    builder.Append(BuildEpisodeHtml(items));
                    break;
                case "similar":
                    builder.Append(BuildSimilarHtml(items));
                    break;
                default:
                    builder.Append(BuildMovieHtml(items, string.IsNullOrWhiteSpace(title) ? originalTitle : title, maxQuality));
                    break;
            }

            return builder.ToString();
        }

        private static string BuildVoiceHtml(JArray voices)
        {
            var html = new StringBuilder();
            html.Append("<div class=\"videos__line videos__line--voice\" data-smartfilter=\"true\">");
            bool first = true;

            foreach (var token in voices.OfType<JObject>())
            {
                string name = token.Value<string>("name") ?? token.Value<string>("title");
                string link = token.Value<string>("link");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(link))
                    continue;

                bool active = token.Value<bool?>("active") == true || token.Value<bool?>("selected") == true;
                string provider = token.Value<string>("provider");

                var payload = new JObject
                {
                    ["method"] = "link",
                    ["url"] = link
                };

                if (!string.IsNullOrWhiteSpace(provider))
                    payload["provider"] = provider;

                string serialized = JsonConvert.SerializeObject(payload, Formatting.None);

                html.Append("<div class=\"videos__item videos__season selector ");
                if (first || active)
                {
                    html.Append("focused ");
                    first = false;
                }
                html.Append("\" data-json='");
                html.Append(WebUtility.HtmlEncode(serialized));
                html.Append("'><div class=\"videos__season-layers\"></div><div class=\"videos__item-imgbox videos__season-imgbox\"><div class=\"videos__item-title videos__season-title\">");
                html.Append(WebUtility.HtmlEncode(name));

                if (!string.IsNullOrWhiteSpace(provider))
                {
                    html.Append("<div class=\"smartfilter-meta\">");
                    html.Append(WebUtility.HtmlEncode(provider));
                    html.Append("</div>");
                }

                html.Append("</div></div></div>");
            }

            html.Append("</div>");
            return html.ToString();
        }

        private static string BuildSeasonHtml(JArray items)
        {
            var html = new StringBuilder();
            html.Append("<div class=\"videos__line\" data-smartfilter=\"true\">");
            bool first = true;

            foreach (var token in items.OfType<JObject>())
            {
                string name = token.Value<string>("name") ?? token.Value<string>("title");
                string link = token.Value<string>("link");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(link))
                    continue;

                string provider = token.Value<string>("provider");

                var payload = new JObject
                {
                    ["method"] = "link",
                    ["url"] = link
                };

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
                html.Append(WebUtility.HtmlEncode(name));

                if (!string.IsNullOrWhiteSpace(provider))
                {
                    html.Append("<div class=\"smartfilter-meta\">");
                    html.Append(WebUtility.HtmlEncode(provider));
                    html.Append("</div>");
                }

                html.Append("</div></div></div>");
            }

            html.Append("</div>");
            return html.ToString();
        }

        private static string BuildEpisodeHtml(JArray items)
        {
            var html = new StringBuilder();
            html.Append("<div class=\"videos__line\" data-smartfilter=\"true\">");
            bool first = true;

            foreach (var token in items.OfType<JObject>())
            {
                string name = token.Value<string>("name") ?? token.Value<string>("title");
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                string stream = token.Value<string>("stream") ?? token.Value<string>("url");
                string link = token.Value<string>("link");
                string method = token.Value<string>("method");
                string provider = token.Value<string>("provider");

                var payload = new JObject();
                if (!string.IsNullOrWhiteSpace(method))
                    payload["method"] = method;

                string url = !string.IsNullOrWhiteSpace(stream) ? stream : link;
                if (!string.IsNullOrWhiteSpace(url))
                    payload["url"] = url;

                if (payload.Count == 0)
                    continue;

                if (string.IsNullOrWhiteSpace(method))
                    payload["method"] = string.IsNullOrWhiteSpace(stream) ? "link" : "call";

                if (!string.IsNullOrWhiteSpace(provider))
                    payload["provider"] = provider;

                string serialized = JsonConvert.SerializeObject(payload, Formatting.None);

                html.Append("<div class=\"videos__item selector ");
                if (first)
                {
                    html.Append("focused ");
                    first = false;
                }
                html.Append("\" data-json='");
                html.Append(WebUtility.HtmlEncode(serialized));
                html.Append("'><div class=\"videos__item-imgbox\"><div class=\"videos__item-title\">");
                html.Append(WebUtility.HtmlEncode(name));

                string quality = token.Value<string>("quality") ?? token.Value<string>("maxquality");
                if (!string.IsNullOrWhiteSpace(quality) || !string.IsNullOrWhiteSpace(provider))
                {
                    html.Append("<div class=\"smartfilter-meta\">");
                    if (!string.IsNullOrWhiteSpace(quality))
                        html.Append(WebUtility.HtmlEncode(quality));
                    if (!string.IsNullOrWhiteSpace(provider))
                    {
                        if (!string.IsNullOrWhiteSpace(quality))
                            html.Append(" · ");
                        html.Append(WebUtility.HtmlEncode(provider));
                    }
                    html.Append("</div>");
                }

                html.Append("</div></div></div>");
            }

            html.Append("</div>");
            return html.ToString();
        }

        private static string BuildSimilarHtml(JArray items)
        {
            var html = new StringBuilder();
            html.Append("<div class=\"videos__line\" data-smartfilter=\"true\">");
            bool first = true;

            foreach (var token in items.OfType<JObject>())
            {
                string link = token.Value<string>("link") ?? token.Value<string>("url");
                if (string.IsNullOrWhiteSpace(link))
                    continue;

                string name = token.Value<string>("title") ?? token.Value<string>("name") ?? "Источник";
                string provider = token.Value<string>("provider");

                var payload = new JObject
                {
                    ["method"] = token.Value<string>("method") ?? "link",
                    ["url"] = link
                };

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
                html.Append(WebUtility.HtmlEncode(name));

                if (!string.IsNullOrWhiteSpace(provider))
                {
                    html.Append("<div class=\"smartfilter-meta\">");
                    html.Append(WebUtility.HtmlEncode(provider));
                    html.Append("</div>");
                }

                html.Append("</div></div></div>");
            }

            html.Append("</div>");
            return html.ToString();
        }

        private static string BuildMovieHtml(JArray items, string caption, string maxQuality)
        {
            var html = new StringBuilder();
            html.Append("<div class=\"videos__line\" data-smartfilter=\"true\">");
            bool first = true;

            foreach (var token in items.OfType<JObject>())
            {
                string link = token.Value<string>("link") ?? token.Value<string>("url");
                string stream = token.Value<string>("stream");
                string method = token.Value<string>("method");

                if (string.IsNullOrWhiteSpace(link) && string.IsNullOrWhiteSpace(stream))
                    continue;

                string title = token.Value<string>("title") ?? token.Value<string>("name") ?? caption ?? "Источник";
                string provider = token.Value<string>("provider");
                string quality = token.Value<string>("quality") ?? token.Value<string>("maxquality") ?? maxQuality;

                var payload = new JObject();

                if (!string.IsNullOrWhiteSpace(method))
                    payload["method"] = method;

                if (!string.IsNullOrWhiteSpace(stream))
                {
                    payload["url"] = stream;
                    if (string.IsNullOrWhiteSpace(method))
                        payload["method"] = "call";
                }
                else
                {
                    payload["url"] = link;
                    if (string.IsNullOrWhiteSpace(method))
                        payload["method"] = "link";
                }

                if (!string.IsNullOrWhiteSpace(provider))
                    payload["provider"] = provider;

                string serialized = JsonConvert.SerializeObject(payload, Formatting.None);

                html.Append("<div class=\"videos__item selector ");
                if (first)
                {
                    html.Append("focused ");
                    first = false;
                }
                html.Append("\" data-json='");
                html.Append(WebUtility.HtmlEncode(serialized));
                html.Append("'><div class=\"videos__item-imgbox\"><div class=\"videos__item-title\">");
                html.Append(WebUtility.HtmlEncode(title));

                if (!string.IsNullOrWhiteSpace(quality) || !string.IsNullOrWhiteSpace(provider))
                {
                    html.Append("<div class=\"smartfilter-meta\">");
                    if (!string.IsNullOrWhiteSpace(quality))
                        html.Append(WebUtility.HtmlEncode(quality));
                    if (!string.IsNullOrWhiteSpace(provider))
                    {
                        if (!string.IsNullOrWhiteSpace(quality))
                            html.Append(" · ");
                        html.Append(WebUtility.HtmlEncode(provider));
                    }
                    html.Append("</div>");
                }

                html.Append("</div></div></div>");
            }

            html.Append("</div>");
            return html.ToString();
        }
    }
}
