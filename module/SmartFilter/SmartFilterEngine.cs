using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SmartFilter
{
    internal sealed class ProviderDescriptor
    {
        public string Name { get; init; }
        public string Url { get; init; }
        public string Plugin { get; init; }
        public int? Index { get; init; }
    }

    internal sealed class ProviderFetchResult
    {
        public ProviderDescriptor Descriptor { get; init; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public int ResponseTime { get; set; }
        public int ItemsCount { get; set; }
        public JToken Payload { get; set; }
        public string ContentType { get; set; }

        public string ProviderName => Descriptor?.Name;
        public string ProviderPlugin => Descriptor?.Plugin;
        public int? ProviderIndex => Descriptor?.Index;

        private int? _estimatedItems;

        public bool HasContent
        {
            get
            {
                if (ItemsCount > 0)
                    return true;

                _estimatedItems ??= EstimateItems(Payload);
                return _estimatedItems > 0;
            }
        }

        public ProviderStatus ToStatus()
        {
            return new ProviderStatus
            {
                Name = ProviderName,
                Plugin = ProviderPlugin,
                Index = ProviderIndex,
                Status = Success
                    ? (HasContent ? "completed" : "empty")
                    : "error",
                Items = ItemsCount,
                ResponseTime = ResponseTime,
                Error = Success ? null : (ErrorMessage ?? "Ошибка запроса")
            };
        }

        private static int EstimateItems(JToken payload, int depth = 0)
        {
            if (payload == null || depth > 2)
                return 0;

            if (payload is JArray array)
            {
                int validCount = array.Count(item => item != null && item.Type != JTokenType.Null && item.Type != JTokenType.Undefined);
                return validCount;
            }

            if (payload is JObject obj)
            {
                if (obj.TryGetValue("success", out var success)
                    && success.Type == JTokenType.Boolean
                    && !success.Value<bool>())
                {
                    return 0;
                }

                if (obj.TryGetValue("status", out var status)
                    && status.Type == JTokenType.String
                    && status.Value<string>().Equals("error", StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }

                var standardKeys = new[]
                {
                    "data", "results", "items", "episodes", "seasons", "voice", "voices", "voice_list", "translations",
                    "files", "playlist", "streams", "qualities", "subtitles", "media"
                };

                foreach (var key in standardKeys)
                {
                    if (obj.TryGetValue(key, out var token) && token is JArray arr && arr.Count > 0)
                    {
                        int validCount = arr.Count(item => item != null && item.Type != JTokenType.Null && item.Type != JTokenType.Undefined);
                        if (validCount > 0)
                            return validCount;
                    }
                }

                if (obj.TryGetValue("content_type", out _) && obj.TryGetValue("translations", out var translations) && translations is JArray transArr)
                {
                    int validCount = transArr.Count(item => item != null && item.Type != JTokenType.Null && item.Type != JTokenType.Undefined);
                    if (validCount > 0)
                        return validCount;
                }

                foreach (var property in obj.Properties())
                {
                    if (property.Value is JArray nested && nested.Count > 0)
                    {
                        int validCount = nested.Count(item => item != null && item.Type != JTokenType.Null && item.Type != JTokenType.Undefined);
                        if (validCount > 0)
                            return validCount;
                    }

                    if (property.Value is JObject nestedObj)
                    {
                        int nestedCount = EstimateItems(nestedObj, depth + 1);
                        if (nestedCount > 0)
                            return nestedCount;
                    }
                }
            }

            return 0;
        }
    }

    public sealed class SmartFilterEngine
    {
        private readonly IMemoryCache memoryCache;
        private readonly string host;
        private readonly HttpContext httpContext;
        private readonly SemaphoreSlim semaphore;

        private static readonly Lazy<HttpClient> sharedHttpClient = new(() =>
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                UseCookies = false
            };

            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        });

        private static HttpClient HttpClient => sharedHttpClient.Value;

        public SmartFilterEngine(IMemoryCache cache, string hostUrl, HttpContext context)
        {
            memoryCache = cache;
            host = hostUrl;
            httpContext = context;
            semaphore = new SemaphoreSlim(Math.Max(1, ModInit.conf.maxParallelRequests));
        }

        public async Task<AggregationResult> AggregateProvidersAsync(
            string imdbId,
            long kinopoiskId,
            string title,
            string originalTitle,
            int year,
            int serial,
            string originalLanguage,
            string providerFilter,
            int requestedSeason,
            string progressKey)
        {
            var query = BuildBaseQueryParameters(imdbId, kinopoiskId, title, originalTitle, year, serial, originalLanguage);
            NormalizeSeasonQuery(query, serial, requestedSeason);

            var defaultType = ResolveDefaultType(serial, requestedSeason);
            var providers = await GetActiveProvidersAsync(query);

            if (!string.IsNullOrWhiteSpace(providerFilter))
            {
                providers = providers
                    .Where(p => string.Equals(p.Name, providerFilter, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(p.Plugin, providerFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var aggregation = new AggregationResult
            {
                Type = defaultType,
                ProgressKey = progressKey
            };

            if (providers.Count == 0)
            {
                SmartFilterProgress.PublishFinal(memoryCache, progressKey, aggregation.Providers);
                return aggregation;
            }

            SmartFilterProgress.Initialize(memoryCache, progressKey, providers);

            var tasks = providers.Select(provider => FetchProviderAsync(provider, query, progressKey)).ToArray();
            var results = await Task.WhenAll(tasks);

            var aggregator = new SimpleAggregator(defaultType);

            foreach (var result in results)
            {
                if (result.Success && result.Payload != null)
                {
                    result.ItemsCount = aggregator.Add(result);
                }
            }

            aggregation.Type = aggregator.Type;
            aggregation.Data = aggregator.BuildPayload();
            aggregation.Providers = results.Select(r => r.ToStatus()).OrderBy(p => p.Index ?? int.MaxValue).ThenBy(p => p.Name).ToList();

            SmartFilterProgress.PublishFinal(memoryCache, progressKey, aggregation.Providers);
            return aggregation;
        }

        private async Task<List<ProviderDescriptor>> GetActiveProvidersAsync(Dictionary<string, string> baseQuery)
        {
            var providers = new List<ProviderDescriptor>();

            try
            {
                string eventsUrl = QueryHelpers.AddQueryString($"{host}/lite/events", baseQuery);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ModInit.conf.requestTimeoutSeconds > 0 ? ModInit.conf.requestTimeoutSeconds : 40));
                string response = await HttpClient.GetStringAsync(eventsUrl, cts.Token);

                if (string.IsNullOrWhiteSpace(response) || IsHtmlResponse(response))
                    return providers;

                var providerArray = JArray.Parse(response);
                var exclude = new HashSet<string>(ModInit.conf.excludeProviders ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                var includeOnly = new HashSet<string>(ModInit.conf.includeOnlyProviders ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

                foreach (var token in providerArray.OfType<JObject>())
                {
                    string name = token.Value<string>("name");
                    string url = token.Value<string>("url");
                    string plugin = token.Value<string>("balanser");
                    int? index = token.TryGetValue("index", out var indexToken) && int.TryParse(indexToken.ToString(), out int parsedIndex)
                        ? parsedIndex
                        : null;

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                        continue;

                    if (string.Equals(name, "SmartFilter Aggregator", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (exclude.Contains(name))
                        continue;

                    if (includeOnly.Count > 0 && !includeOnly.Contains(name))
                        continue;

                    providers.Add(new ProviderDescriptor
                    {
                        Name = name,
                        Url = url.Replace("{localhost}", host, StringComparison.Ordinal),
                        Plugin = plugin,
                        Index = index
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SmartFilter: error while loading providers - {ex.Message}");
            }

            return providers.OrderBy(p => p.Index ?? int.MaxValue).ThenBy(p => p.Name).ToList();
        }

        private async Task<ProviderFetchResult> FetchProviderAsync(ProviderDescriptor provider, Dictionary<string, string> baseQuery, string progressKey)
        {
            var result = new ProviderFetchResult { Descriptor = provider };

            if (provider == null || string.IsNullOrWhiteSpace(provider.Url))
            {
                result.ErrorMessage = "Некорректный провайдер";
                return result;
            }

            await semaphore.WaitAsync();

            try
            {
                var requestQuery = new Dictionary<string, string>(baseQuery, StringComparer.OrdinalIgnoreCase)
                {
                    ["rjson"] = "true"
                };

                string url = QueryHelpers.AddQueryString(provider.Url, requestQuery);
                SmartFilterProgress.MarkRunning(memoryCache, progressKey, provider.Name);

                int maxAttempts = ModInit.conf.enableRetry ? Math.Max(1, ModInit.conf.maxRetryAttempts) : 1;

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        if (attempt > 1)
                            await Task.Delay(Math.Max(0, ModInit.conf.retryDelayMs));

                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ModInit.conf.requestTimeoutSeconds > 0 ? ModInit.conf.requestTimeoutSeconds : 25));
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        string response = await HttpClient.GetStringAsync(url, cts.Token);
                        stopwatch.Stop();
                        result.ResponseTime = (int)stopwatch.ElapsedMilliseconds;

                        if (TryParseProviderResponse(response, out var payload, out string contentType, out int items))
                        {
                            result.Payload = payload;
                            result.ContentType = contentType;
                            result.ItemsCount = items;
                            result.Success = true;
                            break;
                        }

                        result.ErrorMessage = "Неподдерживаемый ответ";
                    }
                    catch (TaskCanceledException)
                    {
                        result.ErrorMessage = "Таймаут запроса";
                        if (attempt == maxAttempts)
                            break;
                    }
                    catch (HttpRequestException ex)
                    {
                        result.ErrorMessage = ex.Message;
                        if (attempt == maxAttempts)
                            break;
                    }
                    catch (Exception ex)
                    {
                        result.ErrorMessage = ex.Message;
                        break;
                    }
                }
            }
            finally
            {
                SmartFilterProgress.MarkResult(memoryCache, progressKey, result);
                semaphore.Release();
            }

            return result;
        }

        private static bool TryParseProviderResponse(string response, out JToken payload, out string contentType, out int items)
        {
            payload = null;
            contentType = null;
            items = 0;

            if (string.IsNullOrWhiteSpace(response) || IsHtmlResponse(response))
                return false;

            try
            {
                var token = JToken.Parse(response);

                if (token is JObject obj)
                {
                    if (obj.TryGetValue("type", out var typeToken) && typeToken.Type == JTokenType.String)
                        contentType = typeToken.ToString();

                    payload = obj;
                    items = CountItems(obj);
                    return true;
                }

                if (token is JArray array)
                {
                    payload = array;
                    items = array.Count;
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static int CountItems(JObject obj)
        {
            if (obj == null)
                return 0;

            foreach (var key in new[] { "data", "results", "items", "episodes", "seasons" })
            {
                if (obj.TryGetValue(key, out var token) && token is JArray arr)
                    return arr.Count;
            }

            foreach (var property in obj.Properties())
            {
                if (property.Value is JArray nested)
                    return nested.Count;
            }

            return 0;
        }

        private Dictionary<string, string> BuildBaseQueryParameters(string imdbId, long kinopoiskId, string title, string originalTitle, int year, int serial, string originalLanguage)
        {
            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (httpContext?.Request?.Query != null)
            {
                foreach (var pair in httpContext.Request.Query)
                {
                    var value = pair.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                        query[pair.Key] = value;
                }
            }

            query.Remove("rjson");
            query.Remove("provider");

            if (!string.IsNullOrEmpty(imdbId))
                query["imdb_id"] = imdbId;
            else
                query.Remove("imdb_id");

            if (kinopoiskId > 0)
                query["kinopoisk_id"] = kinopoiskId.ToString();
            else
                query.Remove("kinopoisk_id");

            if (!string.IsNullOrEmpty(title))
                query["title"] = title;
            else
                query.Remove("title");

            if (!string.IsNullOrEmpty(originalTitle))
                query["original_title"] = originalTitle;
            else
                query.Remove("original_title");

            if (year > 0)
                query["year"] = year.ToString();
            else
                query.Remove("year");

            if (serial >= 0)
                query["serial"] = serial.ToString();
            else
                query.Remove("serial");

            if (!string.IsNullOrEmpty(originalLanguage))
                query["original_language"] = originalLanguage;
            else
                query.Remove("original_language");

            return query;
        }

        private static void NormalizeSeasonQuery(Dictionary<string, string> query, int serial, int requestedSeason)
        {
            if (query == null)
                return;

            if (serial == 1)
            {
                if (requestedSeason > 0)
                {
                    string seasonValue = requestedSeason.ToString();
                    query["s"] = seasonValue;
                    query["season"] = seasonValue;
                }
                else
                {
                    query.Remove("s");
                    query.Remove("season");
                    query["s"] = "-1";
                }
            }
            else if (requestedSeason > 0)
            {
                query["s"] = requestedSeason.ToString();
            }
            else
            {
                query.Remove("s");
                query.Remove("season");
            }
        }

        private static string ResolveDefaultType(int serial, int requestedSeason)
        {
            if (serial == 1)
                return requestedSeason > 0 ? "episode" : "season";

            if (serial == 2)
                return "episode";

            return "movie";
        }

        internal static string BuildCacheKey(Dictionary<string, string> query)
        {
            var normalized = query
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Key}={kv.Value}");

            return "smartfilter:" + string.Join("&", normalized);
        }

        private static bool IsHtmlResponse(string response)
        {
            if (string.IsNullOrEmpty(response))
                return false;

            string trimmed = response.TrimStart();
            return trimmed.StartsWith("<", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("<html", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class SimpleAggregator
        {
            private static readonly string[] SeasonKeys = { "data", "seasons" };
            private static readonly string[] EpisodeKeys = { "data", "episodes" };
            private static readonly string[] MovieKeys = { "data", "results", "items" };
            private static readonly string[] VoiceKeys = { "voice", "voices", "voice_list", "translations" };
            private static readonly Regex QualityRegex = new("(4k|[0-9]{3,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

            private readonly string defaultType;
            private readonly HashSet<string> itemKeys = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> voiceKeys = new(StringComparer.OrdinalIgnoreCase);
            private readonly JArray items = new();
            private readonly JArray voices = new();

            private string maxQuality;
            public string Type { get; private set; }

            public SimpleAggregator(string defaultType)
            {
                this.defaultType = string.IsNullOrWhiteSpace(defaultType) ? "movie" : defaultType.ToLowerInvariant();
                Type = this.defaultType;
            }

            public int Add(ProviderFetchResult result)
            {
                if (result?.Payload == null)
                    return 0;

                var payload = result.Payload;
                string providerType = NormalizeType(result.ContentType) ?? NormalizeType(payload is JObject obj ? obj.Value<string>("type") : null);

                if (!string.IsNullOrWhiteSpace(providerType))
                    UpdateType(providerType);

                var entries = ExtractItems(payload, Type);
                int added = 0;

                foreach (var token in entries)
                {
                    if (token is not JObject objItem)
                        continue;

                    var clone = (JObject)objItem.DeepClone();
                    EnsureProviderFields(clone, result);

                    string key = BuildItemKey(clone);
                    if (itemKeys.Add(key))
                    {
                        items.Add(clone);
                        added++;
                    }
                }

                foreach (var voice in ExtractVoice(payload))
                {
                    if (voice is not JObject voiceObj)
                        continue;

                    var clone = (JObject)voiceObj.DeepClone();
                    if (string.IsNullOrWhiteSpace(clone.Value<string>("provider")))
                        clone["provider"] = result.ProviderName;

                    string key = BuildVoiceKey(clone);
                    if (voiceKeys.Add(key))
                        voices.Add(clone);
                }

                var quality = ExtractQuality(payload);
                if (!string.IsNullOrWhiteSpace(quality))
                    UpdateQuality(quality);

                return added;
            }

            public JObject BuildPayload()
            {
                var payload = new JObject
                {
                    ["items"] = new JArray(items)
                };

                if (voices.Count > 0)
                    payload["voice"] = new JArray(voices);

                if (!string.IsNullOrWhiteSpace(maxQuality))
                    payload["maxquality"] = maxQuality;

                return payload;
            }

            private static IEnumerable<JToken> ExtractItems(JToken payload, string type)
            {
                if (payload == null)
                    return Array.Empty<JToken>();

                var keys = type switch
                {
                    "season" => SeasonKeys,
                    "episode" => EpisodeKeys,
                    _ => MovieKeys
                };

                if (payload is JObject obj)
                {
                    foreach (var key in keys)
                    {
                        if (obj.TryGetValue(key, out var token) && token is JArray arr && arr.Count > 0)
                            return arr;
                    }

                    foreach (var property in obj.Properties())
                    {
                        if (property.Value is JArray array && array.Count > 0)
                            return array;
                    }
                }
                else if (payload is JArray array)
                {
                    return array;
                }

                return Array.Empty<JToken>();
            }

            private static IEnumerable<JToken> ExtractVoice(JToken payload)
            {
                if (payload is not JObject obj)
                    return Array.Empty<JToken>();

                foreach (var key in VoiceKeys)
                {
                    if (obj.TryGetValue(key, out var token) && token is JArray arr && arr.Count > 0)
                        return arr;
                }

                return Array.Empty<JToken>();
            }

            private static string ExtractQuality(JToken payload)
            {
                if (payload is not JObject obj)
                    return null;

                foreach (var key in new[] { "maxquality", "quality", "quality_label", "qualityName", "qualityName" })
                {
                    if (obj.TryGetValue(key, out var token))
                    {
                        string value = token?.ToString();
                        if (!string.IsNullOrWhiteSpace(value))
                            return value;
                    }
                }

                return null;
            }

            private void UpdateType(string providerType)
            {
                providerType = NormalizeType(providerType);
                if (string.IsNullOrEmpty(providerType))
                    return;

                if (Type == providerType)
                    return;

                if (Type == "movie" && providerType is "season" or "episode")
                {
                    Type = providerType;
                    return;
                }

                if (Type == "season" && providerType == "episode")
                {
                    Type = providerType;
                    return;
                }

                if (Type == "similar" && providerType != "similar")
                {
                    Type = providerType;
                }
            }

            private void UpdateQuality(string quality)
            {
                if (string.IsNullOrWhiteSpace(quality))
                    return;

                if (string.IsNullOrWhiteSpace(maxQuality))
                {
                    maxQuality = quality;
                    return;
                }

                if (CompareQuality(quality, maxQuality) > 0)
                    maxQuality = quality;
            }

            private static int CompareQuality(string left, string right)
            {
                int scoreLeft = QualityScore(left);
                int scoreRight = QualityScore(right);

                if (scoreLeft == scoreRight)
                    return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);

                return scoreLeft.CompareTo(scoreRight);
            }

            private static int QualityScore(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return 0;

                var match = QualityRegex.Match(value);
                if (!match.Success)
                    return 0;

                if (match.Value.Equals("4k", StringComparison.OrdinalIgnoreCase))
                    return 4000;

                if (int.TryParse(match.Value, out int numeric))
                    return numeric;

                return 0;
            }

            private static string NormalizeType(string type)
            {
                if (string.IsNullOrWhiteSpace(type))
                    return null;

                type = type.Trim().ToLowerInvariant();

                return type switch
                {
                    "episodes" => "episode",
                    "seasons" => "season",
                    "series" => "episode",
                    _ => type
                };
            }

            private static void EnsureProviderFields(JObject obj, ProviderFetchResult result)
            {
                if (string.IsNullOrWhiteSpace(obj.Value<string>("provider")) && !string.IsNullOrWhiteSpace(result.ProviderName))
                    obj["provider"] = result.ProviderName;

                if (!string.IsNullOrWhiteSpace(result.ProviderPlugin) && string.IsNullOrWhiteSpace(obj.Value<string>("balanser")))
                    obj["balanser"] = result.ProviderPlugin;
            }

            private static string BuildItemKey(JObject obj)
            {
                string link = obj.Value<string>("link") ?? obj.Value<string>("url") ?? obj.Value<string>("stream");
                string episode = obj.Value<string>("episode") ?? obj.Value<string>("number");
                string season = obj.Value<string>("season") ?? obj.Value<string>("s");
                string name = obj.Value<string>("name");
                string provider = obj.Value<string>("provider");

                if (!string.IsNullOrEmpty(link))
                    return link.ToLowerInvariant();

                if (!string.IsNullOrEmpty(name))
                    return $"{provider}|{name}".ToLowerInvariant();

                if (!string.IsNullOrEmpty(episode))
                    return $"{provider}|{season}|{episode}".ToLowerInvariant();

                return Guid.NewGuid().ToString();
            }

            private static string BuildVoiceKey(JObject obj)
            {
                string name = obj.Value<string>("name") ?? obj.Value<string>("title") ?? "voice";
                string translationId = obj.Value<string>("translation_id") ?? obj.Value<string>("translationId") ?? obj.Value<string>("translation_key");
                string provider = obj.Value<string>("provider");
                string link = obj.Value<string>("link");

                if (!string.IsNullOrWhiteSpace(translationId))
                    return $"{provider}|{translationId}".ToLowerInvariant();

                if (!string.IsNullOrWhiteSpace(link))
                    return link.ToLowerInvariant();

                return $"{provider}|{name}".ToLowerInvariant();
            }
        }
    }
}
