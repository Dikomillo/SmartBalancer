using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared.Engine;
using Shared.Models;
using Shared.Models.Templates;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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
            public string Quality { get; init; }
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

        private sealed class AggregationAccumulator
        {
            private readonly Dictionary<string, JObject> _items = new(StringComparer.OrdinalIgnoreCase);
            private readonly string _fallbackTitle;
            private readonly string _fallbackOriginal;
            private readonly int _fallbackYear;

            public AggregationAccumulator(string title, string originalTitle, int year)
            {
                _fallbackTitle = title;
                _fallbackOriginal = originalTitle;
                _fallbackYear = year;
            }

            public bool AddResult(SmartFilterEngine engine, ProviderFetchResult result, string expectedType)
            {
                bool changed = false;

                foreach (var normalized in engine.BuildNormalizedItems(result, expectedType, _fallbackTitle, _fallbackOriginal, _fallbackYear))
                {
                    if (normalized == null)
                        continue;

                    string key = normalized.Value<string>("id");
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    if (_items.TryGetValue(key, out var existing))
                    {
                        if (SmartFilterEngine.MergeNormalizedItem(existing, normalized))
                            changed = true;
                    }
                    else
                    {
                        _items[key] = (JObject)normalized.DeepClone();
                        changed = true;
                    }
                }

                return changed;
            }

            public (JArray Items, AggregationMetadata Metadata) BuildSnapshot()
            {
                var array = new JArray(_items.Values.Select(v => v.DeepClone()));
                var metadata = SmartFilterEngine.BuildMetadata(array);
                return (array, metadata);
            }
        }

        private readonly IMemoryCache memoryCache;
        private readonly string host;
        private readonly HttpContext httpContext;
        private readonly SemaphoreSlim semaphore;

        private static readonly string[] UkrainianVoiceKeywords =
        {
            "укр",
            "укра",
            "україн",
            "украин",
            "uk",
            "ua",
            "ukr"
        };

        private static readonly string[] RussianVoiceKeywords =
        {
            "рус",
            "рос",
            "ru",
            "rus",
            "russian",
            "росій",
            "русск",
            "росс"
        };

        private static readonly string[] EnglishVoiceKeywords =
        {
            "eng",
            "англ",
            "english",
            "original",
            "ориг"
        };

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

            bool preferSingleProvider = false;
            if (ModInit.conf.preferSingleProviderPassthrough && providers.Count == 1 && string.IsNullOrWhiteSpace(providerFilter))
            {
                preferSingleProvider = true;
                providerFilter = providers[0].Name;
            }

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
                SmartFilterProgress.PublishFinal(memoryCache, progressKey, aggregation.Providers, new JArray(), new AggregationMetadata());
                return aggregation;
            }

            SmartFilterProgress.Initialize(memoryCache, progressKey, providers);

            var accumulator = new AggregationAccumulator(title, originalTitle, year);
            var successful = new List<ProviderFetchResult>();
            var allResults = new List<ProviderFetchResult>();

            var pendingTasks = providers
                .Select(provider => FetchProviderTemplateAsync(provider, baseQuery, progressKey))
                .ToList();

            while (pendingTasks.Count > 0)
            {
                var completedTask = await Task.WhenAny(pendingTasks);
                pendingTasks.Remove(completedTask);

                var result = await completedTask;
                allResults.Add(result);

                ApplyAdapters(result);

                if (result.Success && result.Payload != null)
                {
                    successful.Add(result);
                    aggregation.Type = DetermineContentType(successful, serial, providerFilter, requestedSeason, aggregation.Type);

                    if (accumulator.AddResult(this, result, aggregation.Type))
                    {
                        var (partial, metadata) = accumulator.BuildSnapshot();
                        SmartFilterProgress.UpdatePartial(memoryCache, progressKey, partial, metadata, ready: false);
                    }
                }
            }

            if (preferSingleProvider && string.Equals(aggregation.Type, "similar", StringComparison.OrdinalIgnoreCase))
                aggregation.Type = requestedSeason > 0 ? "episode" : "season";

            var (items, finalMetadata) = accumulator.BuildSnapshot();
            aggregation.Data = items ?? new JArray();
            aggregation.Html = null;
            aggregation.Metadata = finalMetadata;
            aggregation.Providers = allResults.Select(r => r.ToStatus()).OrderBy(p => p.Index ?? int.MaxValue).ThenBy(p => p.Name).ToList();

            SmartFilterProgress.PublishFinal(memoryCache, progressKey, aggregation.Providers, items, finalMetadata);
            return aggregation;
        }

        private async Task<List<ProviderDescriptor>> GetActiveProvidersAsync(Dictionary<string, string> baseQuery, int serial)
        {
            var providers = new List<ProviderDescriptor>();
            bool isAnimeRequest = IsAnimeRequest(baseQuery);

            try
            {
                string eventsUrl = QueryHelpers.AddQueryString($"{host}/lite/events", baseQuery);
                int timeout = ModInit.conf.requestTimeoutSeconds > 0 ? ModInit.conf.requestTimeoutSeconds : 40;
                string response = await Http.Get(eventsUrl, timeoutSeconds: timeout, statusCodeOK: false, weblog: false).ConfigureAwait(false);

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
                int timeout = ModInit.conf.requestTimeoutSeconds > 0 ? ModInit.conf.requestTimeoutSeconds : 25;

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    if (attempt > 1)
                        await Task.Delay(Math.Max(0, ModInit.conf.retryDelayMs)).ConfigureAwait(false);

                    try
                    {
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        var httpResult = await Http.BaseGetAsync(url, timeoutSeconds: timeout, statusCodeOK: false, weblog: false).ConfigureAwait(false);
                        stopwatch.Stop();
                        result.ResponseTime = (int)stopwatch.ElapsedMilliseconds;

                        string responseBody = httpResult.content;
                        var httpResponse = httpResult.response;
                        var statusCode = httpResponse?.StatusCode ?? 0;

                        try
                        {
                            result.ErrorMessage = "Unsupported response";

                            if (!string.IsNullOrWhiteSpace(responseBody) &&
                                TryParseProviderResponse(responseBody, out JToken payload, out string contentType, out int count))
                            {
                                result.Payload = payload;
                                result.ContentType = contentType;
                                result.ItemsCount = count;
                                result.HasContent = count > 0;
                                result.Success = true;
                                httpResponse?.Dispose();
                                break;
                            }

                            if (statusCode == HttpStatusCode.RequestTimeout)
                                result.ErrorMessage = "Request timeout";
                            else if (httpResponse != null && string.IsNullOrWhiteSpace(responseBody))
                                result.ErrorMessage = $"HTTP {(int)statusCode}";
                            else if (httpResponse == null && string.IsNullOrWhiteSpace(responseBody))
                                result.ErrorMessage = "Request failed";
                        }
                        finally
                        {
                            httpResponse?.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        result.ErrorMessage = ex.Message;
                        if (attempt == maxAttempts)
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

            var rawContentTypes = results
                .Select(r => r.ContentType)
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .ToList();

            var contentTypes = rawContentTypes
                .Select(type => NormalizeContentType(type, serial, requestedSeason))
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

            var firstContentType = contentTypes.FirstOrDefault()
                ?? NormalizeContentType(rawContentTypes.FirstOrDefault(), serial, requestedSeason);
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
            string originalLanguage = GetOriginalLanguage(baseQuery);

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
                        Provider = providerName,
                        Quality = seasonObj.Value<string>("quality")
                                   ?? seasonObj.Value<string>("maxquality")
                                   ?? quality
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

            var processedVoices = FilterAndSortVoices(voices, originalLanguage, currentTranslation);

            if (seasons.Count == 0)
            {
                if (ModInit.conf.enableSeasonFallback)
                {
                    var fallback = TryBuildSeasonFallback(providerResults, baseQuery, processedVoices, quality, currentTranslation, originalLanguage);
                    if (fallback.HasValue)
                        return fallback.Value;
                }

                var voiceTpl = processedVoices.Count > 0 ? BuildVoiceTemplate(processedVoices) : (VoiceTpl?)null;
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

            var voiceTemplate = processedVoices.Count > 0 ? BuildVoiceTemplate(processedVoices) : (VoiceTpl?)null;
            var json = ParseTemplateJson(seasonTpl.ToJson(voiceTemplate));
            ApplySeasonMetadata(json, seasons, quality);
            var html = seasonTpl.ToHtml(voiceTemplate);
            return new AggregatedPayload(json, html);
        }

        private AggregatedPayload? TryBuildSeasonFallback(
            IEnumerable<ProviderFetchResult> providerResults,
            Dictionary<string, string> baseQuery,
            List<VoiceEntry> strictVoices,
            string quality,
            string currentTranslation,
            string originalLanguage)
        {
            var seasons = new List<SeasonEntry>();
            var voices = new List<VoiceEntry>();
            var seenSeasonKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenVoiceLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string localQuality = quality;

            foreach (var result in providerResults)
            {
                if (!result.Success || result.Payload == null)
                    continue;

                localQuality ??= ExtractQuality(result.Payload);
                string providerName = ResolveProviderName(result);

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

                foreach (var episodeToken in ExtractEpisodeTokens(result.Payload))
                {
                    if (episodeToken is not JObject episodeObj)
                        continue;

                    if (!NormalizeEpisodeItem(episodeObj, result, strict: false, out var failureReason))
                    {
                        LogNormalizationFailure("season-fallback", result, episodeObj, failureReason);
                        continue;
                    }

                    int? seasonNumber = episodeObj.Value<int?>("s") ?? episodeObj.Value<int?>("season");
                    if (!seasonNumber.HasValue || seasonNumber.Value <= 0)
                        continue;

                    string link = BuildSmartFilterUrl(baseQuery, providerName, seasonNumber, currentTranslation);
                    if (string.IsNullOrWhiteSpace(link))
                        continue;

                    string key = $"{providerName}:{seasonNumber.Value}";
                    if (!seenSeasonKeys.Add(key))
                        continue;

                    seasons.Add(new SeasonEntry
                    {
                        Number = seasonNumber,
                        Name = $"{seasonNumber.Value} сезон",
                        Link = link,
                        Provider = providerName,
                        Quality = episodeObj.Value<string>("quality")
                                   ?? episodeObj.Value<string>("maxquality")
                                   ?? localQuality
                    });
                }
            }

            if (seasons.Count == 0)
                return null;

            if (strictVoices != null)
            {
                foreach (var voice in strictVoices)
                {
                    if (voice == null || string.IsNullOrWhiteSpace(voice.Link))
                        continue;

                    voices.Add(new VoiceEntry
                    {
                        Name = voice.Name,
                        Active = voice.Active,
                        Link = voice.Link,
                        Provider = voice.Provider
                    });
                }
            }

            var processedVoices = FilterAndSortVoices(voices, originalLanguage, currentTranslation);

            seasons.Sort((left, right) =>
            {
                int leftSeason = left.Number ?? int.MaxValue;
                int rightSeason = right.Number ?? int.MaxValue;
                int compare = leftSeason.CompareTo(rightSeason);
                if (compare != 0)
                    return compare;

                return string.Compare(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase);
            });

            var seasonTpl = string.IsNullOrWhiteSpace(localQuality)
                ? new SeasonTpl(seasons.Count)
                : new SeasonTpl(localQuality, seasons.Count);

            foreach (var season in seasons)
            {
                string displayName = string.IsNullOrWhiteSpace(season.Name)
                    ? (season.Number.HasValue ? $"{season.Number.Value} сезон" : season.Provider)
                    : season.Name;

                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = season.Provider;

                seasonTpl.Append(displayName, season.Link, season.Number?.ToString() ?? string.Empty);
            }

            var voiceTemplate = processedVoices.Count > 0 ? BuildVoiceTemplate(processedVoices) : (VoiceTpl?)null;
            var json = ParseTemplateJson(seasonTpl.ToJson(voiceTemplate));
            ApplySeasonMetadata(json, seasons, localQuality);
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
            string originalLanguage = GetOriginalLanguage(baseQuery);

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

                    if (!NormalizeEpisodeItem(episodeObj, result, strict: true, out var failureReason))
                    {
                        LogNormalizationFailure("episode", result, episodeObj, failureReason);
                        continue;
                    }

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

            var processedVoices = FilterAndSortVoices(voices, originalLanguage, currentTranslation);

            if (episodes.Count == 0)
            {
                if (ModInit.conf.enableEpisodeFallback)
                {
                    var fallback = TryBuildLenientEpisodePayload(providerResults, baseQuery, processedVoices, quality, baseTitle, currentTranslation, originalLanguage, requestedSeason);
                    if (fallback.HasValue)
                        return fallback.Value;
                }

                var voiceTpl = processedVoices.Count > 0 ? BuildVoiceTemplate(processedVoices) : (VoiceTpl?)null;
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

            var voiceTemplate = processedVoices.Count > 0 ? BuildVoiceTemplate(processedVoices) : (VoiceTpl?)null;
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
            ApplyEpisodeMetadata(json, episodes, quality);

            var html = BuildEpisodeHtml(episodeTpl, voiceTemplate);
            return new AggregatedPayload(json, html);
        }

        private AggregatedPayload? TryBuildLenientEpisodePayload(
            IEnumerable<ProviderFetchResult> providerResults,
            Dictionary<string, string> baseQuery,
            List<VoiceEntry> strictVoices,
            string quality,
            string baseTitle,
            string currentTranslation,
            string originalLanguage,
            int? requestedSeason)
        {
            var episodes = new List<EpisodeEntry>();
            var voices = new List<VoiceEntry>();
            var seenEpisodeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenVoiceLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string localQuality = quality;

            foreach (var result in providerResults)
            {
                if (!result.Success || result.Payload == null)
                    continue;

                localQuality ??= ExtractQuality(result.Payload);
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

                foreach (var token in ExtractEpisodeTokens(result.Payload))
                {
                    if (token is not JObject episodeObj)
                        continue;

                    if (!NormalizeEpisodeItem(episodeObj, result, strict: false, out var failureReason))
                    {
                        LogNormalizationFailure("episode-fallback", result, episodeObj, failureReason);
                        continue;
                    }

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
                return null;

            if (strictVoices != null)
            {
                foreach (var voice in strictVoices)
                {
                    if (voice == null || string.IsNullOrWhiteSpace(voice.Link))
                        continue;

                    voices.Add(new VoiceEntry
                    {
                        Name = voice.Name,
                        Active = voice.Active,
                        Link = voice.Link,
                        Provider = voice.Provider
                    });
                }
            }

            var processedVoices = FilterAndSortVoices(voices, originalLanguage, currentTranslation);

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

            var voiceTemplate = processedVoices.Count > 0 ? BuildVoiceTemplate(processedVoices) : (VoiceTpl?)null;
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
            ApplyEpisodeMetadata(json, episodes, localQuality);

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

        private List<VoiceEntry> FilterAndSortVoices(List<VoiceEntry> voices, string originalLanguage, string currentTranslation)
        {
            if (voices == null || voices.Count == 0)
                return new List<VoiceEntry>();

            var uniqueByLink = new Dictionary<string, VoiceEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var voice in voices)
            {
                if (voice == null || string.IsNullOrWhiteSpace(voice.Link))
                    continue;

                if (uniqueByLink.TryGetValue(voice.Link, out var existing))
                {
                    if (ComputeVoiceScore(voice, originalLanguage, currentTranslation) > ComputeVoiceScore(existing, originalLanguage, currentTranslation))
                        uniqueByLink[voice.Link] = voice;
                }
                else
                {
                    uniqueByLink[voice.Link] = voice;
                }
            }

            var filtered = uniqueByLink.Values.ToList();
            bool hasPreferred = filtered.Any(v => !IsVoiceUnwanted(v.Name));
            if (hasPreferred)
                filtered = filtered.Where(v => !IsVoiceUnwanted(v.Name)).ToList();

            var scored = filtered
                .Select(v => new
                {
                    Voice = v,
                    Score = ComputeVoiceScore(v, originalLanguage, currentTranslation),
                    NormalizedName = NormalizeVoiceKey(v.Name)
                })
                .OrderByDescending(v => v.Score)
                .ThenByDescending(v => v.Voice.Active)
                .ThenBy(v => v.NormalizedName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Voice.Provider, StringComparer.OrdinalIgnoreCase)
                .Select(v => new VoiceEntry
                {
                    Name = v.Voice.Name,
                    Active = v.Voice.Active,
                    Link = v.Voice.Link,
                    Provider = v.Voice.Provider
                })
                .ToList();

            if (scored.Count > 0 && !scored.Any(v => v.Active))
            {
                var first = scored[0];
                scored[0] = new VoiceEntry
                {
                    Name = first.Name,
                    Active = true,
                    Link = first.Link,
                    Provider = first.Provider
                };
            }

            return scored;
        }

        private static int ComputeVoiceScore(VoiceEntry voice, string originalLanguage, string currentTranslation)
        {
            if (voice == null)
                return int.MinValue;

            int score = 0;

            if (voice.Active)
                score += 200;

            if (!string.IsNullOrWhiteSpace(currentTranslation))
            {
                if (string.Equals(currentTranslation, voice.Name, StringComparison.OrdinalIgnoreCase))
                    score += 120;
                else if (string.Equals(currentTranslation, voice.Provider, StringComparison.OrdinalIgnoreCase))
                    score += 60;
            }

            score += GetVoiceLanguageAffinity(voice);

            if (IsPreferredVoiceName(voice.Name))
                score += 90;

            if (IsPreferredVoiceName(voice.Provider))
                score += 40;

            if (MatchesOriginalLanguage(voice.Name, originalLanguage))
                score += 35;

            if (MatchesOriginalLanguage(voice.Provider, originalLanguage))
                score += 15;

            if (IsVoiceUnwanted(voice.Name))
                score -= 120;

            if (string.IsNullOrWhiteSpace(voice.Name))
                score -= 10;

            if (!string.IsNullOrWhiteSpace(voice.Provider))
                score += 5;

            return score;
        }

        private static int GetVoiceLanguageAffinity(VoiceEntry voice)
        {
            if (voice == null)
                return 0;

            int affinity = 0;

            if (ContainsAnyKeyword(voice.Name, UkrainianVoiceKeywords) || ContainsAnyKeyword(voice.Provider, UkrainianVoiceKeywords))
                affinity = Math.Max(affinity, 80);

            if (ContainsAnyKeyword(voice.Name, RussianVoiceKeywords) || ContainsAnyKeyword(voice.Provider, RussianVoiceKeywords))
                affinity = Math.Max(affinity, 65);

            if (ContainsAnyKeyword(voice.Name, EnglishVoiceKeywords) || ContainsAnyKeyword(voice.Provider, EnglishVoiceKeywords))
                affinity = Math.Max(affinity, 45);

            return affinity;
        }

        private static bool MatchesOriginalLanguage(string value, string originalLanguage)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(originalLanguage))
                return false;

            string normalizedLanguage = originalLanguage.ToLowerInvariant();

            if (normalizedLanguage.StartsWith("uk") || normalizedLanguage.StartsWith("ua"))
                return ContainsAnyKeyword(value, UkrainianVoiceKeywords);

            if (normalizedLanguage.StartsWith("ru"))
                return ContainsAnyKeyword(value, RussianVoiceKeywords);

            if (normalizedLanguage.StartsWith("en"))
                return ContainsAnyKeyword(value, EnglishVoiceKeywords);

            return false;
        }

        private static bool IsPreferredVoiceName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string[] preferredKeywords =
            {
                "baibako",
                "muzhd",
                "dex",
                "coldfilm",
                "lostfilm",
                "newstudio"
            };

            return ContainsAnyKeyword(value, preferredKeywords);
        }

        private static bool ContainsAnyKeyword(string value, string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(value) || keywords == null || keywords.Length == 0)
                return false;

            string normalized = value.ToLowerInvariant();

            foreach (var keyword in keywords)
            {
                if (string.IsNullOrWhiteSpace(keyword))
                    continue;

                if (normalized.Contains(keyword))
                    return true;
            }

            return false;
        }

        private static bool IsVoiceUnwanted(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string normalized = value.ToLowerInvariant();
            string[] unwantedKeywords = { "trailer", "трейлер", "анонс", "promo", "караоке", "ost", "саундтрек", "кадр", "тизер" };

            return unwantedKeywords.Any(keyword => normalized.Contains(keyword));
        }

        private static string NormalizeVoiceKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string normalized = value.Trim().ToLowerInvariant();
            normalized = Regex.Replace(normalized, @"\s+", " ");
            return normalized;
        }

        private static string BuildEpisodeHtml(EpisodeTpl episodeTpl, VoiceTpl? voiceTpl)
        {
            string episodeHtml = episodeTpl.ToHtml() ?? string.Empty;
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

        private void LogNormalizationFailure(string stage, ProviderFetchResult source, JObject payload, string reason)
        {
            if (!(ModInit.conf.detailedLogging || ModInit.conf.logDroppedItems))
                return;

            try
            {
                string providerName = ResolveProviderName(source);
                string message = $"SmartFilter[{stage}]: пропущен элемент провайдера '{providerName}'";
                if (!string.IsNullOrWhiteSpace(reason))
                    message += $": {reason}";

                Console.WriteLine(message);

                if (ModInit.conf.logDroppedItems && payload != null)
                {
                    var snapshot = payload.DeepClone();
                    Console.WriteLine(snapshot.ToString(ModInit.conf.detailedLogging ? Formatting.Indented : Formatting.None));
                }
            }
            catch
            {
            }
        }

        private bool NormalizeEpisodeItem(JObject obj, ProviderFetchResult source, bool strict, out string failureReason)
        {
            failureReason = null;

            if (obj == null)
            {
                failureReason = "пустой объект";
                return false;
            }

            if (!EnsureUrl(obj, "url", "link", "stream", "file", "src", "iframe", "watch", "hls", "manifest"))
            {
                if (!strict)
                {
                    string candidate = TryFindAnyUrl(obj);
                    if (!string.IsNullOrWhiteSpace(candidate))
                        obj["url"] = candidate;
                }

                if (string.IsNullOrWhiteSpace(obj.Value<string>("url")))
                {
                    failureReason = "не удалось определить ссылку";
                    return false;
                }
            }

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

        private bool NormalizeEpisodeItem(JObject obj, ProviderFetchResult source)
            => NormalizeEpisodeItem(obj, source, strict: true, out _);

        private static string TryFindAnyUrl(JObject obj)
        {
            if (obj == null)
                return null;

            foreach (var property in obj.Properties())
            {
                if (property.Value == null || property.Value.Type == JTokenType.Null)
                    continue;

                if (property.Value.Type == JTokenType.String)
                {
                    string value = property.Value.ToString();
                    if (LooksLikeUrl(value))
                        return value;
                }

                if (property.Value is JObject nested)
                {
                    string nestedValue = TryFindAnyUrl(nested);
                    if (!string.IsNullOrWhiteSpace(nestedValue))
                        return nestedValue;
                }

                if (property.Value is JArray array)
                {
                    foreach (var item in array)
                    {
                        if (item is JObject nestedObj)
                        {
                            string nestedValue = TryFindAnyUrl(nestedObj);
                            if (!string.IsNullOrWhiteSpace(nestedValue))
                                return nestedValue;
                        }
                        else if (item.Type == JTokenType.String)
                        {
                            string value = item.ToString();
                            if (LooksLikeUrl(value))
                                return value;
                        }
                    }
                }
            }

            return null;
        }

        private static bool LooksLikeUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return value.Contains("://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("//")
                || value.StartsWith("/", StringComparison.Ordinal)
                || value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
        }

        private void ApplyAdapters(ProviderFetchResult result)
        {
            if (result?.Payload == null)
                return;

            NormalizeProviderPayload(result.Payload);

            if (result.Payload is JObject obj)
            {
                PromoteArray(obj, "playlist", "results");
                PromoteArray(obj, "playlists", "results");
                PromoteArray(obj, "items", "results");
                PromoteArray(obj, "list", "results");
                PromoteArray(obj, "folder", "data");
                PromoteArray(obj, "folders", "data");
                PromoteArray(obj, "children", "data");
            }
        }

        private static void PromoteArray(JObject obj, string sourceKey, string targetKey)
        {
            if (obj == null || string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(targetKey))
                return;

            if (obj.TryGetValue(targetKey, out var existing) && existing is JArray)
                return;

            if (!obj.TryGetValue(sourceKey, out var token) || token == null)
                return;

            if (token is JArray array)
            {
                obj[targetKey] = array;
            }
            else if (token is JObject nested && ShouldConvertToArray(nested))
            {
                obj[targetKey] = NormalizeDictionaryToArray(nested);
            }
        }

        private static void NormalizeProviderPayload(JToken token)
        {
            if (token == null)
                return;

            if (token is JObject obj)
            {
                foreach (var property in obj.Properties().ToList())
                {
                    NormalizeProviderPayload(property.Value);

                    if (property.Value is JObject nested && ShouldConvertToArray(nested))
                        obj[property.Name] = NormalizeDictionaryToArray(nested);
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array)
                    NormalizeProviderPayload(item);
            }
        }

        private static bool ShouldConvertToArray(JObject obj)
        {
            if (obj == null)
                return false;

            int count = 0;
            int convertible = 0;

            foreach (var property in obj.Properties())
            {
                count++;
                if (string.IsNullOrWhiteSpace(property.Name))
                    return false;

                if (Regex.IsMatch(property.Name, "^\\d+"))
                {
                    convertible++;
                    continue;
                }

                if (property.Value is JObject || property.Value is JArray || property.Value.Type == JTokenType.Null)
                {
                    convertible++;
                    continue;
                }

                return false;
            }

            return count > 0 && convertible == count;
        }

        private static JArray NormalizeDictionaryToArray(JObject obj)
        {
            var array = new JArray();

            foreach (var property in obj.Properties().OrderBy(p => ParseLeadingInt(p.Name)).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                array.Add(property.Value);
            }

            return array;
        }

        private static int ParseLeadingInt(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return int.MaxValue;

            var match = Regex.Match(value, "\\d+");
            if (match.Success && int.TryParse(match.Value, out var parsed))
                return parsed;

            return int.MaxValue;
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
            if (payload is JArray array)
            {
                foreach (var item in array)
                {
                    string nested = ExtractQuality(item);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }

                return null;
            }

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
                else if (property.Value is JArray nestedArray)
                {
                    string nestedQuality = ExtractQuality(nestedArray);
                    if (!string.IsNullOrWhiteSpace(nestedQuality))
                        return nestedQuality;
                }
            }

            return null;
        }

        private static void ApplySeasonMetadata(JToken json, IReadOnlyList<SeasonEntry> seasons, string fallbackQuality)
        {
            if (json is not JObject root || seasons == null || seasons.Count == 0)
                return;

            if (string.IsNullOrWhiteSpace(root.Value<string>("maxquality")) && !string.IsNullOrWhiteSpace(fallbackQuality))
                root["maxquality"] = fallbackQuality;

            if (root.TryGetValue("data", out var dataToken) && dataToken is JArray dataArray)
            {
                for (int index = 0; index < dataArray.Count && index < seasons.Count; index++)
                {
                    if (dataArray[index] is not JObject item)
                        continue;

                    var season = seasons[index];

                    if (!string.IsNullOrWhiteSpace(season.Provider))
                    {
                        item["provider"] ??= season.Provider;
                        item["details"] ??= season.Provider;
                    }

                    string quality = season.Quality;
                    if (string.IsNullOrWhiteSpace(quality))
                        quality = fallbackQuality;

                    if (!string.IsNullOrWhiteSpace(quality))
                    {
                        if (string.IsNullOrWhiteSpace(item.Value<string>("maxquality")))
                            item["maxquality"] = quality;

                        item["smartfilterQuality"] = quality;
                    }
                }
            }
        }

        private static void ApplyEpisodeMetadata(JToken json, IReadOnlyList<EpisodeEntry> episodes, string fallbackQuality)
        {
            if (json is not JObject root || episodes == null || episodes.Count == 0)
                return;

            if (string.IsNullOrWhiteSpace(root.Value<string>("maxquality")) && !string.IsNullOrWhiteSpace(fallbackQuality))
                root["maxquality"] = fallbackQuality;

            if (root.TryGetValue("data", out var dataToken) && dataToken is JArray dataArray)
            {
                for (int index = 0; index < dataArray.Count && index < episodes.Count; index++)
                {
                    if (dataArray[index] is not JObject item)
                        continue;

                    var episode = episodes[index];

                    if (!string.IsNullOrWhiteSpace(episode.Provider))
                    {
                        item["provider"] ??= episode.Provider;
                        item["details"] ??= episode.Provider;
                    }

                    if (!string.IsNullOrWhiteSpace(episode.Translation))
                    {
                        string voice = episode.Translation;
                        item["translate"] ??= voice;
                        item["voice"] ??= voice;
                        item["voice_name"] ??= voice;
                        item["smartfilterVoice"] = voice;
                    }

                    string quality = !string.IsNullOrWhiteSpace(episode.Quality) ? episode.Quality : fallbackQuality;
                    if (!string.IsNullOrWhiteSpace(quality))
                    {
                        if (string.IsNullOrWhiteSpace(item.Value<string>("maxquality")))
                            item["maxquality"] = quality;

                        item["smartfilterQuality"] = quality;
                    }
                }
            }
        }

        private static string NormalizeContentType(string contentType, int serial, int requestedSeason)
        {
            if (string.IsNullOrWhiteSpace(contentType))
                return null;

            string normalized = contentType.Trim().ToLowerInvariant();

            return normalized switch
            {
                "movie" or "season" or "episode" or "similar" => normalized,
                "seasons" => "season",
                "episodes" or "files" or "items" => "episode",
                "playlist" or "playlists" or "folder" or "folders" or "serial" =>
                    requestedSeason > 0 ? "episode" : "season",
                "voice" or "voices" => requestedSeason > 0 ? "episode" : "season",
                _ => normalized
            };
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
            {
                string seasonValue = seasonNumber.Value.ToString();
                query["s"] = seasonValue;
                query["season"] = seasonValue;
                query["season_number"] = seasonValue;
            }
            else
            {
                query.Remove("s");
                query.Remove("season");
                query.Remove("season_number");
            }

            if (!string.IsNullOrWhiteSpace(translation))
            {
                query["t"] = translation;
                query["translate"] = translation;
                query["translation"] = translation;
                query["voice"] = translation;
            }
            else
            {
                query.Remove("t");
                query.Remove("translate");
                query.Remove("translation");
                query.Remove("voice");
            }

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

        private static string GetOriginalLanguage(Dictionary<string, string> baseQuery)
        {
            if (baseQuery != null && baseQuery.TryGetValue("original_language", out var language) && !string.IsNullOrWhiteSpace(language))
                return language;

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

            bool expectingSeason = string.Equals(expectedType, "season", StringComparison.OrdinalIgnoreCase);
            bool expectingEpisode = string.Equals(expectedType, "episode", StringComparison.OrdinalIgnoreCase);

            if (expectingSeason)
            {
                var seasonCandidates = ExtractSeasonTokens(payload).ToList();
                if (seasonCandidates.Count > 0)
                {
                    foreach (var season in seasonCandidates)
                        yield return season.DeepClone();
                    yield break;
                }
            }

            if (expectingEpisode)
            {
                var episodeCandidates = ExtractEpisodeTokens(payload).ToList();
                if (episodeCandidates.Count > 0)
                {
                    foreach (var episode in episodeCandidates)
                        yield return episode.DeepClone();
                    yield break;
                }
            }

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

                if (expectingEpisode && obj.TryGetValue("episodes", out var episodesToken))
                {
                    foreach (var item in EnumerateArrayItems(episodesToken))
                        yield return item.DeepClone();
                    yield break;
                }

                if (expectingSeason)
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

        private static IEnumerable<JObject> ExtractSeasonTokens(JToken token)
        {
            if (token == null)
                yield break;

            if (token is JArray array)
            {
                foreach (var item in array)
                {
                    foreach (var season in ExtractSeasonTokens(item))
                        yield return season;
                }
                yield break;
            }

            if (token is JObject obj)
            {
                if (LooksLikeSeason(obj))
                    yield return obj;

                foreach (var property in obj.Properties())
                {
                    if (property.Value == null || property.Value.Type == JTokenType.Null)
                        continue;

                    foreach (var season in ExtractSeasonTokens(property.Value))
                        yield return season;
                }
            }
        }

        private static IEnumerable<JObject> ExtractEpisodeTokens(JToken token)
        {
            if (token == null)
                yield break;

            if (token is JArray array)
            {
                foreach (var item in array)
                {
                    foreach (var episode in ExtractEpisodeTokens(item))
                        yield return episode;
                }
                yield break;
            }

            if (token is JObject obj)
            {
                if (LooksLikeEpisode(obj))
                {
                    yield return obj;
                    yield break;
                }

                foreach (var property in obj.Properties())
                {
                    if (property.Value == null || property.Value.Type == JTokenType.Null)
                        continue;

                    foreach (var episode in ExtractEpisodeTokens(property.Value))
                        yield return episode;
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

        private static bool LooksLikeSeason(JObject obj)
        {
            if (obj == null)
                return false;

            if (obj.TryGetValue("type", out var typeToken))
            {
                var typeValue = typeToken?.ToString();
                if (!string.IsNullOrWhiteSpace(typeValue) && typeValue.IndexOf("season", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            if (obj.TryGetValue("folder", out var folderToken) && ContainsEpisodeCandidate(folderToken))
                return true;

            if (obj.TryGetValue("playlist", out var playlistToken) && ContainsEpisodeCandidate(playlistToken))
                return true;

            if (obj.TryGetValue("seasons", out var seasonsToken) && seasonsToken is JArray seasonsArray && seasonsArray.Count > 0)
                return true;

            if (HasAnyProperty(obj, "season", "s") && !HasAnyProperty(obj, "file", "stream", "src", "url", "link"))
                return true;

            string title = obj.Value<string>("title") ?? obj.Value<string>("name");
            if (!string.IsNullOrWhiteSpace(title) && Regex.IsMatch(title, "(сезон|season)", RegexOptions.IgnoreCase))
                return true;

            return false;
        }

        private static bool LooksLikeEpisode(JObject obj)
        {
            if (obj == null)
                return false;

            if (obj.TryGetValue("folder", out _) || obj.TryGetValue("playlist", out _))
                return false;

            if (!HasAnyProperty(obj, "file", "stream", "src", "hls", "url", "link", "manifest", "mpd"))
                return false;

            if (HasAnyProperty(obj, "voice", "voices", "translations") && !HasAnyProperty(obj, "episode", "e", "name", "title"))
                return false;

            if (HasAnyProperty(obj, "episode", "e", "serie", "series"))
                return true;

            if (HasAnyProperty(obj, "season", "s"))
                return true;

            string title = obj.Value<string>("title") ?? obj.Value<string>("name");
            if (!string.IsNullOrWhiteSpace(title) && Regex.IsMatch(title, "\\d"))
                return true;

            return false;
        }

        private static bool ContainsEpisodeCandidate(JToken token)
        {
            if (token == null)
                return false;

            return ExtractEpisodeTokens(token).Any();
        }

        private static bool HasAnyProperty(JObject obj, params string[] propertyNames)
        {
            if (obj == null || propertyNames == null)
                return false;

            foreach (var name in propertyNames)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (obj.TryGetValue(name, out var token) && token != null && token.Type != JTokenType.Null && !string.IsNullOrWhiteSpace(token.ToString()))
                    return true;
            }

            return false;
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
                }
            }
            else if (requestedSeason <= 0)
            {
                query.Remove("s");
                query.Remove("season");
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

        private IEnumerable<JObject> BuildNormalizedItems(ProviderFetchResult result, string expectedType, string fallbackTitle, string fallbackOriginalTitle, int fallbackYear)
        {
            if (result?.Payload == null)
                yield break;

            string providerName = ResolveProviderName(result);
            string defaultTitle = !string.IsNullOrWhiteSpace(fallbackTitle) ? fallbackTitle : fallbackOriginalTitle;

            foreach (var token in ExtractItems(result.Payload, expectedType))
            {
                if (token is not JObject obj)
                    continue;

                var normalized = (JObject)obj.DeepClone();

                if (!NormalizeEpisodeItem(normalized, result, strict: false, out _))
                {
                    if (!EnsureUrl(normalized, "url", "link", "stream", "file", "src", "iframe", "watch", "hls", "manifest"))
                        continue;
                }

                string url = normalized.Value<string>("url");
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                string title = ExtractItemTitle(normalized, defaultTitle, providerName);
                int? season = ExtractFirstInt(normalized, "season", "s", "season_number", "seasonNumber");
                int? episode = ExtractFirstInt(normalized, "episode", "e", "episode_number", "episodeNumber");
                string voiceRaw = ExtractVoiceLabel(normalized, providerName);
                string qualityRaw = ExtractQualityCandidate(normalized, result.Payload);

                var normalization = NormalizationStore.Instance;
                var quality = normalization.NormalizeQuality(qualityRaw);
                var voice = normalization.NormalizeVoice(voiceRaw);

                string qualityCode = !string.IsNullOrEmpty(quality.Code) ? quality.Code : SanitizeKey(qualityRaw);
                string qualityLabel = !string.IsNullOrEmpty(quality.Label) ? quality.Label : (qualityRaw ?? string.Empty);
                string voiceCode = !string.IsNullOrEmpty(voice.Code) ? voice.Code : SanitizeKey(voiceRaw);
                string voiceLabel = !string.IsNullOrEmpty(voice.Label) ? voice.Label : (voiceRaw ?? string.Empty);

                int? year = normalized.Value<int?>("year") ?? (fallbackYear > 0 ? fallbackYear : (int?)null);

                string key = BuildItemKey(title, year, season, episode, voiceCode, qualityCode);

                var item = new JObject
                {
                    ["id"] = key,
                    ["title"] = title ?? string.Empty,
                    ["season"] = season.HasValue ? new JValue(season.Value) : JValue.CreateNull(),
                    ["episode"] = episode.HasValue ? new JValue(episode.Value) : JValue.CreateNull(),
                    ["source"] = "SmartFilter",
                    ["quality_code"] = qualityCode ?? string.Empty,
                    ["quality_label"] = qualityLabel ?? string.Empty,
                    ["voice_code"] = voiceCode ?? string.Empty,
                    ["voice_label"] = voiceLabel ?? string.Empty,
                    ["url"] = url,
                    ["is_camrip"] = DetermineCamripFlag(qualityCode, qualityLabel, normalized),
                    ["is_hdr"] = DetermineHdrFlag(normalized),
                    ["size_mb"] = ParseSizeMb(normalized.Value<string>("size") ?? normalized.Value<string>("size_mb") ?? normalized.Value<string>("filesize")),
                    ["duration"] = ParseDurationSeconds(normalized.Value<string>("duration") ?? normalized.Value<string>("time") ?? normalized.Value<string>("length")),
                    ["poster"] = ExtractPoster(normalized),
                    ["year"] = year.HasValue ? new JValue(year.Value) : JValue.CreateNull()
                };

                var alternative = new JObject
                {
                    ["provider"] = providerName,
                    ["plugin"] = result.ProviderPlugin ?? string.Empty,
                    ["url"] = url,
                    ["quality_label"] = qualityLabel ?? string.Empty,
                    ["quality_code"] = qualityCode ?? string.Empty,
                    ["voice_label"] = voiceLabel ?? string.Empty,
                    ["voice_code"] = voiceCode ?? string.Empty
                };

                if (normalized.TryGetValue("headers", out var headersToken))
                    alternative["headers"] = headersToken.DeepClone();

                if (normalized.TryGetValue("translation", out var translationToken))
                    alternative["translation"] = translationToken.DeepClone();
                else if (!string.IsNullOrWhiteSpace(voiceRaw))
                    alternative["translation"] = voiceRaw;

                if (normalized.TryGetValue("quality_list", out var qualityListToken))
                    alternative["quality_list"] = qualityListToken.DeepClone();

                if (normalized.TryGetValue("streams", out var streamsToken))
                    alternative["streams"] = streamsToken.DeepClone();

                if (normalized.TryGetValue("files", out var filesToken))
                    alternative["files"] = filesToken.DeepClone();

                if (normalized.TryGetValue("manifest", out var manifestToken))
                    alternative["manifest"] = manifestToken.DeepClone();

                item["alternatives"] = new JArray { alternative };

                yield return item;
            }
        }

        private static bool MergeNormalizedItem(JObject target, JObject source)
        {
            if (target == null || source == null)
                return false;

            bool changed = false;
            var targetAlt = (JArray)(target["alternatives"] ?? new JArray());
            target["alternatives"] = targetAlt;

            var existingUrls = new HashSet<string>(targetAlt.OfType<JObject>().Select(o => o.Value<string>("url") ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            var sourceAlt = source["alternatives"] as JArray;
            if (sourceAlt != null)
            {
                foreach (var alt in sourceAlt.OfType<JObject>())
                {
                    string url = alt.Value<string>("url") ?? string.Empty;
                    if (existingUrls.Add(url))
                    {
                        targetAlt.Add(alt.DeepClone());
                        changed = true;
                        PromoteBestQuality(target, alt);
                    }
                    else
                    {
                        PromoteBestQuality(target, alt);
                    }
                }
            }

            return changed;
        }

        private static void PromoteBestQuality(JObject target, JObject candidate)
        {
            if (target == null || candidate == null)
                return;

            string existingQuality = target.Value<string>("quality_code") ?? string.Empty;
            string candidateQuality = candidate.Value<string>("quality_code") ?? string.Empty;

            int existingScore = ScoreQuality(existingQuality);
            int candidateScore = ScoreQuality(candidateQuality);

            if (candidateScore > existingScore)
            {
                target["quality_code"] = candidateQuality;
                target["quality_label"] = candidate.Value<string>("quality_label") ?? candidateQuality;
                target["url"] = candidate.Value<string>("url") ?? target.Value<string>("url");
                target["voice_code"] = candidate.Value<string>("voice_code") ?? target.Value<string>("voice_code");
                target["voice_label"] = candidate.Value<string>("voice_label") ?? target.Value<string>("voice_label");
            }
        }

        private static AggregationMetadata BuildMetadata(JArray items)
        {
            var metadata = new AggregationMetadata();
            if (items == null)
                return metadata;

            metadata.TotalItems = items.Count;

            var qualityCounts = new Dictionary<string, AggregationFacet>(StringComparer.OrdinalIgnoreCase);
            var voiceCounts = new Dictionary<string, AggregationFacet>(StringComparer.OrdinalIgnoreCase);

            foreach (var token in items.OfType<JObject>())
            {
                string qualityCode = token.Value<string>("quality_code") ?? string.Empty;
                string qualityLabel = token.Value<string>("quality_label") ?? string.Empty;
                if (!qualityCounts.TryGetValue(qualityCode, out var qualityFacet))
                {
                    qualityFacet = new AggregationFacet { Code = qualityCode, Label = string.IsNullOrEmpty(qualityLabel) ? "-" : qualityLabel, Count = 0 };
                    qualityCounts[qualityCode] = qualityFacet;
                }
                qualityFacet.Count++;

                string voiceCode = token.Value<string>("voice_code") ?? string.Empty;
                string voiceLabel = token.Value<string>("voice_label") ?? string.Empty;
                if (!voiceCounts.TryGetValue(voiceCode, out var voiceFacet))
                {
                    voiceFacet = new AggregationFacet { Code = voiceCode, Label = string.IsNullOrEmpty(voiceLabel) ? "-" : voiceLabel, Count = 0 };
                    voiceCounts[voiceCode] = voiceFacet;
                }
                voiceFacet.Count++;
            }

            metadata.Qualities = qualityCounts;
            metadata.Voices = voiceCounts;
            return metadata;
        }

        private static string ExtractItemTitle(JObject obj, string fallbackTitle, string providerName)
        {
            string[] keys = { "title", "name", "original_title", "originalTitle", "label" };
            foreach (var key in keys)
            {
                if (!obj.TryGetValue(key, out var token) || token == null)
                    continue;

                var value = token.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return fallbackTitle ?? providerName ?? string.Empty;
        }

        private static int? ExtractFirstInt(JObject obj, params string[] keys)
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

        private static string ExtractVoiceLabel(JObject obj, string providerName)
        {
            string[] keys =
            {
                "voice", "voice_name", "voiceName", "translator", "translation", "author", "dub", "voice_label", "voiceLabel", "voice_title"
            };

            foreach (var key in keys)
            {
                if (!obj.TryGetValue(key, out var token) || token == null)
                    continue;

                var value = token.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            if (obj.TryGetValue("details", out var detailsToken))
            {
                var details = detailsToken.ToString();
                if (!string.IsNullOrWhiteSpace(details) && !string.Equals(details, providerName, StringComparison.OrdinalIgnoreCase))
                    return details.Trim();
            }

            return providerName;
        }

        private static string ExtractQualityCandidate(JObject obj, JToken payload)
        {
            string[] keys =
            {
                "quality", "quality_full", "quality_name", "qualityName", "maxquality", "maxQuality", "resolution", "video_quality"
            };

            foreach (var key in keys)
            {
                if (!obj.TryGetValue(key, out var token) || token == null)
                    continue;

                var value = token.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            if (payload != null)
            {
                var fallback = ExtractQuality(payload);
                if (!string.IsNullOrWhiteSpace(fallback))
                    return fallback;
            }

            return null;
        }

        private static string BuildItemKey(string title, int? year, int? season, int? episode, string voiceCode, string qualityCode)
        {
            string normalizedTitle = SanitizeKey(title);
            string normalizedVoice = SanitizeKey(voiceCode);
            string normalizedQuality = SanitizeKey(qualityCode);

            return string.Join("|", new[]
            {
                normalizedTitle,
                year.HasValue ? year.Value.ToString() : string.Empty,
                season.HasValue ? season.Value.ToString() : string.Empty,
                episode.HasValue ? episode.Value.ToString() : string.Empty,
                normalizedVoice,
                normalizedQuality
            });
        }

        private static string SanitizeKey(string value)
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

        private static bool DetermineCamripFlag(string qualityCode, string qualityLabel, JObject obj)
        {
            if (!string.IsNullOrWhiteSpace(qualityCode) && qualityCode.Contains("cam", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrWhiteSpace(qualityLabel) && qualityLabel.Contains("cam", StringComparison.OrdinalIgnoreCase))
                return true;

            if (obj != null)
            {
                if (obj.TryGetValue("camrip", out var camToken) && camToken.Type == JTokenType.Boolean)
                    return camToken.Value<bool>();
            }

            return false;
        }

        private static bool DetermineHdrFlag(JObject obj)
        {
            if (obj == null)
                return false;

            if (obj.TryGetValue("hdr", out var hdrToken) && hdrToken.Type == JTokenType.Boolean)
                return hdrToken.Value<bool>();

            foreach (var key in new[] { "quality", "quality_full", "quality_name" })
            {
                if (!obj.TryGetValue(key, out var token) || token == null)
                    continue;

                var value = token.ToString();
                if (value.Contains("hdr", StringComparison.OrdinalIgnoreCase) || value.Contains("dolby", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static double? ParseSizeMb(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim();

            double multiplier = 1.0;
            if (value.EndsWith("gb", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1024.0;
                value = value[..^2];
            }
            else if (value.EndsWith("mb", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1.0;
                value = value[..^2];
            }

            if (double.TryParse(value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                return Math.Round(parsed * multiplier, 2);

            return null;
        }

        private static int? ParseDurationSeconds(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim();
            if (TimeSpan.TryParse(value, out var span))
                return (int)span.TotalSeconds;

            if (int.TryParse(value, out int seconds))
                return seconds;

            return null;
        }

        private static string ExtractPoster(JObject obj)
        {
            string[] keys = { "poster", "img", "image", "cover", "thumbnail" };
            foreach (var key in keys)
            {
                if (!obj.TryGetValue(key, out var token) || token == null)
                    continue;

                var value = token.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        private static int ScoreQuality(string qualityCode)
        {
            if (string.IsNullOrWhiteSpace(qualityCode))
                return -1;

            return qualityCode.ToLowerInvariant() switch
            {
                "2160p" => 6,
                "1440p" => 5,
                "1080p" => 4,
                "720p" => 3,
                "480p" => 2,
                "360p" => 1,
                "camrip" => 0,
                _ => 0
            };
        }
    }
}
