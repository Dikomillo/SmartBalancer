using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Shared;
using Shared.Engine;
using Shared.Models;
using Shared.Models.Templates;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web;
using System.Security.Cryptography;
using SmartFilter.parse;

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
            [FromQuery] string imdb_id = null,
            [FromQuery] long kinopoisk_id = 0,
            [FromQuery] string title = null,
            [FromQuery] string original_title = null,
            [FromQuery] int year = 0,
            [FromQuery] int serial = -1,
            [FromQuery] string original_language = null,
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

                var providerCache = await InvokeCache<List<ProviderResult>>(
                    BuildCacheKey("smartfilter:providers:", host,
                        imdb_id,
                        kinopoisk_id > 0 ? kinopoisk_id.ToString() : null,
                        Normalize(title),
                        Normalize(original_title),
                        year > 0 ? year.ToString() : null,
                        serial.ToString(),
                        rawQuery),
                    TimeSpan.FromMinutes(ResolveProviderCacheMinutes()),
                    async res =>
                    {
                        var result = await engine.AggregateProvidersAsync(imdb_id, kinopoisk_id, title, original_title, year, serial, original_language);

                        if (result == null || result.Count == 0)
                            return res.Fail("no providers");

                        return result;
                    });

                if (!providerCache.IsSuccess)
                    Console.WriteLine($"⚠️ SmartFilter: Provider cache returned '{providerCache.ErrorMsg ?? "unknown"}'");

                var providerResults = providerCache.Value ?? new List<ProviderResult>();
                var validResults = providerResults.Where(r => r.HasContent).ToList();

                Console.WriteLine($"📊 SmartFilter: Found {validResults.Count} valid results from {providerResults.Count} total providers");

                if (validResults.Count == 0)
                    return OnError("Контент не найден");

                // Собираем статусы провайдеров
                var providerStatus = providerResults.Select(r => new {
                    name = r.ProviderName,
                    status = r.HasContent ? "completed" : "error",
                    responseTime = r.ResponseTime
                }).ToList();

                // Разделяем логику по типу контента
                if (serial == -1 || serial == 0) // Фильмы
                {
                    var cinemaResult = GetCinema.Process(validResults, title, original_title);
                    var jsonResult = cinemaResult.ToJson();
                    
                    if (rjson)
                    {
                        // Модифицируем JSON ответ для фронтенда
                        var responseObj = new {
                            type = "movie",
                            data = JArray.Parse(jsonResult)["data"],
                            providers = providerStatus
                        };
                        return Content(JsonConvert.SerializeObject(responseObj), "application/json; charset=utf-8");
                    }
                    else
                    {
                        return Content(cinemaResult.ToHtml(), "text/html; charset=utf-8");
                    }
                }
                else if (serial == 1) // Сериалы
                {
                    var queryParams = HttpUtility.ParseQueryString(rawQuery.TrimStart('?'));
                    string requestedSeason = string.IsNullOrEmpty(queryParams?["s"]) ? "-1" : queryParams["s"];
                    string requestedVoice = queryParams?["t"] ?? string.Empty;

                    string providerFingerprint = string.Join("|", validResults
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
                        rawQuery);

                    TimeSpan serialTtl = seasonStage
                        ? TimeSpan.FromMinutes(ResolveSeasonCacheMinutes())
                        : TimeSpan.FromMinutes(ResolveEpisodeCacheMinutes());

                    var serialCache = await InvokeCache<SerialProcessResult>(
                        serialCacheKey,
                        serialTtl,
                        async res =>
                        {
                            var result = GetSerials.Process(validResults, title, original_title, host, HttpContext.Request.QueryString.Value ?? string.Empty, rjson);

                            if (result == null || (result.SeasonCount == 0 && result.EpisodeCount == 0))
                                return res.Fail("no content");

                            return result;
                        });

                    if (!serialCache.IsSuccess)
                        Console.WriteLine($"⚠️ SmartFilter: Serial cache returned '{serialCache.ErrorMsg ?? "unknown"}'");

                    var serialsResult = serialCache.Value;

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
        public int ResponseTime { get; set; } = 0;
        public DateTime FetchedAt { get; set; } = DateTime.Now;
    }
}