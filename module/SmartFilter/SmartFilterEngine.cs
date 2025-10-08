using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json.Linq;
using Shared.Models;
using Shared.Models.Templates;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace SmartFilter
{
    internal class ProviderDescriptor
    {
        public string Name { get; init; }
        public string Url { get; init; }
        public string Plugin { get; init; }
        public int? Index { get; init; }
    }

    internal class ProviderFetchResult
    {
        public ProviderDescriptor Descriptor { get; init; }
        public string ProviderName => Descriptor?.Name;
        public string ProviderPlugin => Descriptor?.Plugin;
        public int? ProviderIndex => Descriptor?.Index;
        public bool Success { get; set; }
        public bool HasContent { get; set; }
        public int ItemsCount { get; set; }
        public int ResponseTime { get; set; }
        public string ErrorMessage { get; set; }
        public string ContentType { get; set; }
        public JToken Payload { get; set; }

        public ProviderStatus ToStatus()
        {
            return new ProviderStatus
            {
                Name = ProviderName,
                Plugin = ProviderPlugin,
                Index = ProviderIndex,
                Items = ItemsCount,
                ResponseTime = ResponseTime,
                Error = Success ? null : ErrorMessage,
                Status = Success
                    ? (HasContent ? "completed" : "empty")
                    : "error"
            };
        }
    }

    public class SmartFilterEngine
    {
        private readonly struct AggregatedPayload
        {
            public AggregatedPayload(JToken json, string html)
            {
                Json = json ?? new JArray();
                Html = html;
            }

            public JToken Json { get; }
            public string Html { get; }
        }

        private sealed class SeasonEntry
        {
            public int? Number { get; init; }
            public string Name { get; set; }
            public string Link { get; init; }
            public string Provider { get; init; }
        }

        private sealed class VoiceEntry
        {
            public string Name { get; init; }
            public bool Active { get; init; }
            public string Link { get; init; }
            public string Provider { get; init; }
        }

        private sealed class EpisodeEntry
        {
            public int? Season { get; init; }
            public int? Episode { get; init; }
            public string Name { get; init; }
            public string Link { get; init; }
            public string Method { get; init; }
            public string Stream { get; init; }
            public string Translation { get; init; }
            public string Provider { get; init; }
            public JObject Headers { get; init; }
            public int? HlsManifestTimeout { get; init; }
            public string Quality { get; init; }
        }

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

        public async Task<AggregationResult> AggregateProvidersAsync(string imdbId, long kinopoiskId, string title, string originalTitle, int year, int serial, string originalLanguage, string providerFilter, int requestedSeason, string progressKey)
        {
            var baseQuery = BuildBaseQueryParameters(imdbId, kinopoiskId, title, originalTitle, year, serial, originalLanguage);
            NormalizeSeasonQuery(baseQuery, serial, requestedSeason);
            var providers = await GetActiveProvidersAsync(baseQuery, serial);

            if (!string.IsNullOrWhiteSpace(providerFilter))
            {
                providers = providers
                    .Where(p => string.Equals(p.Name, providerFilter, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(p.Plugin, providerFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var aggregation = new AggregationResult
            {
                Type = DetermineDefaultType(serial, providerFilter, requestedSeason),
                ProgressKey = progressKey
            };

            if (providers.Count == 0)
            {
                SmartFilterProgress.PublishFinal(memoryCache, progressKey, aggregation.Providers);
                return aggregation;
            }

            SmartFilterProgress.Initialize(memoryCache, progressKey, providers);

            var tasks = providers.Select(provider => FetchProviderTemplateAsync(provider, baseQuery, progressKey)).ToList();
            var results = await Task.WhenAll(tasks);

            var successful = results.Where(r => r.Success && r.Payload != null).ToList();
            if (successful.Count > 0)
                aggregation.Type = DetermineContentType(successful, serial, providerFilter, requestedSeason, aggregation.Type);

            var payload = BuildAggregationData(successful, aggregation.Type, providerFilter, baseQuery);
            aggregation.Data = payload.Json;
            aggregation.Html = payload.Html;
            aggregation.Providers = results.Select(r => r.ToStatus()).OrderBy(p => p.Index ?? int.MaxValue).ThenBy(p => p.Name).ToList();

            SmartFilterProgress.PublishFinal(memoryCache, progressKey, aggregation.Providers);
            return aggregation;
        }

        private async Task<List<ProviderDescriptor>> GetActiveProvidersAsync(Dictionary<string, string> baseQuery, int serial)
        {
            var providers = new List<ProviderDescriptor>();
            bool isAnimeRequest = IsAnimeRequest(baseQuery);

            try
            {
                string eventsUrl = QueryHelpers.AddQueryString($"{host}/lite/events", baseQuery);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ModInit.conf.requestTimeoutSeconds > 0 ? ModInit.conf.requestTimeoutSeconds : 40));
                var response = await HttpClient.GetStringAsync(eventsUrl, cts.Token);

                if (string.IsNullOrWhiteSpace(response))
                    return providers;

                var providerArray = JArray.Parse(response);
                var exclude = new HashSet<string>(ModInit.conf.excludeProviders ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                var includeOnly = new HashSet<string>(ModInit.conf.includeOnlyProviders ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

                foreach (var token in providerArray.OfType<JObject>())
                {
                    string name = token.Value<string>("name");
                    string url = token.Value<string>("url");
                    string plugin = token.Value<string>("balanser");
                    int? index = token.TryGetValue("index", out var indexToken) && int.TryParse(indexToken.ToString(), out int parsedIndex) ? parsedIndex : null;

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                        continue;

                    if (string.Equals(name, "SmartFilter Aggregator", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (exclude.Contains(name))
                        continue;

                    if (includeOnly.Count > 0 && !includeOnly.Contains(name))
                        continue;

                    if (serial == 0 && !isAnimeRequest && IsAnimeProvider(name))
                        continue;

                    providers.Add(new ProviderDescriptor
                    {
                        Name = name,
                        Url = url.Replace("{localhost}", host),
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

        private async Task<ProviderFetchResult> FetchProviderTemplateAsync(ProviderDescriptor provider, Dictionary<string, string> baseQuery, string progressKey)
        {
            var result = new ProviderFetchResult { Descriptor = provider };

            if (provider == null || string.IsNullOrWhiteSpace(provider.Url))
            {
                result.ErrorMessage = "Invalid provider";
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

                        if (TryParseProviderResponse(response, out JToken payload, out string contentType, out int count))
                        {
                            result.Payload = payload;
                            result.ContentType = contentType;
                            result.ItemsCount = count;
                            result.HasContent = count > 0;
                            result.Success = true;
                            break;
                        }

                        result.ErrorMessage = "Unsupported response";
                    }
                    catch (TaskCanceledException)
                    {
                        result.ErrorMessage = "Request timeout";
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

        private bool TryParseProviderResponse(string response, out JToken payload, out string contentType, out int itemsCount)
        {
            payload = null;
            contentType = null;
            itemsCount = 0;

            if (string.IsNullOrWhiteSpace(response) || IsHtmlResponse(response))
                return false;

            try
            {
                var token = JToken.Parse(response);

                if (token is JObject obj)
                {
                    if (obj.TryGetValue("type", out var typeToken) && typeToken.Type == JTokenType.String)
                        contentType = typeToken.ToString();

                    if (obj.TryGetValue("data", out var dataToken))
                        itemsCount = CountItems(dataToken);
                    else if (obj.TryGetValue("results", out var resultsToken))
                        itemsCount = CountItems(resultsToken);

                    if (itemsCount == 0 && obj.TryGetValue("episodes", out var episodesToken))
                        itemsCount = CountItems(episodesToken);

                    if (itemsCount == 0)
                        itemsCount = CountItems(obj);

                    payload = obj;
                }
                else if (token is JArray array)
                {
                    payload = array;
                    itemsCount = array.Count;
                }
                else
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private string DetermineContentType(IEnumerable<ProviderFetchResult> results, int serial, string providerFilter, int requestedSeason, string fallback)
        {
            if (serial == 1 && string.IsNullOrWhiteSpace(providerFilter) && requestedSeason <= 0)
                return "similar";

            var contentTypes = results
                .Select(r => r.ContentType)
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .ToList();

            if (contentTypes.Count > 0)
            {
                static bool Matches(string value, string expected) =>
                    string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

                if (serial == 1)
                {
                    if (requestedSeason > 0)
                    {
                        var episodeType = contentTypes.FirstOrDefault(t => Matches(t, "episode"));
                        if (episodeType != null)
                            return episodeType;
                    }
                    else
                    {
                        var seasonType = contentTypes.FirstOrDefault(t => Matches(t, "season"));
                        if (seasonType != null)
                            return seasonType;
                    }
                }

                foreach (var preferred in new[] { "movie", "season", "episode", "similar" })
                {
                    var match = contentTypes.FirstOrDefault(t => Matches(t, preferred));
                    if (match != null)
                        return match;
                }

            }

            var firstContentType = contentTypes.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstContentType))
                return firstContentType;

            return fallback ?? DetermineDefaultType(serial, providerFilter, requestedSeason);
        }

        private static string DetermineDefaultType(int serial, string providerFilter, int requestedSeason)
        {
            if (serial == 1)
            {
                if (requestedSeason > 0)
                    return "episode";

                return string.IsNullOrWhiteSpace(providerFilter) ? "similar" : "season";
            }

            return "movie";
        }

        private AggregatedPayload BuildAggregationData(IEnumerable<ProviderFetchResult> results, string expectedType, string providerFilter, Dictionary<string, string> baseQuery)
        {
            var providerResults = (results ?? Array.Empty<ProviderFetchResult>()).ToList();
            if (providerResults.Count == 0)
            {
                if (IsSeriesType(expectedType))
                    return CreateEmptySeriesPayload(expectedType);

                return new AggregatedPayload(new JArray(), null);
            }

            if (string.Equals(expectedType, "similar", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(providerFilter))
                return BuildProviderList(providerResults, baseQuery);

            if (string.Equals(expectedType, "season", StringComparison.OrdinalIgnoreCase))
                return BuildSeasonPayload(providerResults, baseQuery);

            if (string.Equals(expectedType, "episode", StringComparison.OrdinalIgnoreCase))
                return BuildEpisodePayload(providerResults, baseQuery);

            return new AggregatedPayload(MergePayloads(providerResults, expectedType), null);
        }

        private static bool IsSeriesType(string expectedType)
            => string.Equals(expectedType, "season", StringComparison.OrdinalIgnoreCase) || string.Equals(expectedType, "episode", StringComparison.OrdinalIgnoreCase);

        private JToken MergePayloads(IEnumerable<ProviderFetchResult> results, string expectedType)
        {
            var providerResults = (results ?? Array.Empty<ProviderFetchResult>()).ToList();
            if (providerResults.Count == 0)
                return new JArray();

            bool groupByProvider = false;

            if (!groupByProvider)
            {
                var aggregated = new JArray();

                foreach (var result in providerResults)
                {
                    foreach (var item in ExtractItems(result.Payload, expectedType))
                    {
                        if (item is JObject obj)
                        {
                            // Normalize season items to the shape Lampa expects
                        if (string.Equals(expectedType, "season", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!NormalizeSeasonItem(obj, result))
                                continue;
                        }

                        if (string.IsNullOrEmpty(obj.Value<string>("provider")))
                            obj["provider"] = ResolveProviderName(result);

                            if (string.IsNullOrEmpty(obj.Value<string>("balanser")) && !string.IsNullOrEmpty(result.ProviderPlugin))
                                obj["balanser"] = result.ProviderPlugin;
                        }

                        aggregated.Add(item);
                    }
                }

                return aggregated;
            }

            var grouped = new JObject();

            foreach (var result in providerResults)
            {
                string providerName = ResolveProviderName(result);

                bool created = false;
                if (!grouped.TryGetValue(providerName, out var providerToken) || providerToken is not JArray providerArray)
                {
                    providerArray = new JArray();
                    grouped[providerName] = providerArray;
                    created = true;
                }

                int beforeCount = providerArray.Count;

                foreach (var item in ExtractItems(result.Payload, expectedType))
                {
                    if (item is JObject obj)
                    {
                        if (string.Equals(expectedType, "season", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!NormalizeSeasonItem(obj, result))
                                continue;
                        }

                        if (string.IsNullOrEmpty(obj.Value<string>("provider")))
                            obj["provider"] = providerName;

                        if (string.IsNullOrEmpty(obj.Value<string>("balanser")) && !string.IsNullOrEmpty(result.ProviderPlugin))
                            obj["balanser"] = result.ProviderPlugin;
                    }

                    providerArray.Add(item);
                }

                if (created && beforeCount == providerArray.Count)
                    grouped.Remove(providerName);
            }

            if (!grouped.Properties().Any())
                return new JArray();

            return grouped;
        }

        private AggregatedPayload BuildSeasonPayload(IEnumerable<ProviderFetchResult> providerResults, Dictionary<string, string> baseQuery)
        {
            var seasons = new List<SeasonEntry>();
            var voices = new List<VoiceEntry>();
            var seenSeasonLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenVoiceLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string quality = null;
            string currentTranslation = GetCurrentTranslation(baseQuery);

            foreach (var result in providerResults)
            {
                if (!result.Success || result.Payload == null)
                    continue;

                quality ??= ExtractQuality(result.Payload);
                string providerName = ResolveProviderName(result);

                foreach (var token in ExtractItems(result.Payload, "season"))
                {
                    if (token is not JObject seasonObj)
                        continue;

                    if (!NormalizeSeasonItem(seasonObj, result))
                        continue;

                    int? seasonNumber = ExtractSeasonNumber(seasonObj);
                    string link = BuildSmartFilterUrl(baseQuery, providerName, seasonNumber, currentTranslation);
                    if (!string.IsNullOrWhiteSpace(link))
                        seasonObj["url"] = link;

                    string seasonLink = seasonObj.Value<string>("url") ?? seasonObj.Value<string>("link");
                    if (string.IsNullOrWhiteSpace(seasonLink) || !seenSeasonLinks.Add(seasonLink))
                        continue;

                    string seasonName = seasonObj.Value<string>("name") ?? seasonObj.Value<string>("title") ?? string.Empty;
                    seasons.Add(new SeasonEntry
                    {
                        Number = seasonNumber,
                        Name = seasonName,
                        Link = seasonLink,
                        Provider = providerName
                    });
                }

                foreach (var voiceToken in ExtractVoiceItems(result.Payload))
                {
                    var normalizedVoice = NormalizeVoiceItem(voiceToken, result, baseQuery, null, currentTranslation);
                    if (normalizedVoice == null)
                        continue;

                    string voiceLink = normalizedVoice.Value<string>("url");
                    if (string.IsNullOrWhiteSpace(voiceLink) || !seenVoiceLinks.Add(voiceLink))
                        continue;

                    voices.Add(new VoiceEntry
                    {
                        Name = normalizedVoice.Value<string>("name"),
                        Active = normalizedVoice.Value<bool?>("active") ?? false,
                        Link = voiceLink,
                        Provider = providerName
                    });
                }
            }

            if (seasons.Count == 0)
            {
                var voiceTpl = voices.Count > 0 ? BuildVoiceTemplate(voices) : (VoiceTpl?)null;
                return CreateEmptySeriesPayload("season", voiceTpl, quality);
            }

            seasons.Sort((left, right) =>
            {
                int leftSeason = left.Number ?? int.MaxValue;
                int rightSeason = right.Number ?? int.MaxValue;
                int compare = leftSeason.CompareTo(rightSeason);
                if (compare != 0)
                    return compare;

                return string.Compare(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase);
            });

            var duplicateSeasonNumbers = seasons
                .Where(i => i.Number.HasValue)
                .GroupBy(i => i.Number.Value)
                .Where(g => g.Select(v => v.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                .Select(g => g.Key)
                .ToHashSet();

            var seasonTpl = string.IsNullOrWhiteSpace(quality)
                ? new SeasonTpl(seasons.Count)
                : new SeasonTpl(quality, seasons.Count);

            foreach (var season in seasons)
            {
                string displayName = season.Name;

                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = season.Number.HasValue ? $"{season.Number.Value} сезон" : season.Provider;

                if (!season.Number.HasValue || duplicateSeasonNumbers.Contains(season.Number.Value))
                    displayName = CombineNameWithProvider(displayName, season.Provider);

                seasonTpl.Append(displayName, season.Link, season.Number?.ToString() ?? string.Empty);
            }

            var voiceTemplate = voices.Count > 0 ? BuildVoiceTemplate(voices) : (VoiceTpl?)null;
            var json = ParseTemplateJson(seasonTpl.ToJson(voiceTemplate));
            var html = seasonTpl.ToHtml(voiceTemplate);
            return new AggregatedPayload(json, html);
        }

        private AggregatedPayload BuildEpisodePayload(IEnumerable<ProviderFetchResult> providerResults, Dictionary<string, string> baseQuery)
        {
            var episodes = new List<EpisodeEntry>();
            var voices = new List<VoiceEntry>();
            var seenEpisodeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenVoiceLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string quality = null;
            string currentTranslation = GetCurrentTranslation(baseQuery);
            int? requestedSeason = GetRequestedSeason(baseQuery);
            string baseTitle = ResolveSeriesTitle(baseQuery);

            foreach (var result in providerResults)
            {
                if (!result.Success || result.Payload == null)
                    continue;

                quality ??= ExtractQuality(result.Payload);
                string providerName = ResolveProviderName(result);

                foreach (var voiceToken in ExtractVoiceItems(result.Payload))
                {
                    var normalizedVoice = NormalizeVoiceItem(voiceToken, result, baseQuery, requestedSeason, currentTranslation);
                    if (normalizedVoice == null)
                        continue;

                    string voiceLink = normalizedVoice.Value<string>("url");
                    if (string.IsNullOrWhiteSpace(voiceLink) || !seenVoiceLinks.Add(voiceLink))
                        continue;

                    voices.Add(new VoiceEntry
                    {
                        Name = normalizedVoice.Value<string>("name"),
                        Active = normalizedVoice.Value<bool?>("active") ?? false,
                        Link = voiceLink,
                        Provider = providerName
                    });
                }

                foreach (var token in ExtractItems(result.Payload, "episode"))
                {
                    if (token is not JObject episodeObj)
                        continue;

                    if (!NormalizeEpisodeItem(episodeObj, result))
                        continue;

                    string key = BuildEpisodeKey(episodeObj);
                    if (!string.IsNullOrWhiteSpace(key) && !seenEpisodeKeys.Add(key))
                        continue;

                    episodes.Add(new EpisodeEntry
                    {
                        Season = episodeObj.Value<int?>("s") ?? episodeObj.Value<int?>("season"),
                        Episode = episodeObj.Value<int?>("e") ?? episodeObj.Value<int?>("episode"),
                        Name = episodeObj.Value<string>("name") ?? episodeObj.Value<string>("title"),
                        Link = episodeObj.Value<string>("url"),
                        Method = episodeObj.Value<string>("method"),
                        Stream = episodeObj.Value<string>("stream") ?? episodeObj.Value<string>("streamlink"),
                        Translation = episodeObj.Value<string>("translate") ?? episodeObj.Value<string>("voice_name") ?? episodeObj.Value<string>("voice") ?? episodeObj.Value<string>("details"),
                        Provider = providerName,
                        Headers = episodeObj["headers"] as JObject,
                        HlsManifestTimeout = episodeObj.Value<int?>("hls_manifest_timeout"),
                        Quality = episodeObj.Value<string>("quality") ?? episodeObj.Value<string>("maxquality")
                    });
                }
            }

            if (episodes.Count == 0)
            {
                var voiceTpl = voices.Count > 0 ? BuildVoiceTemplate(voices) : (VoiceTpl?)null;
                return CreateEmptySeriesPayload("episode", voiceTpl, quality);
            }

            episodes.Sort((left, right) =>
            {
                int leftSeason = left.Season ?? int.MaxValue;
                int rightSeason = right.Season ?? int.MaxValue;
                int compare = leftSeason.CompareTo(rightSeason);
                if (compare != 0)
                    return compare;

                int leftEpisode = left.Episode ?? int.MaxValue;
                int rightEpisode = right.Episode ?? int.MaxValue;
                compare = leftEpisode.CompareTo(rightEpisode);
                if (compare != 0)
                    return compare;

                return string.Compare(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase);
            });

            var voiceTemplate = voices.Count > 0 ? BuildVoiceTemplate(voices) : (VoiceTpl?)null;
            var episodeTpl = new EpisodeTpl(episodes.Count);

            foreach (var episode in episodes)
            {
                string seasonValue = episode.Season?.ToString() ?? string.Empty;
                string episodeValue = episode.Episode?.ToString() ?? string.Empty;
                string name = string.IsNullOrWhiteSpace(episode.Name) && episode.Episode.HasValue
                    ? $"{episode.Episode.Value} серия"
                    : episode.Name ?? "Серия";
                string method = string.IsNullOrWhiteSpace(episode.Method) ? "play" : episode.Method;
                string voiceName = BuildVoiceLabel(episode.Translation, episode.Provider, episode.Quality);
                var headers = ConvertHeaders(episode.Headers);

                episodeTpl.Append(
                    name,
                    string.IsNullOrWhiteSpace(baseTitle) ? episode.Provider : baseTitle,
                    seasonValue,
                    episodeValue,
                    episode.Link,
                    method,
                    streamlink: episode.Stream,
                    voice_name: voiceName,
                    headers: headers,
                    hls_manifest_timeout: episode.HlsManifestTimeout);
            }

            var json = ParseTemplateJson(episodeTpl.ToJson(voiceTemplate));
            if (!string.IsNullOrWhiteSpace(quality) && json is JObject obj)
                obj["maxquality"] = quality;

            var html = BuildEpisodeHtml(episodeTpl, voiceTemplate);
            return new AggregatedPayload(json, html);
        }

        private AggregatedPayload CreateEmptySeriesPayload(string expectedType, VoiceTpl? voiceTpl = null, string quality = null)
        {
            bool isEpisode = string.Equals(expectedType, "episode", StringComparison.OrdinalIgnoreCase);

            if (isEpisode)
            {
                var tpl = new EpisodeTpl(0);
                var json = ParseTemplateJson(tpl.ToJson(voiceTpl));

                if (!string.IsNullOrWhiteSpace(quality) && json is JObject obj)
                    obj["maxquality"] = quality;

                var html = BuildEpisodeHtml(tpl, voiceTpl);
                return new AggregatedPayload(json, html);
            }

            SeasonTpl seasonTpl = string.IsNullOrWhiteSpace(quality)
                ? new SeasonTpl(0)
                : new SeasonTpl(quality, 0);

            var seasonJson = ParseTemplateJson(seasonTpl.ToJson(voiceTpl));
            var seasonHtml = seasonTpl.ToHtml(voiceTpl);

            return new AggregatedPayload(seasonJson, seasonHtml);
        }

        private static VoiceTpl BuildVoiceTemplate(List<VoiceEntry> voices)
        {
            if (voices == null)
                return new VoiceTpl();

            var duplicates = voices
                .Where(v => !string.IsNullOrWhiteSpace(v.Name))
                .GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(x => x.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var tpl = new VoiceTpl(voices.Count);

            foreach (var voice in voices)
            {
                string baseName = string.IsNullOrWhiteSpace(voice.Name) ? voice.Provider : voice.Name;
                string key = baseName ?? string.Empty;
                string displayName = baseName;

                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = voice.Provider;

                if (string.IsNullOrWhiteSpace(displayName) || duplicates.Contains(key))
                    displayName = CombineNameWithProvider(displayName, voice.Provider);

                tpl.Append(displayName, voice.Active, voice.Link);
            }

            return tpl;
        }

        private static string BuildEpisodeHtml(EpisodeTpl episodeTpl, VoiceTpl? voiceTpl)
        {
            string episodeHtml = episodeTpl?.ToHtml() ?? string.Empty;
            string voiceHtml = voiceTpl?.ToHtml() ?? string.Empty;

            if (string.IsNullOrEmpty(voiceHtml))
                return episodeHtml;

            if (string.IsNullOrEmpty(episodeHtml))
                return voiceHtml;

            return voiceHtml + episodeHtml;
        }

        private static string CombineNameWithProvider(string baseName, string provider)
        {
            if (string.IsNullOrWhiteSpace(provider))
                return baseName ?? string.Empty;

            if (string.IsNullOrWhiteSpace(baseName))
                return provider;

            if (baseName.IndexOf(provider, StringComparison.OrdinalIgnoreCase) >= 0)
                return baseName;

            return $"{baseName} • {provider}";
        }

        private static string BuildVoiceLabel(string translation, string provider, string quality)
        {
            var parts = new List<string>();

            void Add(string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    value = value.Trim();
                    if (parts.All(p => !string.Equals(p, value, StringComparison.OrdinalIgnoreCase)))
                        parts.Add(value);
                }
            }

            Add(translation);
            Add(quality);

            if (!string.IsNullOrWhiteSpace(provider) && parts.All(p => !string.Equals(p, provider, StringComparison.OrdinalIgnoreCase)))
                parts.Add(provider.Trim());

            if (parts.Count == 0)
                return provider;

            return string.Join(" • ", parts);
        }

        private static string ResolveSeriesTitle(Dictionary<string, string> baseQuery)
        {
            if (baseQuery == null)
                return string.Empty;

            if (baseQuery.TryGetValue("title", out var title) && !string.IsNullOrWhiteSpace(title))
                return title;

            if (baseQuery.TryGetValue("original_title", out var original) && !string.IsNullOrWhiteSpace(original))
                return original;

            return string.Empty;
        }

        private static List<HeadersModel> ConvertHeaders(JObject headersObj)
        {
            if (headersObj == null)
                return null;

            var dict = headersObj.Properties()
                .Select(p => new { p.Name, Value = p.Value?.ToString() ?? string.Empty })
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

            return dict.Count == 0 ? null : HeadersModel.Init(dict);
        }

        private static JToken ParseTemplateJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new JArray();

            try
            {
                return JToken.Parse(json);
            }
            catch
            {
                return new JArray();
            }
        }

        private bool NormalizeEpisodeItem(JObject obj, ProviderFetchResult source)
        {
            if (obj == null)
                return false;

            if (!EnsureUrl(obj, "url", "link", "stream", "file", "src"))
                return false;

            EnsureMethod(obj, "play");

            NormalizeIntProperty(obj, "s", "season");
            NormalizeIntProperty(obj, "season", "s");
            NormalizeIntProperty(obj, "e", "episode");
            NormalizeIntProperty(obj, "episode", "e");

            string providerName = ResolveProviderName(source);
            if (string.IsNullOrWhiteSpace(obj.Value<string>("provider")))
                obj["provider"] = providerName;

            if (string.IsNullOrWhiteSpace(obj.Value<string>("details")))
            {
                var voiceName = obj.Value<string>("voice_name") ?? obj.Value<string>("voice") ?? obj.Value<string>("translate");
                if (!string.IsNullOrWhiteSpace(voiceName) && !string.Equals(voiceName, providerName, StringComparison.OrdinalIgnoreCase))
                    obj["details"] = voiceName;
                else
                    obj["details"] = providerName;
            }

            if (string.IsNullOrWhiteSpace(obj.Value<string>("name")))
            {
                int? episodeNumber = obj.Value<int?>("e") ?? obj.Value<int?>("episode");
                if (episodeNumber.HasValue && episodeNumber.Value > 0)
                    obj["name"] = $"{episodeNumber.Value} серия";
            }

            if (string.IsNullOrWhiteSpace(obj.Value<string>("title")))
            {
                var baseTitle = obj.Value<string>("name");
                if (!string.IsNullOrWhiteSpace(baseTitle))
                    obj["title"] = baseTitle;
            }

            if (obj.TryGetValue("headers", out var headersToken) && headersToken is JArray headersArray)
            {
                var headers = new JObject();

                foreach (var entry in headersArray.OfType<JObject>())
                {
                    string headerName = entry.Value<string>("name") ?? entry.Value<string>("key");
                    if (string.IsNullOrWhiteSpace(headerName))
                        continue;

                    string headerValue = entry.Value<string>("value") ?? entry.Value<string>("val") ?? string.Empty;
                    headers[headerName] = headerValue;
                }

                if (headers.Properties().Any())
                    obj["headers"] = headers;
                else
                    obj.Remove("headers");
            }

            return true;
        }

        private JObject NormalizeVoiceItem(JToken voiceToken, ProviderFetchResult source, Dictionary<string, string> baseQuery, int? seasonNumber, string currentTranslation)
        {
            if (voiceToken is not JObject voiceObj)
                return null;

            var voice = (JObject)voiceObj.DeepClone();

            if (!EnsureUrl(voice, "url", "link", "file", "stream", "src"))
                return null;

            EnsureMethod(voice, "link");

            string providerName = ResolveProviderName(source);
            string translationId = ExtractTranslationId(voice);
            string link = BuildSmartFilterUrl(baseQuery, providerName, seasonNumber, translationId ?? currentTranslation);
            if (!string.IsNullOrWhiteSpace(link))
                voice["url"] = link;

            if (string.IsNullOrWhiteSpace(voice.Value<string>("name")))
            {
                var name = voice.Value<string>("title") ?? voice.Value<string>("voice") ?? voice.Value<string>("translation") ?? voice.Value<string>("voice_name");
                if (string.IsNullOrWhiteSpace(name))
                    name = providerName;

                voice["name"] = name;
            }

            voice["active"] = DetermineVoiceActive(voice, currentTranslation, translationId);

            if (string.IsNullOrWhiteSpace(voice.Value<string>("provider")))
                voice["provider"] = providerName;

            if (string.IsNullOrWhiteSpace(voice.Value<string>("details")))
                voice["details"] = providerName;

            return voice;
        }

        private IEnumerable<JObject> ExtractVoiceItems(JToken payload)
        {
            if (payload is not JObject obj)
                yield break;

            foreach (var property in obj.Properties())
            {
                if (IsVoiceProperty(property.Name))
                {
                    foreach (var item in EnumerateArrayItems(property.Value))
                    {
                        if (item is JObject voiceObj)
                            yield return voiceObj;
                    }
                }
                else if (property.Value is JObject nested)
                {
                    foreach (var nestedVoice in ExtractVoiceItems(nested))
                        yield return nestedVoice;
                }
            }
        }

        private static bool IsVoiceProperty(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
                return false;

            return propertyName.Equals("voice", StringComparison.OrdinalIgnoreCase)
                || propertyName.Equals("voices", StringComparison.OrdinalIgnoreCase)
                || propertyName.Equals("voice_list", StringComparison.OrdinalIgnoreCase)
                || propertyName.Equals("translations", StringComparison.OrdinalIgnoreCase)
                || propertyName.Equals("translation", StringComparison.OrdinalIgnoreCase)
                || propertyName.Equals("audio", StringComparison.OrdinalIgnoreCase)
                || propertyName.Equals("audios", StringComparison.OrdinalIgnoreCase)
                || propertyName.Equals("sound", StringComparison.OrdinalIgnoreCase)
                || propertyName.Equals("sounds", StringComparison.OrdinalIgnoreCase)
                || propertyName.Equals("tracks", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildEpisodeKey(JObject obj)
        {
            if (obj == null)
                return null;

            string url = obj.Value<string>("url") ?? string.Empty;
            int season = obj.Value<int?>("s") ?? obj.Value<int?>("season") ?? 0;
            int episode = obj.Value<int?>("e") ?? obj.Value<int?>("episode") ?? 0;

            return $"{season}:{episode}:{url}";
        }

        private static string ExtractQuality(JToken payload)
        {
            if (payload is not JObject obj)
                return null;

            foreach (var property in obj.Properties())
            {
                if (property.Name.Equals("maxquality", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("quality", StringComparison.OrdinalIgnoreCase))
                {
                    string value = property.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            foreach (var property in obj.Properties())
            {
                if (property.Value is JObject nested)
                {
                    string nestedQuality = ExtractQuality(nested);
                    if (!string.IsNullOrWhiteSpace(nestedQuality))
                        return nestedQuality;
                }
            }

            return null;
        }

        private string BuildSmartFilterUrl(Dictionary<string, string> baseQuery, string providerName, int? seasonNumber, string translation)
        {
            if (string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(host))
                return null;

            var query = new Dictionary<string, string>(baseQuery ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
            {
                ["provider"] = providerName
            };

            if (seasonNumber.HasValue && seasonNumber.Value > 0)
                query["s"] = seasonNumber.Value.ToString();
            else
                query.Remove("s");

            if (!string.IsNullOrWhiteSpace(translation))
                query["t"] = translation;
            else
                query.Remove("t");

            string rjsonValue = httpContext?.Request?.Query["rjson"].ToString();
            if (!string.IsNullOrWhiteSpace(rjsonValue))
                query["rjson"] = rjsonValue;
            else
                query.Remove("rjson");

            return QueryHelpers.AddQueryString($"{host}/lite/smartfilter", query);
        }

        private static string ExtractTranslationId(JObject voice)
        {
            if (voice == null)
                return null;

            foreach (var key in new[] { "translation_id", "translationId", "translation", "translate_id", "id", "voice_id", "voiceId" })
            {
                if (voice.TryGetValue(key, out var token))
                {
                    string value = token?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            var url = voice.Value<string>("url") ?? voice.Value<string>("link");
            var queryValue = ExtractQueryParameter(url, "t");
            if (!string.IsNullOrWhiteSpace(queryValue))
                return queryValue;

            var translate = voice.Value<string>("translate");
            if (!string.IsNullOrWhiteSpace(translate))
                return translate;

            return null;
        }

        private static string ExtractQueryParameter(string url, string key)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
                return null;

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                if (!Uri.TryCreate($"http://localhost{url}", UriKind.Absolute, out uri))
                    return null;
            }

            var query = QueryHelpers.ParseQuery(uri.Query);
            return query.TryGetValue(key, out var values) ? values.FirstOrDefault() : null;
        }

        private static string GetCurrentTranslation(Dictionary<string, string> baseQuery)
        {
            if (baseQuery != null && baseQuery.TryGetValue("t", out var translation) && !string.IsNullOrWhiteSpace(translation))
                return translation;

            return null;
        }

        private static int? GetRequestedSeason(Dictionary<string, string> baseQuery)
        {
            if (baseQuery != null && baseQuery.TryGetValue("s", out var seasonValue) && int.TryParse(seasonValue, out var season) && season > 0)
                return season;

            return null;
        }

        private static bool DetermineVoiceActive(JObject voice, string currentTranslation, string translationId)
        {
            if (voice.TryGetValue("active", out var activeToken))
            {
                if (activeToken.Type == JTokenType.Boolean)
                    return activeToken.Value<bool>();

                if (activeToken.Type == JTokenType.Integer)
                    return activeToken.Value<int>() != 0;

                if (activeToken.Type == JTokenType.String)
                {
                    var value = activeToken.ToString();
                    if (bool.TryParse(value, out var boolValue))
                        return boolValue;

                    if (int.TryParse(value, out var intValue))
                        return intValue != 0;
                }
            }

            if (!string.IsNullOrWhiteSpace(currentTranslation))
            {
                if (!string.IsNullOrWhiteSpace(translationId) && string.Equals(currentTranslation, translationId, StringComparison.OrdinalIgnoreCase))
                    return true;

                var name = voice.Value<string>("name") ?? voice.Value<string>("title") ?? voice.Value<string>("translate");
                if (!string.IsNullOrWhiteSpace(name) && string.Equals(currentTranslation, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static int? ExtractSeasonNumber(JObject obj)
        {
            if (obj == null)
                return null;

            if (obj.TryGetValue("id", out var idToken) && int.TryParse(idToken.ToString(), out var idValue))
                return idValue;

            if (obj.TryGetValue("season", out var seasonToken) && int.TryParse(seasonToken.ToString(), out var seasonValue))
                return seasonValue;

            if (obj.TryGetValue("s", out var sToken) && int.TryParse(sToken.ToString(), out var sValue))
                return sValue;

            var name = obj.Value<string>("name") ?? obj.Value<string>("title");
            if (!string.IsNullOrWhiteSpace(name))
            {
                var match = Regex.Match(name, "(\\d+)");
                if (match.Success && int.TryParse(match.Value, out var parsed))
                    return parsed;
            }

            return null;
        }

        private static void EnsureMethod(JObject obj, string defaultMethod)
        {
            if (obj == null)
                return;

            if (string.IsNullOrWhiteSpace(obj.Value<string>("method")))
                obj["method"] = defaultMethod;
        }

        private static bool EnsureUrl(JObject obj, params string[] fallbackKeys)
        {
            if (obj == null)
                return false;

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

        private static void NormalizeIntProperty(JObject obj, string key, params string[] aliases)
        {
            if (obj == null)
                return;

            if (TryNormalizeInt(obj, key, obj[key]))
                return;

            foreach (var alias in aliases ?? Array.Empty<string>())
            {
                if (TryNormalizeInt(obj, key, obj[alias]))
                    return;
            }
        }

        private static bool TryNormalizeInt(JObject obj, string key, JToken token)
        {
            if (obj == null || token == null || token.Type == JTokenType.Null)
                return false;

            if (token.Type == JTokenType.Integer)
            {
                obj[key] = token.Value<int>();
                return true;
            }

            if (int.TryParse(token.ToString(), out var parsed))
            {
                obj[key] = parsed;
                return true;
            }

            return false;
        }

        private bool NormalizeSeasonItem(JObject obj, ProviderFetchResult source)
        {
            if (obj == null)
                return false;

            NormalizeIntProperty(obj, "id", "season", "s");
            NormalizeIntProperty(obj, "season", "id", "s");
            NormalizeIntProperty(obj, "s", "season", "id");

            if (string.IsNullOrWhiteSpace(obj.Value<string>("name")))
            {
                int? seasonNum = obj.Value<int?>("id") ?? obj.Value<int?>("season") ?? obj.Value<int?>("s");
                if (seasonNum.HasValue && seasonNum.Value > 0)
                    obj["name"] = $"{seasonNum.Value} сезон";
                else
                {
                    var title = obj.Value<string>("title");
                    if (!string.IsNullOrWhiteSpace(title))
                        obj["name"] = title;
                }
            }

            EnsureMethod(obj, "link");

            if (!EnsureUrl(obj, "link", "file", "stream", "src"))
            {
                // url will be overwritten later with SmartFilter link, but keep the
                // original value when possible to avoid empty entries.
                var link = obj.Value<string>("link");
                if (!string.IsNullOrWhiteSpace(link))
                    obj["url"] = link;
            }

            string providerName = ResolveProviderName(source);

            if (string.IsNullOrWhiteSpace(obj.Value<string>("provider")))
                obj["provider"] = providerName;

            if (string.IsNullOrWhiteSpace(obj.Value<string>("details")))
                obj["details"] = providerName;

            return true;
        }

        private AggregatedPayload BuildProviderList(IEnumerable<ProviderFetchResult> providerResults, Dictionary<string, string> baseQuery)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<(string Title, string Year, string Details, string Url, string Provider)>();

            foreach (var result in providerResults.OrderBy(r => r.ProviderIndex ?? int.MaxValue).ThenBy(r => ResolveProviderName(r)))
            {
                if (!result.Success || !result.HasContent)
                    continue;

                string providerName = ResolveProviderName(result);
                if (!seen.Add(providerName))
                    continue;

                var providerQuery = new Dictionary<string, string>(baseQuery, StringComparer.OrdinalIgnoreCase);
                providerQuery.Remove("s");
                providerQuery["provider"] = providerName;

                string rjsonValue = httpContext?.Request?.Query["rjson"].ToString();
                if (!string.IsNullOrWhiteSpace(rjsonValue))
                    providerQuery["rjson"] = rjsonValue;

                string url = QueryHelpers.AddQueryString($"{host}/lite/smartfilter", providerQuery);

                string year = null;
                if (baseQuery.TryGetValue("year", out var yearValue) && int.TryParse(yearValue, out var parsedYear) && parsedYear > 0)
                    year = parsedYear.ToString();

                string details = BuildProviderDetails(result);

                entries.Add((providerName, year, details, url, providerName));
            }

            if (entries.Count == 0)
                return new AggregatedPayload(new JArray(), null);

            var tpl = new SimilarTpl(entries.Count);
            foreach (var entry in entries)
                tpl.Append(entry.Title, entry.Year, entry.Details, entry.Url);

            var json = ParseTemplateJson(tpl.ToJson());
            if (json is JObject obj && obj.TryGetValue("data", out var dataToken) && dataToken is JArray dataArray)
            {
                for (int i = 0; i < dataArray.Count && i < entries.Count; i++)
                {
                    if (dataArray[i] is JObject item)
                        item["provider"] = entries[i].Provider;
                }
            }

            var html = tpl.ToHtml();
            return new AggregatedPayload(json, html);
        }

        private static string BuildProviderDetails(ProviderFetchResult result)
        {
            if (result == null || result.ItemsCount <= 0)
                return null;

            string contentType = result.ContentType?.ToLowerInvariant();

            return contentType switch
            {
                "episode" => $"{result.ItemsCount} {FormatCount(result.ItemsCount, "серия", "серии", "серий")}",
                "season" => $"{result.ItemsCount} {FormatCount(result.ItemsCount, "сезон", "сезона", "сезонов")}",
                _ => $"{result.ItemsCount} {FormatCount(result.ItemsCount, "вариант", "варианта", "вариантов")}"
            };
        }

        private static string ResolveProviderName(ProviderFetchResult result)
        {
            if (!string.IsNullOrWhiteSpace(result?.ProviderName))
                return result.ProviderName;

            if (!string.IsNullOrWhiteSpace(result?.ProviderPlugin))
                return result.ProviderPlugin;

            return "Источник";
        }

        private static string FormatCount(int count, string singular, string paucal, string plural)
        {
            int mod100 = count % 100;
            if (mod100 >= 11 && mod100 <= 14)
                return plural;

            return (count % 10) switch
            {
                1 => singular,
                2 or 3 or 4 => paucal,
                _ => plural
            };
        }

        private IEnumerable<JToken> ExtractItems(JToken payload, string expectedType)
        {
            if (payload == null)
                yield break;

            if (payload is JObject obj)
            {
                if (obj.TryGetValue("data", out var dataToken))
                {
                    foreach (var item in EnumerateArrayItems(dataToken))
                        yield return item.DeepClone();
                    yield break;
                }

                if (obj.TryGetValue("results", out var resultsToken))
                {
                    foreach (var item in EnumerateArrayItems(resultsToken))
                        yield return item.DeepClone();
                    yield break;
                }

                if (expectedType == "episode" && obj.TryGetValue("episodes", out var episodesToken))
                {
                    foreach (var item in EnumerateArrayItems(episodesToken))
                        yield return item.DeepClone();
                    yield break;
                }

                if (expectedType == "season")
                {
                    if (obj.TryGetValue("seasons", out var seasonsToken))
                    {
                        foreach (var item in EnumerateArrayItems(seasonsToken))
                            yield return item.DeepClone();
                        yield break;
                    }

                    if (obj.TryGetValue("playlist", out var playlistToken))
                    {
                        foreach (var item in EnumerateArrayItems(playlistToken))
                            yield return item.DeepClone();
                        yield break;
                    }
                }

                foreach (var property in obj.Properties())
                {
                    if (property.Value is JArray propertyArray)
                    {
                        foreach (var item in propertyArray)
                            yield return item.DeepClone();
                    }
                }
            }
            else if (payload is JArray array)
            {
                foreach (var item in array)
                    yield return item.DeepClone();
            }
        }

        private static IEnumerable<JToken> EnumerateArrayItems(JToken token)
        {
            if (token == null)
                yield break;

            if (token is JArray array)
            {
                foreach (var item in array)
                    yield return item;
                yield break;
            }

            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    foreach (var item in EnumerateArrayItems(property.Value))
                        yield return item;
                }
            }
        }

        private static int CountItems(JToken token)
        {
            if (token == null)
                return 0;

            if (token is JArray array)
                return array.Count;

            if (token is JObject obj)
            {
                int count = 0;
                foreach (var property in obj.Properties())
                    count += CountItems(property.Value);
                return count;
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
                    query["s"] = requestedSeason.ToString();
                else
                    query["s"] = "-1";
            }
            else if (requestedSeason <= 0)
            {
                query.Remove("s");
            }
        }

        internal static string BuildCacheKey(Dictionary<string, string> query)
        {
            var normalized = query
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Key}={kv.Value}");

            return "smartfilter:" + string.Join("&", normalized);
        }

        private static bool IsAnimeRequest(Dictionary<string, string> query)
        {
            if (query == null)
                return false;

            if (query.TryGetValue("original_language", out var language) && !string.IsNullOrWhiteSpace(language))
            {
                language = language.ToLowerInvariant();
                if (language is "ja" or "zh")
                    return true;
            }

            if (query.TryGetValue("rchtype", out var rchType) && !string.IsNullOrWhiteSpace(rchType))
            {
                if (string.Equals(rchType, "anime", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsAnimeProvider(string providerName)
        {
            if (string.IsNullOrWhiteSpace(providerName))
                return false;

            string[] animeProviders = { "AniLiberty", "AnimeLib", "AniMedia", "AnimeGo", "Animevost", "Animebesst", "MoonAnime" };
            return animeProviders.Contains(providerName);
        }

        private static bool IsHtmlResponse(string response)
        {
            if (string.IsNullOrEmpty(response))
                return false;

            return response.StartsWith("<", StringComparison.OrdinalIgnoreCase) ||
                   response.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                   response.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
                   response.Contains("<body", StringComparison.OrdinalIgnoreCase);
        }
    }
}
