using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Shared;
using Shared.Engine;
using Shared.Models;
using Shared.Models.Templates;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmartFilter.parse;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace SmartFilter
{
    public class SmartFilterController : BaseOnlineController
    {
        public SmartFilterController() { }

        [HttpGet]
        [Route("smartfilter.js")]
        public ContentResult SmartFilterJS()
        {
            var js = FileCache.ReadAllText("plugins/smartfilter.js");
            return Content(js.Replace("{localhost}", host), "application/javascript; charset=utf-8");
        }

        [HttpGet]
        [Route("lite/smartfilter")]
        public async Task<ActionResult> Index(
            [FromQuery] long id = 0,
            [FromQuery] string imdb_id = null,
            [FromQuery] long kinopoisk_id = 0,
            [FromQuery] string title = null,
            [FromQuery] string original_title = null,
            [FromQuery] int year = 0,
            [FromQuery] int serial = -1,
            [FromQuery] string original_language = null,
            [FromQuery] string source = null,
            [FromQuery] string rchtype = null,
            [FromQuery] bool life = false,
            [FromQuery] bool islite = false,
            [FromQuery] string account_email = null,
            [FromQuery] string uid = null,
            [FromQuery] string token = null,
            [FromQuery] bool rjson = false,
            [FromQuery] string quality = null,
            [FromQuery] string voice = null)
        {
            try
            {
                // Увеличиваем таймаут для этого запроса
                HttpContext.Response.Headers["X-Timeout"] = "300000"; // 5 минут
                Console.WriteLine($"🎬 SmartFilter: Processing request for '{title}' ({year}) - serial: {serial}");

                var engine = new SmartFilterEngine(host, HttpContext);
                string rawQuery = HttpContext.Request.QueryString.HasValue ? HttpContext.Request.QueryString.Value : string.Empty;
                NameValueCollection queryParams = HttpUtility.ParseQueryString(rawQuery.TrimStart('?'));
                string providerQuerySignature = BuildQuerySignature(queryParams, "rjson", "t", "voice");
                string serialQuerySignature = BuildQuerySignature(queryParams, "rjson");

                var conf = ModInit.conf;

                // =========================
                // Providers (InvokeCache<List<ProviderResult>>)
                // =========================
                var providerCache = await InvokeCache<List<ProviderResult>>(
                    BuildCacheKey("smartfilter:providers:", host,
                        id > 0 ? id.ToString() : null,
                        Normalize(imdb_id),
                        kinopoisk_id > 0 ? kinopoisk_id.ToString() : null,
                        Normalize(title),
                        Normalize(original_title),
                        year > 0 ? year.ToString() : null,
                        serial.ToString(),
                        Normalize(original_language),
                        Normalize(source),
                        Normalize(rchtype),
                        life ? "life" : null,
                        islite ? "lite" : null,
                        Normalize(account_email),
                        Normalize(uid),
                        Normalize(token),
                        providerQuerySignature,
                        string.Join("|", conf.excludeProviders ?? Array.Empty<string>()),
                        string.Join("|", conf.includeOnlyProviders ?? Array.Empty<string>())
                    ),
                    TimeSpan.FromMinutes(ResolveProviderCacheMinutes()),
                    proxyManager: null, // при желании можно передать ProxyManager
                    async res =>
                    {
                        var list = await engine.AggregateProvidersAsync(
                            id, imdb_id, kinopoisk_id, title, original_title, year, serial,
                            original_language, source, rchtype, life, islite, account_email, uid, token
                        );

                        if (list == null || list.Count == 0)
                            return res.Fail("no providers");

                        return list;
                    }
                );

                var providerResults = providerCache?.Value ?? new List<ProviderResult>();
                var successfulResults = providerResults.Where(r => r.Success).ToList();
                var validResults = successfulResults.Where(r => r.HasContent).ToList();

                Console.WriteLine($"📊 SmartFilter: {validResults.Count} providers with content out of {successfulResults.Count} successful responses ({providerResults.Count} total)");

                if (validResults.Count == 0)
                    return OnError("Контент не найден");

                // Собираем статусы провайдеров
                var providerStatus = providerResults.Select(r => new
                {
                    name = r.ProviderName,
                    status = r.Success ? (r.HasContent ? "completed" : "empty") : "error",
                    hasContent = r.HasContent,
                    success = r.Success,
                    responseTime = r.ResponseTime,
                    url = r.ProviderUrl,
                    fetchedAt = r.FetchedAt
                }).ToList();

                // Разделяем логику по типу контента
                if (serial == -1 || serial == 0) // Фильмы
                {
                    var cinemaResult = GetCinema.Process(validResults, title, original_title);
                    var jsonResult = cinemaResult.ToJson();

                    if (rjson)
                    {
                        var json = JObject.Parse(jsonResult);
                        json["providers"] = JArray.FromObject(providerStatus);
                        return Content(json.ToString(Formatting.None), "application/json; charset=utf-8");
                    }
                    else
                    {
                        return Content(cinemaResult.ToHtml(), "text/html; charset=utf-8");
                    }
                }
                else if (serial == 1) // Сериалы
                {
                    string requestedSeason = string.IsNullOrEmpty(queryParams?["s"]) ? "-1" : queryParams["s"];
                    string requestedVoice = queryParams?["t"] ?? queryParams?["voice"] ?? voice ?? string.Empty;

                    string providerFingerprint = string.Join("|", successfulResults
                        .Select(r => r.ProviderName)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

                    bool seasonStage = requestedSeason == "-1";
                    string serialCacheKey = BuildCacheKey(
                        seasonStage ? "smartfilter:serial:seasons:" : $"smartfilter:serial:episodes:{requestedSeason}:",
                        host,
                        imdb_id,
                        kinopoisk_id > 0 ? kinopoisk_id.ToString() : null,
                        Normalize(title),
                        Normalize(original_title),
                        requestedVoice,
                        providerFingerprint,
                        serialQuerySignature,
                        Normalize(source),
                        Normalize(rchtype),
                        life ? "life" : null,
                        islite ? "lite" : null,
                        Normalize(account_email),
                        Normalize(uid),
                        Normalize(token),
                        string.Join("|", conf.excludeProviders ?? Array.Empty<string>()),
                        string.Join("|", conf.includeOnlyProviders ?? Array.Empty<string>()));

                    TimeSpan serialTtl = seasonStage
                        ? TimeSpan.FromMinutes(ResolveSeasonCacheMinutes())
                        : TimeSpan.FromMinutes(ResolveEpisodeCacheMinutes());

                    // =========================
                    // Serials (InvokeCache<SerialProcessResult>)
                    // =========================
                    var serialCache = await InvokeCache<SerialProcessResult>(
                        serialCacheKey,
                        serialTtl,
                        proxyManager: null,
                        async res =>
                        {
                            var sr = GetSerials.Process(
                                validResults,
                                title,
                                original_title,
                                host,
                                HttpContext.Request.QueryString.Value ?? string.Empty,
                                rjson
                            );

                            if (sr == null || (sr.SeasonCount == 0 && sr.EpisodeCount == 0))
                                return res.Fail("no content");

                            return sr;
                        }
                    );

                    var serialsResult = serialCache?.Value;

                    if (serialsResult == null || (serialsResult.SeasonCount == 0 && serialsResult.EpisodeCount == 0))
                        return OnError("Контент не найден");

                    if (rjson)
                    {
                        string payload = serialsResult.Type == "episode"
                            ? serialsResult.Episodes?.ToJson(serialsResult.Voice)
                            : serialsResult.Seasons?.ToJson(serialsResult.Voice);

                        JObject json = !string.IsNullOrEmpty(payload) ? JObject.Parse(payload) : new JObject
                        {
                            ["type"] = serialsResult.Type,
                            ["data"] = new JArray()
                        };

                        if (serialsResult.Voice is VoiceTpl voiceTplValue && json["voice"] == null)
                            json["voice"] = JToken.FromObject(voiceTplValue.ToObject());

                        json["providers"] = JArray.FromObject(providerStatus);
                        return Content(json.ToString(Formatting.None), "application/json; charset=utf-8");
                    }
                    else
                    {
                        var htmlBuilder = new StringBuilder();

                        if (serialsResult.Voice is VoiceTpl voiceTpl && voiceTpl.data != null && voiceTpl.data.Count > 0)
                            htmlBuilder.Append(voiceTpl.ToHtml());

                        if (serialsResult.Type == "episode")
                        {
                            if (serialsResult.Episodes is EpisodeTpl etpl)
                                htmlBuilder.Append(etpl.ToHtml());
                        }
                        else
                        {
                            if (serialsResult.Seasons is SeasonTpl stpl)
                                htmlBuilder.Append(stpl.ToHtml());
                        }

                        return Content(htmlBuilder.ToString(), "text/html; charset=utf-8");
                    }
                }
                else
                {
                    return OnError("Неизвестный тип контента");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ SmartFilterController: Unhandled error for '{title}': {ex.Message}");
                Console.WriteLine($"❌ SmartFilterController: Stack trace: {ex.StackTrace}");
                return OnError("Произошла внутренняя ошибка");
            }
        }

        private ActionResult OnError(string message)
        {
            Console.WriteLine($"⚠️ SmartFilter: Returning error: {message}");
            
            if (IsAjaxRequest)
                return Json(new { error = true, message });

            return Content($"<div class='videos__item' style='color: #fff; padding: 20px;'>{message}</div>", "text/html; charset=utf-8");
        }

        private bool IsAjaxRequest => HttpContext.Request.Headers["X-Requested-With"] == "XMLHttpRequest";

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string BuildQuerySignature(NameValueCollection query, params string[] ignoreKeys)
        {
            if (query == null || query.Count == 0)
                return null;

            var ignore = new HashSet<string>(ignoreKeys ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var pairs = new List<string>();

            foreach (string key in query.AllKeys)
            {
                if (string.IsNullOrEmpty(key) || ignore.Contains(key))
                    continue;

                var values = query.GetValues(key);
                if (values == null)
                    continue;

                foreach (var value in values)
                {
                    if (value == null)
                        continue;

                    pairs.Add($"{key}={value}");
                }
            }

            if (pairs.Count == 0)
                return null;

            pairs.Sort(StringComparer.Ordinal);
            return string.Join("&", pairs);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        private static double ResolveProviderCacheMinutes()
        {
            return ModInit.conf.cacheTimeMinutes > 0 ? ModInit.conf.cacheTimeMinutes : 20;
        }

        private static double ResolveSeasonCacheMinutes()
        {
            double baseMinutes = ResolveProviderCacheMinutes();
            return Clamp(baseMinutes, 10, 30);
        }

        private static double ResolveEpisodeCacheMinutes()
        {
            double baseMinutes = ResolveProviderCacheMinutes() / 2d;
            if (baseMinutes < 5d)
                baseMinutes = 5d;
            return Clamp(baseMinutes, 5, 20);
        }

        private static string BuildCacheKey(string prefix, params string[] parts)
        {
            var values = parts?
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p.Trim())
                .ToArray() ?? Array.Empty<string>();

            string raw = values.Length == 0 ? prefix : prefix + string.Join("|", values);

            using var md5 = MD5.Create();
            byte[] hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(raw));

            var builder = new StringBuilder(prefix.Length + hashBytes.Length * 2);
            builder.Append(prefix);

            foreach (byte b in hashBytes)
                builder.Append(b.ToString("x2"));

            return builder.ToString();
        }
    }

    public class ProviderResult
    {
        public string ProviderName { get; set; }
        public string JsonData { get; set; }
        public bool HasContent { get; set; }
        public bool Success { get; set; }
        public int ResponseTime { get; set; } = 0;
        public string ProviderUrl { get; set; }
        public DateTime FetchedAt { get; set; } = DateTime.Now;
    }
}
