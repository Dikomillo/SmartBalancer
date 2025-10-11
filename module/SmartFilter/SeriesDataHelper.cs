using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace SmartFilter
{
    internal static class SeriesDataHelper
    {
        // DeepWiki: docs/architecture/online.md (SeasonTpl, VoiceTpl, EpisodeTpl contracts)
        public static (JToken Data, JArray Voice, string MaxQuality) Unpack(JToken payload)
        {
            var builder = new SeriesPayloadBuilder();
            builder.Process(payload);
            return (builder.BuildData(), builder.BuildVoice(), builder.MaxQuality);
        }

        private sealed class SeriesPayloadBuilder
        {
            private readonly Dictionary<string, JArray> _seasonGroups = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _seasonKeys = new(StringComparer.OrdinalIgnoreCase);
            private readonly JArray _episodes = new();
            private readonly HashSet<string> _episodeKeys = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, JObject> _voiceMap = new(StringComparer.OrdinalIgnoreCase);

            public string MaxQuality { get; private set; }

            // DeepWiki: docs/architecture/online.md#episode (stream quality metadata expectations)
            public void Process(JToken token, string provider = null, string voice = null, int? season = null)
            {
                if (token == null)
                    return;

                switch (token.Type)
                {
                    case JTokenType.Array:
                        foreach (var item in token)
                            Process(item, provider, voice, season);
                        break;

                    case JTokenType.Object:
                        ProcessObject((JObject)token, provider, voice, season);
                        break;
                }
            }

            private void ProcessObject(JObject obj, string provider, string voice, int? season)
            {
                if (obj == null)
                    return;

                provider = ExtractProvider(obj) ?? provider;
                voice = ExtractVoiceLabel(obj) ?? voice;
                season ??= ExtractSeasonNumber(obj);

                UpdateMaxQuality(obj);

                if (IsVoiceNode(obj))
                    RegisterVoice(obj, provider);

                if (IsSeasonNode(obj))
                    RegisterSeason(obj, provider, voice);

                if (IsEpisodeNode(obj))
                    RegisterEpisode(obj, provider, voice, season);

                foreach (var property in obj.Properties())
                {
                    if (property.Value == null || property.Value.Type == JTokenType.Null)
                        continue;

                    if (IsTraversalKey(property.Name))
                    {
                        Process(property.Value, provider, voice, season);
                        continue;
                    }

                    if (property.Value is JObject childObj)
                        ProcessObject(childObj, provider, voice, season);
                    else if (property.Value is JArray childArray)
                        Process(childArray, provider, voice, season);
                }
            }

            private static bool IsTraversalKey(string key)
            {
                if (string.IsNullOrWhiteSpace(key))
                    return false;

                switch (key.ToLowerInvariant())
                {
                    case "data":
                    case "results":
                    case "items":
                    case "playlist":
                    case "playlists":
                    case "list":
                    case "children":
                    case "folders":
                    case "folder":
                    case "episodes":
                    case "episode":
                    case "seasons":
                    case "season":
                    case "series":
                    case "variants":
                    case "voice":
                    case "voices":
                    case "translations":
                    case "voice_list":
                    case "quality":
                    case "quality_list":
                        return true;
                    default:
                        return false;
                }
            }

            private static string ExtractProvider(JObject obj)
            {
                return obj.Value<string>("provider")
                    ?? obj.Value<string>("balanser")
                    ?? obj.Value<string>("source")
                    ?? obj.Value<string>("cdn")
                    ?? obj.Value<string>("service");
            }

            private static string ExtractVoiceLabel(JObject obj)
            {
                var voice = obj.Value<string>("translate")
                    ?? obj.Value<string>("voice")
                    ?? obj.Value<string>("voice_name")
                    ?? obj.Value<string>("voiceName")
                    ?? obj.Value<string>("voice_label")
                    ?? obj.Value<string>("translation")
                    ?? obj.Value<string>("dub")
                    ?? obj.Value<string>("author");

                if (!string.IsNullOrWhiteSpace(voice))
                    return voice.Trim();

                var type = obj.Value<string>("type");
                if (!string.IsNullOrWhiteSpace(type) && type.Equals("voice", StringComparison.OrdinalIgnoreCase))
                {
                    var label = obj.Value<string>("name")
                        ?? obj.Value<string>("title")
                        ?? obj.Value<string>("label");
                    return string.IsNullOrWhiteSpace(label) ? null : label.Trim();
                }

                return null;
            }

            private static int? ExtractSeasonNumber(JObject obj)
            {
                int? ResolveInt(params string[] keys)
                {
                    foreach (var key in keys)
                    {
                        if (string.IsNullOrWhiteSpace(key))
                            continue;

                        if (!obj.TryGetValue(key, out var token) || token == null)
                            continue;

                        if (token.Type == JTokenType.Integer)
                            return token.Value<int>();

                        if (int.TryParse(token.ToString(), out int parsed))
                            return parsed;
                    }

                    return null;
                }

                return ResolveInt("season", "s", "season_number", "number", "index");
            }

            private void UpdateMaxQuality(JObject obj)
            {
                var candidate = obj.Value<string>("maxquality")
                    ?? obj.Value<string>("maxQuality")
                    ?? obj.Value<string>("quality");

                if (string.IsNullOrWhiteSpace(candidate))
                    return;

                if (string.IsNullOrWhiteSpace(MaxQuality))
                {
                    MaxQuality = candidate.Trim();
                    return;
                }

                var currentScore = ScoreQuality(MaxQuality);
                var candidateScore = ScoreQuality(candidate);
                if (candidateScore > currentScore)
                    MaxQuality = candidate.Trim();
            }

            private static int ScoreQuality(string quality)
            {
                if (string.IsNullOrWhiteSpace(quality))
                    return 0;

                var normalized = quality.Trim().ToLowerInvariant();
                return normalized switch
                {
                    "4k" or "uhd" or "2160p" => 6,
                    "1440p" => 5,
                    "1080p" or "fullhd" => 4,
                    "720p" or "hd" => 3,
                    "480p" => 2,
                    "360p" => 1,
                    _ => 1
                };
            }

            private bool IsVoiceNode(JObject obj)
            {
                var type = obj.Value<string>("type");
                if (!string.IsNullOrWhiteSpace(type) && type.Equals("voice", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!string.IsNullOrWhiteSpace(obj.Value<string>("method")) && obj.ContainsKey("url"))
                {
                    var name = obj.Value<string>("name") ?? obj.Value<string>("title");
                    if (!string.IsNullOrWhiteSpace(name) && obj.ContainsKey("active"))
                        return true;
                }

                if (EnsureUrl(obj, out _) && !HasEpisodeMarker(obj) && !HasSeasonMarker(obj) && !obj.ContainsKey("playlist") && !obj.ContainsKey("items") && !obj.ContainsKey("episodes"))
                    return true;

                return false;
            }

            private bool IsSeasonNode(JObject obj)
            {
                var type = obj.Value<string>("type");
                if (!string.IsNullOrWhiteSpace(type) && type.Equals("season", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (obj.ContainsKey("playlist") || obj.ContainsKey("playlists"))
                    return true;

                if (obj.ContainsKey("items") || obj.ContainsKey("episodes"))
                    return true;

                if (obj.ContainsKey("url") || obj.ContainsKey("link"))
                {
                    var title = obj.Value<string>("title") ?? obj.Value<string>("name");
                    var hasSeasonHint = obj.ContainsKey("season") || obj.ContainsKey("s") || (!string.IsNullOrWhiteSpace(title) && title.IndexOf("сезон", StringComparison.OrdinalIgnoreCase) >= 0);
                    return hasSeasonHint;
                }

                return false;
            }

            private bool IsEpisodeNode(JObject obj)
            {
                var type = obj.Value<string>("type");
                if (!string.IsNullOrWhiteSpace(type) && type.Equals("episode", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!EnsureUrl(obj, out _))
                    return false;

                return HasEpisodeMarker(obj);
            }

            private void RegisterVoice(JObject obj, string provider)
            {
                var name = obj.Value<string>("name")
                    ?? obj.Value<string>("title")
                    ?? obj.Value<string>("label")
                    ?? ExtractVoiceLabel(obj)
                    ?? "Оригинал";

                name = name.Trim();
                var key = name.ToLowerInvariant();

                if (_voiceMap.ContainsKey(key))
                    return;

                var voice = new JObject
                {
                    ["name"] = name
                };

                if (obj.TryGetValue("active", out var active))
                    voice["active"] = active.DeepClone();

                if (EnsureUrl(obj, out string url))
                    voice["url"] = url;

                if (obj.TryGetValue("method", out var method))
                    voice["method"] = method.DeepClone();

                if (!string.IsNullOrWhiteSpace(provider))
                    voice["provider"] = provider;

                if (obj.TryGetValue("details", out var details))
                    voice["details"] = details.DeepClone();

                _voiceMap[key] = voice;
            }

            private void RegisterSeason(JObject obj, string provider, string voice)
            {
                if (!EnsureUrl(obj, out string url))
                    return;

                var seasonNumber = ExtractSeasonNumber(obj);
                var name = obj.Value<string>("title")
                    ?? obj.Value<string>("name")
                    ?? (seasonNumber.HasValue ? $"Сезон {seasonNumber}" : "Сезон");

                var keyParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(provider))
                    keyParts.Add(provider.ToLowerInvariant());
                if (!string.IsNullOrWhiteSpace(name))
                    keyParts.Add(name.ToLowerInvariant());
                if (!string.IsNullOrWhiteSpace(voice))
                    keyParts.Add(voice.ToLowerInvariant());
                if (!string.IsNullOrWhiteSpace(url))
                    keyParts.Add(url.ToLowerInvariant());

                var key = string.Join('|', keyParts);
                if (_seasonKeys.Contains(key))
                    return;

                _seasonKeys.Add(key);

                var normalized = new JObject
                {
                    ["type"] = "season",
                    ["name"] = name,
                    ["url"] = url
                };

                if (seasonNumber.HasValue)
                    normalized["season"] = seasonNumber.Value;

                if (!string.IsNullOrWhiteSpace(provider))
                    normalized["provider"] = provider;

                if (!string.IsNullOrWhiteSpace(voice))
                    normalized["voice"] = voice;

                if (obj.TryGetValue("details", out var details))
                    normalized["details"] = details.DeepClone();

                if (obj.TryGetValue("method", out var method))
                    normalized["method"] = method.DeepClone();

                if (obj.TryGetValue("poster", out var poster))
                    normalized["poster"] = poster.DeepClone();

                if (obj.TryGetValue("meta", out var meta))
                    normalized["meta"] = meta.DeepClone();

                var groupKey = string.IsNullOrWhiteSpace(provider) ? string.Empty : provider;
                if (!_seasonGroups.TryGetValue(groupKey, out var group))
                {
                    group = new JArray();
                    _seasonGroups[groupKey] = group;
                }

                group.Add(normalized);
            }

            private void RegisterEpisode(JObject obj, string provider, string voice, int? season)
            {
                if (!EnsureUrl(obj, out string url))
                    return;

                var episodeNumber = ExtractEpisodeNumber(obj);
                season ??= ExtractSeasonNumber(obj);

                var keyParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(provider))
                    keyParts.Add(provider.ToLowerInvariant());
                if (season.HasValue)
                    keyParts.Add($"s{season.Value}");
                if (episodeNumber.HasValue)
                    keyParts.Add($"e{episodeNumber.Value}");
                if (!string.IsNullOrWhiteSpace(url))
                    keyParts.Add(url.ToLowerInvariant());
                if (!string.IsNullOrWhiteSpace(voice))
                    keyParts.Add(voice.ToLowerInvariant());

                var key = string.Join('|', keyParts);
                if (_episodeKeys.Contains(key))
                    return;

                _episodeKeys.Add(key);

                var normalized = new JObject
                {
                    ["type"] = "episode",
                    ["url"] = url
                };

                if (!string.IsNullOrWhiteSpace(provider))
                    normalized["provider"] = provider;

                if (!string.IsNullOrWhiteSpace(voice))
                    normalized["voice"] = voice;

                if (season.HasValue)
                    normalized["season"] = season.Value;

                if (episodeNumber.HasValue)
                    normalized["episode"] = episodeNumber.Value;

                var title = obj.Value<string>("title")
                    ?? obj.Value<string>("name")
                    ?? obj.Value<string>("episode_title");
                if (!string.IsNullOrWhiteSpace(title))
                    normalized["title"] = title;

                if (obj.TryGetValue("details", out var details))
                    normalized["details"] = details.DeepClone();

                if (obj.TryGetValue("method", out var method))
                    normalized["method"] = method.DeepClone();

                if (obj.TryGetValue("subtitles", out var subs))
                    normalized["subtitles"] = subs.DeepClone();

                if (obj.TryGetValue("headers", out var headers))
                    normalized["headers"] = headers.DeepClone();

                var qualities = ExtractQualities(obj);
                if (qualities != null && qualities.Count > 0)
                    normalized["quality"] = qualities;

                if (obj.TryGetValue("stream", out var stream))
                    normalized["stream"] = stream.DeepClone();

                _episodes.Add(normalized);
            }

            private static int? ExtractEpisodeNumber(JObject obj)
            {
                int? ResolveInt(params string[] keys)
                {
                    foreach (var key in keys)
                    {
                        if (string.IsNullOrWhiteSpace(key))
                            continue;

                        if (!obj.TryGetValue(key, out var token) || token == null)
                            continue;

                        if (token.Type == JTokenType.Integer)
                            return token.Value<int>();

                        if (int.TryParse(token.ToString(), out int parsed))
                            return parsed;
                    }

                    return null;
                }

                return ResolveInt("episode", "e", "episode_number", "serie", "series", "number", "index");
            }

            private static bool HasEpisodeMarker(JObject obj)
            {
                if (obj == null)
                    return false;

                return obj.ContainsKey("episode")
                    || obj.ContainsKey("e")
                    || obj.ContainsKey("serie")
                    || obj.ContainsKey("series")
                    || obj.ContainsKey("episode_number")
                    || obj.ContainsKey("number");
            }

            private static bool HasSeasonMarker(JObject obj)
            {
                if (obj == null)
                    return false;

                return obj.ContainsKey("season")
                    || obj.ContainsKey("s")
                    || obj.ContainsKey("season_number")
                    || obj.ContainsKey("season_id");
            }

            private static bool EnsureUrl(JObject obj, out string url)
            {
                url = obj.Value<string>("url")
                    ?? obj.Value<string>("link")
                    ?? obj.Value<string>("file")
                    ?? obj.Value<string>("stream")
                    ?? obj.Value<string>("src");

                if (string.IsNullOrWhiteSpace(url))
                    return false;

                url = url.Trim();
                return !string.IsNullOrWhiteSpace(url);
            }

            private static JArray ExtractQualities(JObject obj)
            {
                var qualities = new JArray();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void AddQualityToken(JToken token)
                {
                    if (token == null || token.Type == JTokenType.Null)
                        return;

                    if (token is JArray array)
                    {
                        foreach (var element in array)
                            AddQualityToken(element);
                        return;
                    }

                    string label;

                    if (token is JObject qualityObj)
                    {
                        label = qualityObj.Value<string>("quality")
                            ?? qualityObj.Value<string>("label")
                            ?? qualityObj.Value<string>("name")
                            ?? qualityObj.Value<string>("title");

                        if (!string.IsNullOrWhiteSpace(label))
                        {
                            label = label.Trim();
                            if (seen.Add(label))
                                qualities.Add(label);
                        }

                        return;
                    }

                    label = token.ToString();
                    if (string.IsNullOrWhiteSpace(label))
                        return;

                    label = label.Trim();
                    if (seen.Add(label))
                        qualities.Add(label);
                }

                var candidates = new[] { "quality", "qualitys", "quality_list", "qualities", "maxquality", "maxQuality" };
                foreach (var key in candidates)
                {
                    if (!obj.TryGetValue(key, out var token) || token == null)
                        continue;

                    AddQualityToken(token);
                }

                return qualities.Count > 0 ? qualities : null;
            }

            public JToken BuildData()
            {
                if (_seasonGroups.Count == 0 && _episodes.Count == 0)
                    return new JArray();

                var result = new JObject();

                if (_seasonGroups.Count > 0)
                {
                    var flatSeasons = new JArray();
                    foreach (var group in _seasonGroups.Values)
                    {
                        foreach (var season in group)
                            flatSeasons.Add(season.DeepClone());
                    }

                    result["seasons"] = flatSeasons;

                    if (_seasonGroups.Count > 1 || !_seasonGroups.ContainsKey(string.Empty))
                    {
                        var grouped = new JObject();
                        foreach (var pair in _seasonGroups)
                        {
                            var key = string.IsNullOrWhiteSpace(pair.Key) ? "default" : pair.Key;
                            grouped[key] = pair.Value.DeepClone();
                        }

                        result["groupedSeasons"] = grouped;
                    }
                }

                if (_episodes.Count > 0)
                    result["episodes"] = _episodes.DeepClone();

                return result;
            }

            public JArray BuildVoice()
            {
                if (_voiceMap.Count == 0)
                    return null;

                var voices = new JArray();
                foreach (var voice in _voiceMap.Values)
                    voices.Add(voice.DeepClone());

                return voices;
            }
        }
    }
}
