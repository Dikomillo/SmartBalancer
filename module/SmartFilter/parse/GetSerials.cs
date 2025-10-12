using Newtonsoft.Json.Linq;
using Shared.Models.Templates;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Web;

namespace SmartFilter.parse
{
    public class SerialProcessResult
    {
        public string Type { get; set; }
        public VoiceTpl? Voice { get; set; }
        public SeasonTpl? Seasons { get; set; }
        public EpisodeTpl? Episodes { get; set; }
        public int SeasonCount { get; set; }
        public int EpisodeCount { get; set; }
    }

    public static class GetSerials
    {
        public static SerialProcessResult Process(List<ProviderResult> validResults, string title, string original_title, string host, string queryString, bool rjson)
        {
            var queryParams = HttpUtility.ParseQueryString(string.IsNullOrEmpty(queryString) ? string.Empty : queryString.TrimStart('?'));
            int requestedSeason = -1;
            if (int.TryParse(queryParams["s"], out int seasonValue) && seasonValue > 0)
                requestedSeason = seasonValue;

            bool isEpisodeStage = requestedSeason > 0;
            string displayTitle = string.IsNullOrEmpty(title) ? original_title : title;

            Console.WriteLine($"📺 SmartFilter: Processing serials for '{displayTitle}' - requested season: {requestedSeason}");

            var voiceEntries = validResults
                .Where(r => !string.IsNullOrWhiteSpace(r.ProviderName))
                .Select(r => r.ProviderName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string requestedVoiceKey = queryParams["t"];
            bool voiceMatched = !string.IsNullOrEmpty(requestedVoiceKey) &&
                                voiceEntries.Any(v => string.Equals(v, requestedVoiceKey, StringComparison.OrdinalIgnoreCase));

            var voiceData = new List<(string name, bool active, string link)>();
            for (int i = 0; i < voiceEntries.Count; i++)
            {
                string providerName = voiceEntries[i];
                bool isActive = voiceMatched
                    ? string.Equals(providerName, requestedVoiceKey, StringComparison.OrdinalIgnoreCase)
                    : i == 0;

                string link = BuildLink(host, queryParams, "t", providerName, rjson);
                voiceData.Add((providerName, isActive, link));
            }

            VoiceTpl? voiceTpl = null;
            if (voiceData.Count > 0)
            {
                var vtpl = new VoiceTpl(voiceData.Count);
                foreach (var entry in voiceData)
                    vtpl.Append(entry.name, entry.active, entry.link);
                voiceTpl = vtpl;
            }

            string effectiveVoice = voiceMatched ? requestedVoiceKey : null;
            var filteredResults = !string.IsNullOrEmpty(effectiveVoice)
                ? validResults.Where(r => string.Equals(r.ProviderName, effectiveVoice, StringComparison.OrdinalIgnoreCase)).ToList()
                : validResults;

            var seasonTpl = new SeasonTpl();
            var episodeTpl = new EpisodeTpl();
            var uniqueSeasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uniqueEpisodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string bestQuality = null;

            foreach (var result in filteredResults)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(result.JsonData))
                        continue;

                    var token = JToken.Parse(result.JsonData);
                    if (isEpisodeStage)
                        AppendEpisodes(token, result.ProviderName, requestedSeason, displayTitle, ref episodeTpl, uniqueEpisodes);
                    else
                        AppendSeasons(token, host, queryParams, rjson, ref seasonTpl, uniqueSeasons, ref bestQuality);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ SmartFilter: Error parsing provider {result.ProviderName}: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(bestQuality))
                seasonTpl.quality = bestQuality;

            int seasonCount = seasonTpl.data?.Count ?? 0;
            int episodeCount = episodeTpl.data?.Count ?? 0;

            Console.WriteLine(isEpisodeStage
                ? $"📊 SmartFilter: Returning {episodeCount} episodes for season {requestedSeason}"
                : $"📊 SmartFilter: Returning {seasonCount} seasons");

            return new SerialProcessResult
            {
                Type = isEpisodeStage ? "episode" : "season",
                Voice = voiceTpl,
                Seasons = !isEpisodeStage && seasonCount > 0 ? seasonTpl : (SeasonTpl?)null,
                Episodes = isEpisodeStage && episodeCount > 0 ? episodeTpl : (EpisodeTpl?)null,
                SeasonCount = seasonCount,
                EpisodeCount = episodeCount
            };
        }

