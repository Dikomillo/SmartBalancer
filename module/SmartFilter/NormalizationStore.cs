using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartFilter
{
    internal sealed class NormalizationStore
    {
        private const string DefaultConfigPath = "module/SmartFilter/normalization.json";
        private static readonly object SyncRoot = new();
        private static NormalizationStore _instance;
        private static DateTime _lastWriteTime;

        private readonly Dictionary<string, NormalizedValue> _qualityMap;
        private readonly Dictionary<string, NormalizedValue> _voiceMap;

        private NormalizationStore(Dictionary<string, NormalizedValue> qualityMap, Dictionary<string, NormalizedValue> voiceMap)
        {
            _qualityMap = qualityMap ?? new Dictionary<string, NormalizedValue>(StringComparer.OrdinalIgnoreCase);
            _voiceMap = voiceMap ?? new Dictionary<string, NormalizedValue>(StringComparer.OrdinalIgnoreCase);
        }

        public static NormalizationStore Instance
        {
            get
            {
                EnsureLoaded();
                return _instance;
            }
        }

        public NormalizedValue NormalizeQuality(string quality)
        {
            if (string.IsNullOrWhiteSpace(quality))
                return NormalizedValue.Empty;

            quality = quality.Trim();
            if (_qualityMap.TryGetValue(quality, out var mapped))
                return mapped;

            var normalizedKey = quality.ToLowerInvariant();
            if (_qualityMap.TryGetValue(normalizedKey, out mapped))
                return mapped;

            foreach (var (key, value) in _qualityMap)
            {
                if (string.Equals(key, quality, StringComparison.OrdinalIgnoreCase))
                    return value;
            }

            return new NormalizedValue(SanitizeCode(quality), quality);
        }

        public NormalizedValue NormalizeVoice(string voice)
        {
            if (string.IsNullOrWhiteSpace(voice))
                return NormalizedValue.Empty;

            voice = voice.Trim();
            if (_voiceMap.TryGetValue(voice, out var mapped))
                return mapped;

            var normalizedKey = voice.ToLowerInvariant();
            if (_voiceMap.TryGetValue(normalizedKey, out mapped))
                return mapped;

            foreach (var (key, value) in _voiceMap)
            {
                if (string.Equals(key, voice, StringComparison.OrdinalIgnoreCase))
                    return value;
            }

            return new NormalizedValue(SanitizeCode(voice), voice);
        }

        private static void EnsureLoaded()
        {
            lock (SyncRoot)
            {
                string path = DefaultConfigPath;
                var writeTime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

                if (_instance != null && writeTime == _lastWriteTime)
                    return;

                try
                {
                    if (!File.Exists(path))
                    {
                        _instance = CreateDefault();
                        _lastWriteTime = writeTime;
                        return;
                    }

                    string json = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        _instance = CreateDefault();
                        _lastWriteTime = writeTime;
                        return;
                    }

                    var options = new JsonSerializerOptions
                    {
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        PropertyNameCaseInsensitive = true
                    };

                    var payload = JsonSerializer.Deserialize<NormalizationPayload>(json, options) ?? new NormalizationPayload();
                    _instance = new NormalizationStore(BuildMap(payload.Quality), BuildMap(payload.Voice));
                    _lastWriteTime = writeTime;
                }
                catch
                {
                    _instance = CreateDefault();
                    _lastWriteTime = writeTime;
                }
            }
        }

        private static Dictionary<string, NormalizedValue> BuildMap(Dictionary<string, NormalizationEntry> source)
        {
            var map = new Dictionary<string, NormalizedValue>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
                return map;

            foreach (var pair in source)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    continue;

                var entry = pair.Value ?? new NormalizationEntry();
                var code = string.IsNullOrWhiteSpace(entry.Code)
                    ? SanitizeCode(pair.Key)
                    : entry.Code.Trim();
                var label = string.IsNullOrWhiteSpace(entry.Label)
                    ? pair.Key.Trim()
                    : entry.Label.Trim();

                var normalized = new NormalizedValue(code, label);
                map[pair.Key.Trim()] = normalized;
                map[SanitizeCode(pair.Key)] = normalized;
            }

            return map;
        }

        private static NormalizationStore CreateDefault()
        {
            var qualities = new Dictionary<string, NormalizedValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["2160"] = new("2160p", "2160p"),
                ["2160p"] = new("2160p", "2160p"),
                ["1440p"] = new("1440p", "1440p"),
                ["1080"] = new("1080p", "1080p"),
                ["1080p"] = new("1080p", "1080p"),
                ["fhd"] = new("1080p", "1080p"),
                ["fullhd"] = new("1080p", "1080p"),
                ["720p"] = new("720p", "720p"),
                ["720"] = new("720p", "720p"),
                ["hd"] = new("720p", "720p"),
                ["480p"] = new("480p", "480p"),
                ["sd"] = new("480p", "480p"),
                ["360p"] = new("360p", "360p"),
                ["camrip"] = new("camrip", "CamRip"),
                ["cam"] = new("camrip", "CamRip")
            };

            var voices = new Dictionary<string, NormalizedValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["Jaskier"] = new("jaskier", "Jaskier"),
                ["Jasker"] = new("jaskier", "Jaskier"),
                ["Jask"] = new("jaskier", "Jaskier"),
                ["ColdFilm"] = new("coldfilm", "ColdFilm"),
                ["Cold Film"] = new("coldfilm", "ColdFilm"),
                ["LostFilm"] = new("lostfilm", "LostFilm"),
                ["Lost Film"] = new("lostfilm", "LostFilm"),
                ["Original"] = new("original", "Оригинал"),
                ["eng"] = new("original", "Оригинал"),
                ["uk"] = new("uk", "Украинский"),
                ["ua"] = new("uk", "Украинский"),
                ["рус"] = new("ru", "Русский"),
                ["ru"] = new("ru", "Русский")
            };

            return new NormalizationStore(qualities, voices);
        }

        private static string SanitizeCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var chars = value.Trim().ToLowerInvariant().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!char.IsLetterOrDigit(c))
                    chars[i] = '-';
            }

            return new string(chars).Trim('-');
        }

        private class NormalizationPayload
        {
            [JsonPropertyName("quality")]
            public Dictionary<string, NormalizationEntry> Quality { get; set; } = new();

            [JsonPropertyName("voice")]
            public Dictionary<string, NormalizationEntry> Voice { get; set; } = new();
        }

        private class NormalizationEntry
        {
            [JsonPropertyName("code")]
            public string Code { get; set; }

            [JsonPropertyName("label")]
            public string Label { get; set; }
        }
    }

    internal readonly struct NormalizedValue
    {
        public static readonly NormalizedValue Empty = new(string.Empty, string.Empty);

        public NormalizedValue(string code, string label)
        {
            Code = code ?? string.Empty;
            Label = label ?? string.Empty;
        }

        public string Code { get; }
        public string Label { get; }

        public bool HasValue => !string.IsNullOrEmpty(Code) || !string.IsNullOrEmpty(Label);
    }
}
