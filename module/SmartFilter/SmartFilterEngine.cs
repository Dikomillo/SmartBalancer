using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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

        public async Task<AggregationResult> AggregateProvidersAsync(string imdbId, long kinopoiskId, string title, string originalTitle, int year, int serial, string originalLanguage, int requestedSeason, string progressKey)
        {
            var baseQuery = BuildBaseQueryParameters(imdbId, kinopoiskId, title, originalTitle, year, serial, originalLanguage);
            var providers = await GetActiveProvidersAsync(baseQuery, serial);

            var aggregation = new AggregationResult
            {
                Type = DetermineDefaultType(serial, requestedSeason),
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
                aggregation.Type = DetermineContentType(successful, serial, requestedSeason, aggregation.Type);

            aggregation.Data = MergePayloads(successful, aggregation.Type);
            aggregation.Providers = results.Select(r => r.ToStatus()).OrderBy(p => p.Index ?? int.MaxValue).ThenBy(p => p.Name).ToList();

            SmartFilterProgress.PublishFinal(memoryCache, progressKey, aggregation.Providers);
            return aggregation;
        }

        private async Task<List<ProviderDescriptor>> GetActiveProvidersAsync(Dictionary<string, string> baseQuery, int serial)
        {
            var providers = new List<ProviderDescriptor>();

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

                    if (serial != 1 && IsAnimeProvider(name))
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

                    if (obj.TryGetValue("data", out var dataToken) && dataToken is JArray dataArray)
                        itemsCount = dataArray.Count;
                    else if (obj.TryGetValue("results", out var resultsToken) && resultsToken is JArray resultsArray)
                        itemsCount = resultsArray.Count;

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

        private string DetermineContentType(IEnumerable<ProviderFetchResult> results, int serial, int requestedSeason, string fallback)
        {
            foreach (var result in results)
            {
                if (!string.IsNullOrWhiteSpace(result.ContentType))
                    return result.ContentType;
            }

            return fallback ?? DetermineDefaultType(serial, requestedSeason);
        }

        private static string DetermineDefaultType(int serial, int requestedSeason)
        {
            if (serial == 1)
                return requestedSeason > 0 ? "episode" : "season";

            return "movie";
        }

        private JArray MergePayloads(IEnumerable<ProviderFetchResult> results, string expectedType)
        {
            var aggregated = new JArray();

            foreach (var result in results)
            {
                foreach (var item in ExtractItems(result.Payload, expectedType))
                {
                    if (item is JObject obj)
                    {
                        if (string.IsNullOrEmpty(obj.Value<string>("provider")))
                            obj["provider"] = result.ProviderName;

                        if (string.IsNullOrEmpty(obj.Value<string>("balanser")) && !string.IsNullOrEmpty(result.ProviderPlugin))
                            obj["balanser"] = result.ProviderPlugin;
                    }

                    aggregated.Add(item);
                }
            }

            return aggregated;
        }

        private IEnumerable<JToken> ExtractItems(JToken payload, string expectedType)
        {
            if (payload == null)
                yield break;

            if (payload is JObject obj)
            {
                if (obj.TryGetValue("data", out var dataToken) && dataToken is JArray dataArray)
                {
                    foreach (var item in dataArray)
                        yield return item.DeepClone();
                    yield break;
                }

                if (obj.TryGetValue("results", out var resultsToken) && resultsToken is JArray resultsArray)
                {
                    foreach (var item in resultsArray)
                        yield return item.DeepClone();
                    yield break;
                }

                if (expectedType == "episode" && obj.TryGetValue("episodes", out var episodesToken) && episodesToken is JArray episodesArray)
                {
                    foreach (var item in episodesArray)
                        yield return item.DeepClone();
                    yield break;
                }
            }
            else if (payload is JArray array)
            {
                foreach (var item in array)
                    yield return item.DeepClone();
            }
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

        internal static string BuildCacheKey(Dictionary<string, string> query)
        {
            var normalized = query
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Key}={kv.Value}");

            return "smartfilter:" + string.Join("&", normalized);
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