        private static void AppendSeasons(JToken token, string host, NameValueCollection queryParams, bool rjson,
            ref SeasonTpl seasonTpl, HashSet<string> uniqueSeasons, ref string bestQuality)
        {
            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                string type = obj.Value<string>("type") ?? string.Empty;

                var qualityCandidate = obj.Value<string>("maxquality") ?? obj.Value<string>("quality");
                bestQuality = ChooseBetterQuality(bestQuality, qualityCandidate);

                if (type == "season" && obj["data"] is JArray seasonArray)
                {
                    foreach (var item in seasonArray)
                        AppendSeasonItem(item, host, queryParams, rjson, ref seasonTpl, uniqueSeasons);
                }
                else if (type == "movie" && obj["data"] is JArray movieArray)
                {
                    var grouped = movieArray
                        .Where(i => i["season"] != null || i["s"] != null)
                        .GroupBy(i => (i["season"] ?? i["s"])?.ToString() ?? "1");

                    foreach (var group in grouped)
                    {
                        var seasonObject = new JObject
                        {
                            ["id"] = group.Key,
                            ["name"] = $"{group.Key} сезон"
                        };
                        AppendSeasonItem(seasonObject, host, queryParams, rjson, ref seasonTpl, uniqueSeasons);
                    }
                }
                else if (obj["data"] is JArray genericArray)
                {
                    foreach (var item in genericArray)
                        AppendSeasonItem(item, host, queryParams, rjson, ref seasonTpl, uniqueSeasons);
                }
            }
            else if (token.Type == JTokenType.Array)
            {
                foreach (var item in (JArray)token)
                    AppendSeasonItem(item, host, queryParams, rjson, ref seasonTpl, uniqueSeasons);
            }
        }

        private static void AppendSeasonItem(JToken item, string host, NameValueCollection queryParams, bool rjson,
            ref SeasonTpl seasonTpl, HashSet<string> uniqueSeasons)
        {
            string seasonId = item.Value<string>("id") ?? item.Value<string>("season") ?? item.Value<string>("s");
            string seasonName = item.Value<string>("name") ?? item.Value<string>("title");

            if (string.IsNullOrEmpty(seasonId))
                seasonId = ExtractNumericSuffix(seasonName);

            if (string.IsNullOrEmpty(seasonId))
                seasonId = ((seasonTpl.data?.Count ?? 0) + 1).ToString();

            if (string.IsNullOrEmpty(seasonName))
                seasonName = $"{seasonId} сезон";

            if (!uniqueSeasons.Add(seasonId))
                return;

            string link = BuildSeasonLink(host, queryParams, seasonId, rjson);
            seasonTpl.Append(seasonName, link, seasonId);
        }

        private static void AppendEpisodes(JToken token, string providerName, int requestedSeason, string title,
            ref EpisodeTpl episodeTpl, HashSet<string> uniqueEpisodes)
        {
            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                string type = obj.Value<string>("type") ?? string.Empty;

                if (type == "episode" && obj["data"] is JArray episodeArray)
                {
                    AppendEpisodeItems(episodeArray, providerName, requestedSeason, title, ref episodeTpl, uniqueEpisodes);
                }
                else if (type == "movie" && obj["data"] is JArray movieArray)
                {
                    var filtered = movieArray.Where(i => MatchesSeason(i, requestedSeason)).ToList();
                    AppendEpisodeItems(new JArray(filtered), providerName, requestedSeason, title, ref episodeTpl, uniqueEpisodes);
                }
                else if (obj["data"] is JArray genericArray)
                {
                    AppendEpisodeItems(genericArray, providerName, requestedSeason, title, ref episodeTpl, uniqueEpisodes);
                }
            }
            else if (token.Type == JTokenType.Array)
            {
                AppendEpisodeItems((JArray)token, providerName, requestedSeason, title, ref episodeTpl, uniqueEpisodes);
            }
        }

        private static void AppendEpisodeItems(JArray episodeArray, string providerName, int requestedSeason, string title,
            ref EpisodeTpl episodeTpl, HashSet<string> uniqueEpisodes)
        {
            foreach (var item in episodeArray)
            {
                if (!MatchesSeason(item, requestedSeason))
                    continue;

                string episodeNumber = item.Value<string>("episode") ?? item.Value<string>("e") ?? item.Value<string>("number");
                if (string.IsNullOrEmpty(episodeNumber))
                    continue;

                string episodeName = item.Value<string>("name") ?? $"{episodeNumber} серия";
                string link = item.Value<string>("url");
                if (string.IsNullOrEmpty(link))
                    continue;

                string method = item.Value<string>("method") ?? "play";
                string stream = item.Value<string>("stream");
                string quality = item.Value<string>("maxquality") ?? item.Value<string>("quality");

                var streamquality = ParseStreamQuality(item["quality"]);
                string voice = ResolveVoice(item, providerName, quality);

                string episodeKey = $"{requestedSeason}:{episodeNumber}:{providerName}:{voice}:{link}";
                if (!uniqueEpisodes.Add(episodeKey))
                    continue;

                episodeTpl.Append(
                    name: episodeName,
                    title: title,
                    s: requestedSeason.ToString(),
                    e: episodeNumber,
                    link: link,
                    method: method,
                    streamquality: streamquality,
                    streamlink: stream,
                    voice_name: voice);
            }
        }

        private static bool MatchesSeason(JToken item, int requestedSeason)
        {
            if (requestedSeason <= 0)
                return true;

            string seasonValue = item.Value<string>("season") ?? item.Value<string>("s");
            if (string.IsNullOrEmpty(seasonValue))
                return true;

            return string.Equals(seasonValue, requestedSeason.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static StreamQualityTpl? ParseStreamQuality(JToken token)
        {
            if (token is JObject qualityObj && qualityObj.HasValues)
            {
                var streams = new List<(string link, string quality)>();
                foreach (var prop in qualityObj.Properties())
                {
                    string qualityName = prop.Name;
                    string qualityLink = prop.Value?.ToString();
                    if (string.IsNullOrEmpty(qualityLink))
                        continue;

                    streams.Add((qualityLink, qualityName));
                }

                if (streams.Count > 0)
                    return new StreamQualityTpl(streams);
            }

            return null;
        }

        private static string ResolveVoice(JToken item, string providerName, string quality)
        {
            string translate = item.Value<string>("translate") ??
                               item.Value<string>("voice") ??
                               item.Value<string>("details") ??
                               providerName;

            string cleaned = ExtractCleanVoice(translate, quality);
            if (string.IsNullOrWhiteSpace(cleaned))
                return providerName;

            if (cleaned.IndexOf(providerName, StringComparison.OrdinalIgnoreCase) >= 0)
                return cleaned.Trim();

            return $"{providerName}: {cleaned.Trim()}";
        }

        private static string BuildSeasonLink(string host, NameValueCollection query, string seasonId, bool rjson)
        {
            var updated = CloneQuery(query);
            updated["s"] = seasonId;
            updated["rjson"] = rjson ? "true" : "false";

            return $"{host}/lite/smartfilter?{ToQueryString(updated)}";
        }

        private static string BuildLink(string host, NameValueCollection query, string key, string value, bool rjson)
        {
            var updated = CloneQuery(query);
            if (string.IsNullOrEmpty(value))
                updated.Remove(key);
            else
                updated[key] = value;

            updated["rjson"] = rjson ? "true" : "false";
            return $"{host}/lite/smartfilter?{ToQueryString(updated)}";
        }

        private static NameValueCollection CloneQuery(NameValueCollection query)
        {
            var clone = HttpUtility.ParseQueryString(string.Empty);
            foreach (string key in query.AllKeys)
            {
                if (key == null)
                    continue;

                clone[key] = query[key];
            }
            return clone;
        }

        private static string ToQueryString(NameValueCollection query)
        {
            var sb = new StringBuilder();
            foreach (string key in query.AllKeys)
            {
                if (string.IsNullOrEmpty(key) || query[key] == null)
                    continue;

                if (sb.Length > 0)
                    sb.Append('&');

                sb.Append(HttpUtility.UrlEncode(key));
                sb.Append('=');
                sb.Append(HttpUtility.UrlEncode(query[key]));
            }
            return sb.ToString();
        }

        private static string ChooseBetterQuality(string currentQuality, string newQuality)
        {
            if (string.IsNullOrEmpty(newQuality))
                return currentQuality;

            if (string.IsNullOrEmpty(currentQuality))
                return newQuality;

            int currentScore = QualityScore(currentQuality);
            int newScore = QualityScore(newQuality);

            return newScore > currentScore ? newQuality : currentQuality;
        }

        private static int QualityScore(string quality)
        {
            if (string.IsNullOrWhiteSpace(quality))
                return 0;

            var digits = new string(quality.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int numericQuality))
                return numericQuality;

            string lowered = quality.ToLowerInvariant();
            if (lowered.Contains("4k")) return 4000;
            if (lowered.Contains("ultra")) return 3600;
            if (lowered.Contains("full")) return 2160;
            if (lowered.Contains("hd")) return 1080;
            if (lowered.Contains("sd")) return 480;

            return 0;
        }

        private static string ExtractNumericSuffix(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            int end = value.Length - 1;
            while (end >= 0 && char.IsDigit(value[end]))
                end--;

            if (end == value.Length - 1)
                return null;

            return value.Substring(end + 1);
        }

        private static string ExtractCleanVoice(string translate, string maxQuality)
        {
            if (string.IsNullOrWhiteSpace(translate))
                return string.Empty;

            string result = translate;

            if (!string.IsNullOrWhiteSpace(maxQuality))
                result = ReplaceIgnoreCase(result, maxQuality, string.Empty);

            string[] markers =
            {
                "1080", "720", "480", "360",
                "fullhd", "ultra hd", "uhd", "hdrip", "bdrip", "webrip", "web-dl",
                "sdr", "hdr", "sd", "hd", "4k"
            };

            foreach (var marker in markers)
                result = ReplaceIgnoreCase(result, marker, string.Empty);

            var builder = new StringBuilder(result.Length);
            bool prevSpace = false;
            foreach (char c in result)
            {
                char current = c;
                if (char.IsControl(current))
                    continue;

                if (current == ',' || current == ';' || current == '|' || current == '/')
                    current = ' ';

                if (char.IsWhiteSpace(current))
                {
                    if (prevSpace)
                        continue;
                    prevSpace = true;
                    builder.Append(' ');
                }
                else
                {
                    prevSpace = false;
                    builder.Append(current);
                }
            }

            return builder.ToString().Trim(' ', '-', '.', ',');
        }

        private static string ReplaceIgnoreCase(string source, string value, string replacement)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(value))
                return source;

            int index = source.IndexOf(value, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                source = source.Remove(index, value.Length).Insert(index, replacement);
                index = source.IndexOf(value, index, StringComparison.OrdinalIgnoreCase);
            }

            return source;
        }
    }
}
