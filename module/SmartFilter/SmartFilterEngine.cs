using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared.Models;
using Shared.Models.Templates;
using System;
using System.Net.Http;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace SmartFilter
{
    public class SmartFilterEngine
    {
        private readonly string host;
        private readonly HttpContext httpContext;
        private readonly SemaphoreSlim semaphore;

        public SmartFilterEngine(string hostUrl, HttpContext context)
        {
            host = hostUrl;
            httpContext = context;
            semaphore = new SemaphoreSlim(ModInit.conf.maxParallelRequests, ModInit.conf.maxParallelRequests);
        }

        public async Task<List<ProviderResult>> AggregateProvidersAsync(
            long id,
            string imdb_id,
            long kinopoisk_id,
            string title,
            string original_title,
            int year,
            int serial,
            string original_language,
            string source,
            string rchtype,
            bool life,
            bool islite,
            string account_email,
            string uid,
            string token)
        {
            Console.WriteLine($"🔍 SmartFilter: Starting aggregation for '{title}' ({year})");

            var accessToken = !string.IsNullOrWhiteSpace(token) ? token : ResolveToken();
            var providers = await GetActiveProvidersAsync(
                id,
                imdb_id,
                kinopoisk_id,
                title,
                original_title,
                year,
                serial,
                original_language,
                source,
                rchtype,
                life,
                islite,
                account_email,
                uid,
                accessToken);
            if (providers.Count == 0)
            {
                Console.WriteLine($"⚠️ SmartFilter: No active providers found for '{title}'");
                return new List<ProviderResult>();
            }

            Console.WriteLine($"📡 SmartFilter: Found {providers.Count} active providers for '{title}'");

            var aggregatedResults = new List<ProviderResult>();
            var tasks = providers.Select(async provider =>
            {
                var result = await FetchProviderTemplateAsync(
                    provider,
                    id,
                    imdb_id,
                    kinopoisk_id,
                    title,
                    original_title,
                    year,
                    serial,
                    original_language,
                    source,
                    rchtype,
                    life,
                    islite,
                    account_email,
                    uid,
                    accessToken);

                if (result != null)
                {
                    lock (aggregatedResults)
                    {
                        aggregatedResults.Add(result);
                    }
                }
            });

            await Task.WhenAll(tasks);

            Console.WriteLine($"✅ SmartFilter: Aggregated {aggregatedResults.Count} provider results for '{title}'");
            return aggregatedResults;
        }

        private async Task<List<(string name, string url)>> GetActiveProvidersAsync(
            long id,
            string imdb_id,
            long kinopoisk_id,
            string title,
            string original_title,
            int year,
            int serial,
            string original_language,
            string source,
            string rchtype,
            bool life,
            bool islite,
            string account_email,
            string uid,
            string accessToken)
        {
            var providers = new List<(string name, string url)>();

            try
            {
                var queryParams = new List<string>();
                var exclude = ModInit.conf.excludeProviders ?? Array.Empty<string>();
                var includeOnly = ModInit.conf.includeOnlyProviders ?? Array.Empty<string>();
                if (id > 0) queryParams.Add($"id={id}");
                if (!string.IsNullOrEmpty(imdb_id)) queryParams.Add($"imdb_id={Uri.EscapeDataString(imdb_id)}");
                if (kinopoisk_id > 0) queryParams.Add($"kinopoisk_id={kinopoisk_id}");
                if (!string.IsNullOrEmpty(title)) queryParams.Add($"title={Uri.EscapeDataString(title)}");
                if (!string.IsNullOrEmpty(original_title)) queryParams.Add($"original_title={Uri.EscapeDataString(original_title)}");
                if (year > 0) queryParams.Add($"year={year}");
                if (serial >= 0) queryParams.Add($"serial={serial}");
                if (!string.IsNullOrEmpty(original_language)) queryParams.Add($"original_language={Uri.EscapeDataString(original_language)}");
                if (!string.IsNullOrEmpty(source)) queryParams.Add($"source={Uri.EscapeDataString(source)}");
                if (!string.IsNullOrEmpty(rchtype)) queryParams.Add($"rchtype={Uri.EscapeDataString(rchtype)}");
                if (life) queryParams.Add("life=true");
                if (islite) queryParams.Add("islite=true");
                if (!string.IsNullOrEmpty(account_email)) queryParams.Add($"account_email={Uri.EscapeDataString(account_email)}");
                if (!string.IsNullOrEmpty(uid)) queryParams.Add($"uid={Uri.EscapeDataString(uid)}");
                if (!string.IsNullOrEmpty(accessToken)) queryParams.Add($"token={Uri.EscapeDataString(accessToken)}");

                string queryString = string.Join("&", queryParams.Where(p => !string.IsNullOrEmpty(p)));
                string eventsUrl = string.IsNullOrEmpty(queryString)
                    ? $"{host}/lite/events"
                    : $"{host}/lite/events?{queryString}";
                Console.WriteLine($"🌐 SmartFilter: Fetching providers from: {eventsUrl}");

                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(ModInit.conf.requestTimeoutSeconds > 0 ? ModInit.conf.requestTimeoutSeconds : 40);

                var response = await httpClient.GetStringAsync(eventsUrl);
                if (!string.IsNullOrEmpty(response))
                {
                    var providerArray = JsonConvert.DeserializeObject<dynamic[]>(response);
                    if (providerArray != null)
                    {
                        foreach (var provider in providerArray)
                        {
                            string name = provider.name?.ToString();
                            string url = provider.url?.ToString();

                            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
                                continue;

                            // Исключаем самого себя
                            if (name == "SmartFilter Aggregator")
                                continue;

                            // Фильтруем аниме-провайдеры для фильмов
                            if (serial != 1 && IsAnimeProvider(name))
                            {
                                Console.WriteLine($"⏭️ SmartFilter: Skipping anime provider '{name}' for movie content");
                                continue;
                            }

                            // Проверяем исключения
                            if (exclude.Contains(name))
                            {
                                Console.WriteLine($"⏭️ SmartFilter: Excluding provider '{name}' (in excludeProviders)");
                                continue;
                            }

                            // Проверяем включения (если список не пустой)
                            if (includeOnly.Length > 0 && !includeOnly.Contains(name))
                            {
                                Console.WriteLine($"⏭️ SmartFilter: Skipping provider '{name}' (not in includeOnlyProviders)");
                                continue;
                            }

                            providers.Add((name, url));
                            Console.WriteLine($"✅ SmartFilter: Added provider '{name}': {url}");
                        }
                    }
                }

                Console.WriteLine($"📊 SmartFilter: Total active providers: {providers.Count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ SmartFilter: Error getting active providers: {ex.Message}");
            }

            return providers;
        }

        private bool IsAnimeProvider(string providerName)
        {
            var animeProviders = new[] { "AniLiberty", "AnimeLib", "AniMedia", "AnimeGo", "Animevost", "Animebesst", "MoonAnime" };
            return animeProviders.Contains(providerName);
        }

        private async Task<ProviderResult> FetchProviderTemplateAsync(
            (string name, string url) provider,
            long id,
            string imdb_id,
            long kinopoisk_id,
            string title,
            string original_title,
            int year,
            int serial,
            string original_language,
            string source,
            string rchtype,
            bool life,
            bool islite,
            string account_email,
            string uid,
            string accessToken)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await semaphore.WaitAsync();
            string finalUrl = provider.url;

            try
            {
                var queryParams = new List<string> { "rjson=true" };
                if (id > 0 && !ContainsParameter(provider.url, "id")) queryParams.Add($"id={id}");
                if (!string.IsNullOrEmpty(imdb_id)) queryParams.Add($"imdb_id={Uri.EscapeDataString(imdb_id)}");
                if (kinopoisk_id > 0) queryParams.Add($"kinopoisk_id={kinopoisk_id}");
                if (!string.IsNullOrEmpty(title)) queryParams.Add($"title={Uri.EscapeDataString(title)}");
                if (!string.IsNullOrEmpty(original_title)) queryParams.Add($"original_title={Uri.EscapeDataString(original_title)}");
                if (year > 0) queryParams.Add($"year={year}");
                if (serial >= 0) queryParams.Add($"serial={serial}");
                if (!string.IsNullOrEmpty(original_language)) queryParams.Add($"original_language={Uri.EscapeDataString(original_language)}");
                if (!string.IsNullOrEmpty(source) && !ContainsParameter(provider.url, "source")) queryParams.Add($"source={Uri.EscapeDataString(source)}");
                if (!string.IsNullOrEmpty(rchtype) && !ContainsParameter(provider.url, "rchtype")) queryParams.Add($"rchtype={Uri.EscapeDataString(rchtype)}");
                if (life && !ContainsParameter(provider.url, "life")) queryParams.Add("life=true");
                if (islite && !ContainsParameter(provider.url, "islite")) queryParams.Add("islite=true");
                if (!string.IsNullOrEmpty(account_email) && !ContainsParameter(provider.url, "account_email")) queryParams.Add($"account_email={Uri.EscapeDataString(account_email)}");
                if (!string.IsNullOrEmpty(uid) && !ContainsParameter(provider.url, "uid")) queryParams.Add($"uid={Uri.EscapeDataString(uid)}");
                if (!string.IsNullOrEmpty(accessToken) && !ContainsParameter(provider.url, "token"))
                    queryParams.Add($"token={Uri.EscapeDataString(accessToken)}");

                if (serial == 1)
                {
                    string seasonParam = httpContext?.Request?.Query?["s"];
                    if (string.IsNullOrEmpty(seasonParam))
                        seasonParam = "-1";

                    if (!string.IsNullOrEmpty(seasonParam) && !ContainsParameter(provider.url, "s"))
                        queryParams.Add($"s={Uri.EscapeDataString(seasonParam)}");
                }

                // Исправляем формирование URL (убираем двойные ??)
                string separator = provider.url.Contains("?") ? "&" : "?";
                string queryString = string.Join("&", queryParams.Where(p => !string.IsNullOrEmpty(p)));
                string url = string.IsNullOrEmpty(queryString)
                    ? provider.url
                    : $"{provider.url}{separator}{queryString}";
                finalUrl = url;
                
                // Retry логика
                int maxAttempts = ModInit.conf.enableRetry ? ModInit.conf.maxRetryAttempts : 1;
                
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        if (attempt > 1)
                        {
                            Console.WriteLine($"🔄 SmartFilter: Retry attempt {attempt}/{maxAttempts} for {provider.name}");
                            await Task.Delay(ModInit.conf.retryDelayMs * attempt);
                        }
                        
                        Console.WriteLine($"🔗 SmartFilter: Fetching from {provider.name}: {url}");

                        using var httpClient = new HttpClient();
                        httpClient.Timeout = TimeSpan.FromSeconds(ModInit.conf.requestTimeoutSeconds > 0 ? 
                            ModInit.conf.requestTimeoutSeconds : 25);

                        var response = await httpClient.GetStringAsync(url);
                        
                        if (IsValidResponse(response, provider.name))
                        {
                            stopwatch.Stop();
                            var dataCount = CountResponseItems(response);
                            Console.WriteLine($"⏱️ SmartFilter: {provider.name} responded in {stopwatch.ElapsedMilliseconds}ms with {dataCount} items");

                            return new ProviderResult
                            {
                                ProviderName = provider.name,
                                JsonData = response,
                                HasContent = !IsEmptyResponse(response),
                                Success = true,
                                ResponseTime = (int)stopwatch.ElapsedMilliseconds,
                                ProviderUrl = finalUrl,
                                FetchedAt = DateTime.Now
                            };
                        }
                        else
                        {
                            if (ModInit.conf.detailedLogging)
                                Console.WriteLine($"⚠️ SmartFilter: Invalid response from {provider.name} (attempt {attempt}): {response?.Substring(0, Math.Min(200, response?.Length ?? 0))}");
                        }
                    }
                    catch (TaskCanceledException) when (attempt == maxAttempts)
                    {
                        stopwatch.Stop();
                        Console.WriteLine($"⏰ SmartFilter: Final timeout from {provider.name} after {maxAttempts} attempts ({stopwatch.ElapsedMilliseconds}ms)");
                        break;
                    }
                    catch (HttpRequestException ex) when (ex.Message.Contains("500") && attempt < maxAttempts)
                    {
                        Console.WriteLine($"🔄 SmartFilter: HTTP 500 from {provider.name}, will retry (attempt {attempt}/{maxAttempts})");
                        continue;
                    }
                    catch (Exception ex) when (attempt == maxAttempts)
                    {
                        stopwatch.Stop();
                        Console.WriteLine($"❌ SmartFilter: Final error from {provider.name} ({stopwatch.ElapsedMilliseconds}ms): {ex.Message}");
                        break;
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }

            if (stopwatch.IsRunning)
                stopwatch.Stop();

            return new ProviderResult
            {
                ProviderName = provider.name,
                ProviderUrl = finalUrl,
                Success = false,
                HasContent = false,
                ResponseTime = (int)stopwatch.ElapsedMilliseconds,
                FetchedAt = DateTime.Now
            };
        }

        private int CountResponseItems(string response)
        {
            try
            {
                var json = JObject.Parse(response);
                if (json["data"] is JArray dataArray)
                    return dataArray.Count;
            }
            catch { }
            return 0;
        }

        private bool IsValidResponse(string response, string providerName = "")
        {
            if (string.IsNullOrEmpty(response) || response.Length <= 2)
                return false;

            // Проверяем HTML ответы
            if (IsHtmlResponse(response))
            {
                if (ModInit.conf.detailedLogging)
                    Console.WriteLine($"🌐 SmartFilter: HTML response detected from {providerName}");
                return false;
            }

            // Более гибкая проверка JSON
            try
            {
                var json = JToken.Parse(response);
                
                // Проверяем наличие данных
                if (json["data"] != null || json["results"] != null)
                {
                    // Дополнительная проверка на пустые массивы
                    if (json["data"] is JArray dataArray && dataArray.Count == 0)
                    {
                        if (ModInit.conf.detailedLogging)
                            Console.WriteLine($"📝 SmartFilter: {providerName} returned empty data array");
                        return true; // Валидный JSON, но пустой
                    }
                    return true;
                }
                
                // Проверяем специфичные форматы провайдеров
                if (json["type"] != null)
                    return true;
                    
            }
            catch (Exception ex)
            {
                if (ModInit.conf.detailedLogging)
                    Console.WriteLine($"📛 SmartFilter: JSON parse error from {providerName}: {ex.Message}");
            }

            // Проверяем на ошибки доступа
            bool hasAccessError = response.Contains("\"accsdb\":true") ||
                                 response.Contains("IP-адрес заблокирован") ||
                                 response.Contains("Ошибка доступа");
                                 
            if (hasAccessError && ModInit.conf.detailedLogging)
                Console.WriteLine($"🚫 SmartFilter: Access error detected from {providerName}");

            return !hasAccessError && response != "[]" && response != "{}";
        }

        private bool IsHtmlResponse(string response)
        {
            if (string.IsNullOrEmpty(response))
                return false;

            return response.StartsWith("<") || 
                   response.Contains("<!DOCTYPE") || 
                   response.Contains("<html") || 
                   response.Contains("<body");
        }

        private bool IsEmptyResponse(string response)
        {
            if (string.IsNullOrEmpty(response))
                return true;

            try
            {
                var json = JObject.Parse(response);
                
                // Проверяем MovieTpl формат
                if (json["data"] is JArray dataArray)
                {
                    return dataArray.Count == 0;
                }
                
                // Проверяем SimilarTpl формат
                if (json["type"]?.ToString() == "similar" && json["data"] is JArray similarArray)
                {
                    return similarArray.Count == 0;
                }
                
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static bool ContainsParameter(string url, string key)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
                return false;

            int questionIndex = url.IndexOf('?');
            if (questionIndex == -1)
                return false;

            string query = url.Substring(questionIndex + 1);
            foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = segment.Split('=', 2);
                if (parts.Length == 0)
                    continue;

                if (parts[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private string ResolveToken()
        {
            string token = httpContext?.Request?.Query["token"].ToString();

            if (string.IsNullOrWhiteSpace(token))
                token = httpContext?.Request?.Headers["token"].ToString();

            if (string.IsNullOrWhiteSpace(token) && httpContext?.Items?.ContainsKey("token") == true)
                token = httpContext.Items["token"]?.ToString();

            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
    }
}
