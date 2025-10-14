using Microsoft.AspNetCore.WebUtilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared.Models.Templates;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartFilter
{
    internal static class ResponseRenderer
    {
        internal sealed class MovieRenderPayload
        {
            public MovieRenderPayload(JArray items, string filtersHtml, bool hasActiveFilters)
            {
                Items = items ?? new JArray();
                FiltersHtml = string.IsNullOrEmpty(filtersHtml) ? null : filtersHtml;
                HasActiveFilters = hasActiveFilters;
            }

            public JArray Items { get; }
            public string FiltersHtml { get; }
            public bool HasActiveFilters { get; }
        }

        public static string BuildHtml(
            string type,
            JToken data,
            string title,
            string originalTitle,
            string host = null,
            IReadOnlyDictionary<string, string> query = null,
            MovieRenderPayload moviePayload = null)
        {
            if (data == null)
                return string.Empty;

            bool isSeason = string.Equals(type, "season", StringComparison.OrdinalIgnoreCase);
            bool isEpisode = string.Equals(type, "episode", StringComparison.OrdinalIgnoreCase);

            string voiceHtml = null;
            string maxQuality = null;

            if (isSeason || isEpisode)
            {
                var seriesPayload = SeriesDataHelper.Extract(type, data);
                data = seriesPayload.Items;
                maxQuality = seriesPayload.MaxQuality;

                if (seriesPayload.Voice != null && seriesPayload.Voice.Count > 0)
                    voiceHtml = BuildVoiceHtml(seriesPayload.Voice);
            }

            if (!isSeason && !isEpisode)
            {
                moviePayload ??= PrepareMoviePayload(data, query, host);

                if (moviePayload != null)
                {
                    data = moviePayload.Items ?? data;

                    if (!string.IsNullOrEmpty(moviePayload.FiltersHtml))
                    {
                        voiceHtml = string.IsNullOrEmpty(voiceHtml)
                            ? moviePayload.FiltersHtml
                            : string.Concat(voiceHtml, moviePayload.FiltersHtml);
                    }
                }
            }

            if (data is JObject grouped)
            {
                if (!grouped.Properties().Any())
                    return voiceHtml ?? string.Empty;

                data = Flatten(grouped);
            }

            if (data is not JArray array || array.Count == 0)
                return voiceHtml ?? string.Empty;

            if (isSeason)
                array = SortSeasons(array);

            string content = type switch
            {
                "similar" => BuildSimilarHtml(array),
                "season" => BuildSeasonHtml(array, maxQuality),
                "episode" => BuildEpisodeHtml(array),
                _ => BuildMovieHtml(array, title, originalTitle)
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

        private static readonly string[] MovieVoiceKeys =
        {
            "smartfilterVoice",
            "translation_key",
            "translationKey",
            "translation_id",
            "translationId",
            "voice_id",
            "voiceId",
            "voice",
            "voice_name",
            "voiceName",
            "translate",
            "translation",
            "dub"
        };

        private static readonly string[] MovieQualityKeys =
        {
            "smartfilterQuality",
            "maxquality",
            "maxQuality",
            "quality",
            "quality_label",
            "qualityName",
            "video_quality",
            "source_quality",
            "hd"
        };

        private static readonly string[] MovieVoiceQueryKeys =
        {
            "voice",
            "translate",
            "translation",
            "t",
            "translation_id",
            "translationId",
            "translation_key",
            "translationKey",
            "voice_id",
            "voiceId"
        };

        private static readonly string[] MovieQualityQueryKeys =
        {
            "quality",
            "maxquality",
            "maxQuality",
            "q"
        };

        public static MovieRenderPayload PrepareMoviePayload(JToken data, IReadOnlyDictionary<string, string> query, string host)
        {
            var items = ExtractMovieArray(data) ?? new JArray();
            var filters = MovieFilterCriteria.From(query);

            var voiceOptions = CollectVoiceOptions(items);
            var qualityOptions = CollectQualityOptions(items);

            string voiceFilterHtml = BuildMovieVoiceHtml(voiceOptions, filters, query, host);
            string qualityFilterHtml = BuildMovieQualityHtml(qualityOptions, filters, query, host);

            var filteredItems = ApplyMovieFilters(items, filters);

            return new MovieRenderPayload(filteredItems, CombineFilterHtml(voiceFilterHtml, qualityFilterHtml), filters?.HasFilters ?? false);
        }

        private static JArray ExtractMovieArray(JToken data)
        {
            if (data is JArray array)
                return array;

            if (data is JObject obj)
            {
                foreach (var key in new[] { "data", "results", "items" })
                {
                    if (obj.TryGetValue(key, out var token) && token is JArray nested)
                        return nested;
                }
            }

            return null;
        }

        private static string CombineFilterHtml(string first, string second)
        {
            if (string.IsNullOrEmpty(first))
                return string.IsNullOrEmpty(second) ? null : second;

            if (string.IsNullOrEmpty(second))
                return first;

            return first + second;
        }

        private static string BuildMovieVoiceHtml(List<MovieVoiceOption> options, MovieFilterCriteria filters, IReadOnlyDictionary<string, string> query, string host)
        {
            if (options == null || options.Count == 0)
                return null;

            var tpl = new VoiceTpl(options.Count + 1);

            string resetLink = BuildMovieFilterLink(query, host, null, filters?.Quality);
            tpl.Append("Все озвучки", filters == null || string.IsNullOrEmpty(filters.Voice), resetLink);

            foreach (var option in options)
            {
                string value = string.IsNullOrWhiteSpace(option.Value) ? option.Label : option.Value;
                string link = BuildMovieFilterLink(query, host, value, filters?.Quality);
                bool active = filters?.IsVoiceActive(option) ?? false;
                tpl.Append(option.Label, active, link);
            }

            return tpl.ToHtml();
        }

        private static string BuildMovieQualityHtml(List<MovieQualityOption> options, MovieFilterCriteria filters, IReadOnlyDictionary<string, string> query, string host)
        {
            if (options == null || options.Count == 0)
                return null;

            var tpl = new VoiceTpl(options.Count + 1);

            string resetLink = BuildMovieFilterLink(query, host, filters?.Voice, null);
            tpl.Append("Все качества", filters == null || string.IsNullOrEmpty(filters.Quality), resetLink);

            foreach (var option in options)
            {
                string link = BuildMovieFilterLink(query, host, filters?.Voice, option.Value);
                bool active = filters?.IsQualityActive(option) ?? false;
                tpl.Append(option.Value, active, link);
            }

            return tpl.ToHtml();
        }

        private static string BuildMovieFilterLink(IReadOnlyDictionary<string, string> baseQuery, string host, string voice, string quality)
        {
            var query = new Dictionary<string, string>(baseQuery ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);

            ApplyQueryValue(query, voice, MovieVoiceQueryKeys);
            ApplyQueryValue(query, quality, MovieQualityQueryKeys);

            string baseUrl = string.IsNullOrWhiteSpace(host) ? "/lite/smartfilter" : $"{host.TrimEnd('/')}/lite/smartfilter";

            return QueryHelpers.AddQueryString(baseUrl, query);
        }

        private static void ApplyQueryValue(Dictionary<string, string> query, string value, params string[] keys)
        {
            foreach (var key in keys ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(value))
                    query.Remove(key);
                else
                    query[key] = value.Trim();
            }
        }

        private static List<MovieVoiceOption> CollectVoiceOptions(JArray items)
        {
            var result = new Dictionary<string, MovieVoiceOption>(StringComparer.OrdinalIgnoreCase);

            if (items != null)
            {
                foreach (var obj in items.OfType<JObject>())
                {
                    string label = ExtractVoiceLabel(obj);
                    string provider = obj.Value<string>("provider") ?? obj.Value<string>("balanser");

                    if (string.IsNullOrWhiteSpace(label))
                    {
                        if (string.IsNullOrWhiteSpace(provider))
                            continue;

                        label = provider;
                    }

                    string value = ExtractVoiceValue(obj);
                    if (string.IsNullOrWhiteSpace(value))
                        value = label;

                    string key = string.IsNullOrWhiteSpace(value) ? NormalizeVoiceKey(label) : value;

                    if (!result.TryGetValue(key, out var option))
                    {
                        option = new MovieVoiceOption(label, value);
                        result[key] = option;
                    }

                    if (!string.IsNullOrWhiteSpace(provider))
                        option.Providers.Add(provider);
                }
            }

            EnsureUniqueVoiceLabels(result.Values);

            return result.Values
                .OrderBy(o => o.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static void EnsureUniqueVoiceLabels(IEnumerable<MovieVoiceOption> options)
        {
            if (options == null)
                return;

            var duplicates = options
                .Where(o => !string.IsNullOrWhiteSpace(o.Label))
                .GroupBy(o => o.NormalizedLabel, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var group in duplicates)
            {
                foreach (var option in group)
                {
                    string provider = option.Providers.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                    if (!string.IsNullOrWhiteSpace(provider))
                        option.AppendProvider(provider);
                }
            }
        }

        private static string ExtractVoiceLabel(JObject obj)
        {
            if (obj == null)
                return null;

            foreach (var key in new[] { "translate", "voice", "voice_name", "voiceName", "translation", "dub" })
            {
                var value = obj.Value<string>(key);
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return null;
        }

        private static string ExtractVoiceValue(JObject obj)
        {
            if (obj == null)
                return null;

            foreach (var key in new[] { "translation_key", "translationKey", "translation_id", "translationId", "voice_id", "voiceId", "voice", "t" })
            {
                var value = obj.Value<string>(key);
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return null;
        }

        private static List<MovieQualityOption> CollectQualityOptions(JArray items)
        {
            var result = new Dictionary<string, MovieQualityOption>(StringComparer.OrdinalIgnoreCase);

            if (items != null)
            {
                foreach (var obj in items.OfType<JObject>())
                {
                    foreach (var key in MovieQualityKeys)
                    {
                        var value = obj.Value<string>(key);
                        if (string.IsNullOrWhiteSpace(value))
                            continue;

                        value = value.Trim();
                        if (!result.ContainsKey(value))
                            result[value] = new MovieQualityOption(value);
                    }
                }
            }

            return result.Values
                .OrderByDescending(o => o.Rank)
                .ThenBy(o => o.Value, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static JArray ApplyMovieFilters(JArray items, MovieFilterCriteria filters)
        {
            if (items == null)
                return new JArray();

            if (filters == null || !filters.HasFilters)
                return items;

            var filtered = new JArray();

            foreach (var token in items)
            {
                if (token is not JObject obj)
                {
                    filtered.Add(token);
                    continue;
                }

                if (!filters.MatchesVoice(obj))
                    continue;

                if (!filters.MatchesQuality(obj))
                    continue;

                filtered.Add(obj);
            }

            return filtered;
        }

        private sealed class MovieVoiceOption
        {
            public MovieVoiceOption(string label, string value)
            {
                Label = string.IsNullOrWhiteSpace(label) ? (value ?? string.Empty) : label.Trim();
                Value = string.IsNullOrWhiteSpace(value) ? Label : value.Trim();
                NormalizedValue = NormalizeVoiceKey(Value);
                NormalizedLabel = NormalizeVoiceKey(Label);
            }

            public string Label { get; private set; }
            public string Value { get; }
            public string NormalizedValue { get; }
            public string NormalizedLabel { get; private set; }
            public HashSet<string> Providers { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public void AppendProvider(string provider)
            {
                if (string.IsNullOrWhiteSpace(provider))
                    return;

                string formatted = provider.Trim();
                if (string.IsNullOrWhiteSpace(formatted))
                    return;

                if (string.IsNullOrWhiteSpace(Label))
                    Label = formatted;
                else if (!Label.Contains(formatted, StringComparison.OrdinalIgnoreCase))
                    Label = $"{Label} • {formatted}";

                NormalizedLabel = NormalizeVoiceKey(Label);
            }
        }

        private sealed class MovieQualityOption
        {
            public MovieQualityOption(string value)
            {
                Value = value ?? string.Empty;
                Normalized = NormalizeQualityValue(Value);
                Rank = GetQualityRank(Value);
            }

            public string Value { get; }
            public string Normalized { get; }
            public int Rank { get; }
        }

        private sealed class MovieFilterCriteria
        {
            private MovieFilterCriteria(string voice, string quality)
            {
                Voice = string.IsNullOrWhiteSpace(voice) ? null : voice.Trim();
                Quality = string.IsNullOrWhiteSpace(quality) ? null : quality.Trim();
                NormalizedVoice = NormalizeVoiceKey(Voice);
                NormalizedQuality = NormalizeQualityValue(Quality);
            }

            public string Voice { get; }
            public string Quality { get; }
            public string NormalizedVoice { get; }
            public string NormalizedQuality { get; }
            public bool HasFilters => !string.IsNullOrEmpty(Voice) || !string.IsNullOrEmpty(Quality);

            public static MovieFilterCriteria From(IReadOnlyDictionary<string, string> query)
            {
                if (query == null || query.Count == 0)
                    return new MovieFilterCriteria(null, null);

                string voice = GetFirst(query, MovieVoiceQueryKeys);
                string quality = GetFirst(query, MovieQualityQueryKeys);

                return new MovieFilterCriteria(voice, quality);
            }

            private static string GetFirst(IReadOnlyDictionary<string, string> query, params string[] keys)
            {
                foreach (var key in keys ?? Array.Empty<string>())
                {
                    if (query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                        return value;
                }

                return null;
            }

            public bool MatchesVoice(JObject obj)
            {
                if (string.IsNullOrEmpty(Voice) || obj == null)
                    return true;

                foreach (var key in MovieVoiceKeys)
                {
                    var value = obj.Value<string>(key);
                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    if (string.Equals(value, Voice, StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (!string.IsNullOrEmpty(NormalizedVoice) && string.Equals(NormalizeVoiceKey(value), NormalizedVoice, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                return false;
            }

            public bool MatchesQuality(JObject obj)
            {
                if (string.IsNullOrEmpty(Quality) || obj == null)
                    return true;

                foreach (var key in MovieQualityKeys)
                {
                    var value = obj.Value<string>(key);
                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    if (string.Equals(value, Quality, StringComparison.OrdinalIgnoreCase))
                        return true;

                    var normalized = NormalizeQualityValue(value);
                    if (!string.IsNullOrEmpty(NormalizedQuality) && !string.IsNullOrEmpty(normalized)
                        && normalized.Contains(NormalizedQuality, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            public bool IsVoiceActive(MovieVoiceOption option)
            {
                if (option == null || string.IsNullOrEmpty(Voice))
                    return false;

                if (!string.IsNullOrWhiteSpace(option.Value) && string.Equals(option.Value, Voice, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (string.IsNullOrEmpty(NormalizedVoice))
                    return false;

                if (!string.IsNullOrEmpty(option.NormalizedValue) && string.Equals(option.NormalizedValue, NormalizedVoice, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!string.IsNullOrEmpty(option.NormalizedLabel) && string.Equals(option.NormalizedLabel, NormalizedVoice, StringComparison.OrdinalIgnoreCase))
                    return true;

                return false;
            }

            public bool IsQualityActive(MovieQualityOption option)
            {
                if (option == null || string.IsNullOrEmpty(Quality))
                    return false;

                if (string.Equals(option.Value, Quality, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (string.IsNullOrEmpty(NormalizedQuality))
                    return false;

                if (!string.IsNullOrEmpty(option.Normalized) && option.Normalized.Contains(NormalizedQuality, StringComparison.OrdinalIgnoreCase))
                    return true;

                return false;
            }
        }

        private static string NormalizeVoiceKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string normalized = value.Trim().ToLowerInvariant();
            normalized = Regex.Replace(normalized, @"\s+", " ");
            return normalized;
        }

        private static string NormalizeQualityValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string normalized = value.Trim().ToUpperInvariant();
            normalized = Regex.Replace(normalized, @"\s+", string.Empty);
            normalized = normalized.Replace("-", string.Empty).Replace("_", string.Empty);
            return normalized;
        }

        private static int GetQualityRank(string quality)
        {
            if (string.IsNullOrWhiteSpace(quality))
                return 0;

            var match = Regex.Match(quality, @"(\d{3,4})p", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int parsed))
                return parsed;

            if (quality.IndexOf("4K", StringComparison.OrdinalIgnoreCase) >= 0)
                return 2160;

            if (quality.IndexOf("1440", StringComparison.OrdinalIgnoreCase) >= 0)
                return 1440;

            if (quality.IndexOf("1080", StringComparison.OrdinalIgnoreCase) >= 0)
                return 1080;

            if (quality.IndexOf("720", StringComparison.OrdinalIgnoreCase) >= 0)
                return 720;

            if (quality.IndexOf("480", StringComparison.OrdinalIgnoreCase) >= 0)
                return 480;

            if (quality.IndexOf("360", StringComparison.OrdinalIgnoreCase) >= 0)
                return 360;

            return 0;
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

            var orderedSeasons = OrderSeasonItems(data).ToList();
            if (orderedSeasons.Count == 0)
            {
                html.Append("</div>");
                return html.ToString();
            }

            if (!string.IsNullOrWhiteSpace(maxQuality))
            {
                html.Append("<!--q:");
                html.Append(WebUtility.HtmlEncode(maxQuality));
                html.Append("-->");
            }

            foreach (var token in orderedSeasons)
            {
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

        private static JArray SortSeasons(JArray seasons)
        {
            return new JArray(OrderSeasonItems(seasons));
        }

        private static IEnumerable<JObject> OrderSeasonItems(JArray seasons)
        {
            return seasons
                .OfType<JObject>()
                .OrderBy(item => ExtractSeasonOrder(item))
                .ThenBy(item => item.Value<string>("provider") ?? item.Value<string>("balanser") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Value<string>("name") ?? item.Value<string>("title") ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        }

        private static int ExtractSeasonOrder(JObject item)
        {
            var directSeason = TryParseInt(item?["season"]) ?? TryParseInt(item?["s"]);
            if (directSeason.HasValue)
                return directSeason.Value;

            string name = item?.Value<string>("name") ?? item?.Value<string>("title") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(name))
            {
                var match = Regex.Match(name, @"(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int parsed))
                    return parsed;
            }

            return int.MaxValue;
        }
    }
}
