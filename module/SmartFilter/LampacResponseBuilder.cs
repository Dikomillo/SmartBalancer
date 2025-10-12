using Newtonsoft.Json.Linq;
using Shared.Models;
using Shared.Models.Templates;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SmartFilter
{
    internal static class LampacResponseBuilder
    {
        // DeepWiki: docs/architecture/online.md (MovieTpl, SeasonTpl, EpisodeTpl contracts)
        public static JObject Build(AggregationResult aggregation, string title, string originalTitle)
        {
            if (aggregation == null || aggregation.Data == null)
                return null;

            string resolvedTitle = string.IsNullOrWhiteSpace(title)
                ? (string.IsNullOrWhiteSpace(originalTitle) ? "SmartFilter" : originalTitle)
                : title;

            return (aggregation.Type ?? string.Empty).ToLowerInvariant() switch
            {
                "season" => BuildSeasonPayload(aggregation, resolvedTitle),
                "episode" => BuildEpisodePayload(aggregation, resolvedTitle, originalTitle),
                "similar" => BuildSimilarPayload(aggregation),
                _ => BuildMoviePayload(aggregation, resolvedTitle, originalTitle)
            };
        }

        private static JObject BuildMoviePayload(AggregationResult aggregation, string title, string originalTitle)
        {
            var movie = new MovieTpl(title, originalTitle);
            foreach (var variant in EnumerateVariants(aggregation.Data as JArray))
            {
                string url = variant.Value<string>("url");
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                string label = ComposeDisplayLabel(variant);
                string method = variant.Value<string>("method") ?? "play";
                string stream = variant.Value<string>("stream");
                List<HeadersModel> headers = ExtractHeaders(variant["headers"]);
                string voiceName = variant.Value<string>("voice_label") ?? variant.Value<string>("translation");
                string details = variant.Value<string>("details");
                string qualityLabel = variant.Value<string>("quality_label") ?? variant.Value<string>("quality");
                string year = variant.Value<int?>("year")?.ToString(CultureInfo.InvariantCulture)
                              ?? variant.Value<string>("year");
                int? hlsTimeout = variant.Value<int?>("hls_manifest_timeout");

                movie.Append(
                    string.IsNullOrWhiteSpace(label) ? (voiceName ?? qualityLabel ?? "Источник") : label,
                    url,
                    method: method,
                    stream: stream,
                    voice_name: voiceName,
                    year: year,
                    details: details,
                    quality: qualityLabel,
                    headers: headers,
                    hls_manifest_timeout: hlsTimeout
                );
            }

            if (movie.IsEmpty())
                return null;

            return JObject.Parse(movie.ToJson());
        }

        private static JObject BuildSeasonPayload(AggregationResult aggregation, string title)
        {
            var (seriesData, voiceData, maxQuality) = SeriesDataHelper.Unpack(aggregation.Data);
            var season = new SeasonTpl(maxQuality);

            foreach (var entry in ExtractSeasons(seriesData))
            {
                string url = entry.Value<string>("url");
                string name = entry.Value<string>("name");
                int? identifier = entry.Value<int?>("season") ?? entry.Value<int?>("id");

                if (string.IsNullOrWhiteSpace(url))
                    continue;

                if (string.IsNullOrWhiteSpace(name))
                    name = identifier.HasValue ? $"Сезон {identifier}" : "Сезон";

                season.Append(name, url, identifier ?? 0);
            }

            if (season.data == null || season.data.Count == 0)
                return null;

            VoiceTpl? voiceTpl = BuildVoiceTpl(voiceData);
            string json = voiceTpl.HasValue ? season.ToJson(voiceTpl.Value) : season.ToJson();
            var payload = JObject.Parse(json);

            if (!payload.ContainsKey("title") && !string.IsNullOrWhiteSpace(title))
                payload["title"] = title;

            return payload;
        }

        private static JObject BuildEpisodePayload(AggregationResult aggregation, string title, string originalTitle)
        {
            var (seriesData, voiceData, _) = SeriesDataHelper.Unpack(aggregation.Data);
            var episode = new EpisodeTpl();

            foreach (var entry in ExtractEpisodes(seriesData))
            {
                string url = entry.Value<string>("url");
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                string episodeTitle = entry.Value<string>("title") ?? entry.Value<string>("name");
                string voiceName = entry.Value<string>("voice_label") ?? entry.Value<string>("details");
                string seasonNumber = (entry.Value<int?>("season") ?? entry.Value<int?>("s"))?.ToString(CultureInfo.InvariantCulture);
                string episodeNumber = (entry.Value<int?>("episode") ?? entry.Value<int?>("e"))?.ToString(CultureInfo.InvariantCulture);
                string method = entry.Value<string>("method") ?? "play";
                string stream = entry.Value<string>("stream");
                int? hlsTimeout = entry.Value<int?>("hls_manifest_timeout");
                List<HeadersModel> headers = ExtractHeaders(entry["headers"]);

                string baseTitle = !string.IsNullOrWhiteSpace(title) ? title : (originalTitle ?? "SmartFilter");
                string name = !string.IsNullOrWhiteSpace(episodeTitle) ? episodeTitle : $"Серия {episodeNumber}";

                episode.Append(
                    name,
                    baseTitle,
                    seasonNumber,
                    episodeNumber,
                    url,
                    method: method,
                    streamlink: stream,
                    voice_name: voiceName,
                    headers: headers,
                    hls_manifest_timeout: hlsTimeout
                );
            }

            if (episode.data == null || episode.data.Count == 0)
                return null;

            VoiceTpl? voiceTpl = BuildVoiceTpl(voiceData);
            string json = voiceTpl.HasValue ? episode.ToJson(voiceTpl.Value) : episode.ToJson();
            return JObject.Parse(json);
        }

        private static JObject BuildSimilarPayload(AggregationResult aggregation)
        {
            var similar = new SimilarTpl();
            var items = aggregation.Data as JArray;
            if (items == null || items.Count == 0)
                return null;

            foreach (var obj in items.OfType<JObject>())
            {
                string title = obj.Value<string>("title") ?? obj.Value<string>("name");
                string url = obj.Value<string>("url") ?? obj.Value<string>("link");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
                    continue;

                string details = obj.Value<string>("details");
                string img = obj.Value<string>("poster") ?? obj.Value<string>("img");
                string year = obj.Value<int?>("year")?.ToString(CultureInfo.InvariantCulture)
                              ?? obj.Value<string>("year");

                similar.Append(title, year, details, url, img);
            }

            if (similar.data == null || similar.data.Count == 0)
                return null;

            return JObject.Parse(similar.ToJson());
        }

        private static IEnumerable<JObject> EnumerateVariants(JArray items)
        {
            if (items == null)
                yield break;

            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in items.OfType<JObject>())
            {
                foreach (var variant in EnumerateWithAlternatives(entry))
                {
                    string url = variant.Value<string>("url");
                    if (string.IsNullOrWhiteSpace(url))
                        continue;

                    if (seenUrls.Add(url))
                        yield return variant;
                }
            }
        }

        private static IEnumerable<JObject> EnumerateWithAlternatives(JObject entry)
        {
            if (entry == null)
                yield break;

            yield return PrepareVariant(entry, null);

            if (entry["alternatives"] is JArray altArray)
            {
                foreach (var alt in altArray.OfType<JObject>())
                    yield return PrepareVariant(alt, entry);
            }
        }

        private static JObject PrepareVariant(JObject source, JObject fallback)
        {
            var clone = (JObject)source.DeepClone();
            clone.Remove("alternatives");

            CopyIfMissing(clone, fallback, "provider", "voice_label", "voice_code", "quality_label", "quality_code", "details", "method", "translation");

            if (!clone.ContainsKey("title") && fallback != null && fallback.TryGetValue("title", out var titleToken))
                clone["title"] = titleToken.DeepClone();

            return clone;
        }

        private static void CopyIfMissing(JObject target, JObject fallback, params string[] keys)
        {
            if (target == null || fallback == null || keys == null)
                return;

            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (target.ContainsKey(key))
                    continue;

                if (fallback.TryGetValue(key, out var value) && value != null && value.Type != JTokenType.Null)
                    target[key] = value.Type == JTokenType.String ? new JValue(value.ToString()) : value.DeepClone();
            }
        }

        private static VoiceTpl? BuildVoiceTpl(JArray voiceData)
        {
            if (voiceData == null || voiceData.Count == 0)
                return null;

            var tpl = new VoiceTpl(voiceData.Count);

            foreach (var voice in voiceData.OfType<JObject>())
            {
                string name = voice.Value<string>("name");
                string url = voice.Value<string>("url");
                bool active = voice.Value<bool?>("active") == true;

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                    continue;

                tpl.Append(name, active, url);
            }

            return tpl.data != null && tpl.data.Count > 0 ? tpl : (VoiceTpl?)null;
        }

        private static IEnumerable<JObject> ExtractSeasons(JToken token)
        {
            if (token == null)
                yield break;

            if (token is JObject container)
            {
                if (container["seasons"] is JArray seasons)
                {
                    foreach (var season in seasons.OfType<JObject>())
                        yield return season;
                }
            }
            else if (token is JArray array)
            {
                foreach (var season in array.OfType<JObject>())
                    yield return season;
            }
        }

        private static IEnumerable<JObject> ExtractEpisodes(JToken token)
        {
            if (token == null)
                yield break;

            if (token is JObject container)
            {
                if (container["episodes"] is JArray episodes)
                {
                    foreach (var episode in episodes.OfType<JObject>())
                        yield return episode;
                }
            }
            else if (token is JArray array)
            {
                foreach (var episode in array.OfType<JObject>())
                    yield return episode;
            }
        }

        private static List<HeadersModel> ExtractHeaders(JToken token)
        {
            if (token == null)
                return null;

            var headers = new List<HeadersModel>();

            switch (token.Type)
            {
                case JTokenType.Object:
                    foreach (var property in ((JObject)token).Properties())
                    {
                        if (string.IsNullOrWhiteSpace(property.Name))
                            continue;

                        headers.Add(new HeadersModel(property.Name, property.Value?.ToString()));
                    }
                    break;

                case JTokenType.Array:
                    foreach (var obj in ((JArray)token).OfType<JObject>())
                    {
                        string name = obj.Value<string>("name") ?? obj.Value<string>("key");
                        string value = obj.Value<string>("value") ?? obj.Value<string>("val");

                        if (!string.IsNullOrWhiteSpace(name))
                            headers.Add(new HeadersModel(name, value ?? string.Empty));
                    }
                    break;
            }

            return headers.Count > 0 ? headers : null;
        }

        private static string ComposeDisplayLabel(JObject variant)
        {
            if (variant == null)
                return null;

            var parts = new List<string>();

            string provider = variant.Value<string>("provider");
            string voice = variant.Value<string>("voice_label") ?? variant.Value<string>("translation");
            string quality = variant.Value<string>("quality_label") ?? variant.Value<string>("quality");

            if (!string.IsNullOrWhiteSpace(provider))
                parts.Add(provider.Trim());

            if (!string.IsNullOrWhiteSpace(voice) && !string.Equals(voice, provider, StringComparison.OrdinalIgnoreCase))
                parts.Add(voice.Trim());

            if (!string.IsNullOrWhiteSpace(quality))
                parts.Add(quality.Trim());

            if (parts.Count == 0)
            {
                string title = variant.Value<string>("title");
                if (!string.IsNullOrWhiteSpace(title))
                    parts.Add(title.Trim());
            }

            return parts.Count == 0 ? null : string.Join(" • ", parts);
        }
    }
}
