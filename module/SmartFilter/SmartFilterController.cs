using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared.Engine;
using Shared.Models;
using System;
using System.Collections.Generic;
using System.Linq;

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
        [Route("lite/smartfilter/progress")]
        public ActionResult Progress([FromQuery] string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return Json(new { ready = false, total = 0, progress = 0, providers = Array.Empty<object>() });

            var snapshot = SmartFilterProgress.Snapshot(memoryCache, key);
            if (snapshot == null)
                return Json(new { ready = false, total = 0, progress = 0, providers = Array.Empty<object>() });

            return Content(JsonConvert.SerializeObject(snapshot, Formatting.None), "application/json; charset=utf-8");
        }

        [HttpGet]
        [Route("lite/smartfilter")]
        public async System.Threading.Tasks.Task<ActionResult> Index(
            [FromQuery] string imdb_id = null,
            [FromQuery] long kinopoisk_id = 0,
            [FromQuery] string title = null,
            [FromQuery] string original_title = null,
            [FromQuery] int year = 0,
            [FromQuery] int serial = -1,
            [FromQuery] string original_language = null,
            [FromQuery] bool rjson = false)
        {
            try
            {
                bool checkSearch = HttpContext.Request.Query.ContainsKey("checksearch");

                if (checkSearch)
                {
                    var responseObject = new JObject
                    {
                        ["type"] = ResolveContentType(serial),
                        ["title"] = title ?? original_title ?? string.Empty,
                        ["year"] = year
                    };

                    return Content(responseObject.ToString(Formatting.None), "application/json; charset=utf-8");
                }

                HttpContext.Response.Headers["X-Timeout"] = "300000";

                var querySnapshot = BuildQueryDictionary(HttpContext.Request.Query);
                var cacheKey = SmartFilterEngine.BuildCacheKey(querySnapshot);
                var progressKey = SmartFilterProgress.BuildProgressKey(cacheKey);
                int requestedSeason = TryParseInt(HttpContext.Request.Query["s"]);

                var engine = new SmartFilterEngine(memoryCache, host, HttpContext);
                var aggregation = await InvokeCache(cacheKey,
                    TimeSpan.FromMinutes(Math.Max(1, ModInit.conf.cacheTimeMinutes)),
                    () => engine.AggregateProvidersAsync(imdb_id, kinopoisk_id, title, original_title, year, serial, original_language, requestedSeason, progressKey));

                aggregation ??= new AggregationResult { Type = serial == 1 ? "season" : "movie", ProgressKey = progressKey };
                aggregation.ProgressKey ??= progressKey;

                SmartFilterProgress.PublishFinal(memoryCache, progressKey, aggregation.Providers);

                if (aggregation.Data == null || aggregation.Data.Count == 0)
                    return OnError("Контент не найден");

                var providerStatus = aggregation.Providers
                    .Select(p => new
                    {
                        name = p.Name,
                        plugin = p.Plugin,
                        status = p.Status,
                        items = p.Items,
                        responseTime = p.ResponseTime,
                        error = p.Error
                    })
                    .ToList();

                if (rjson)
                {
                    var responseObject = new JObject
                    {
                        ["type"] = aggregation.Type,
                        ["data"] = aggregation.Data,
                        ["providers"] = JArray.FromObject(providerStatus),
                        ["progressKey"] = progressKey
                    };

                    return Content(responseObject.ToString(Formatting.None), "application/json; charset=utf-8");
                }
                else
                {
                    var html = ResponseRenderer.BuildHtml(aggregation.Type, aggregation.Data, title, original_title);
                    if (string.IsNullOrEmpty(html))
                        return OnError("Контент не найден");

                    return Content(html, "text/html; charset=utf-8");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SmartFilterController: error for '{title}': {ex.Message}");
                return OnError("Произошла внутренняя ошибка");
            }
        }

        private Dictionary<string, string> BuildQueryDictionary(IQueryCollection query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in query)
            {
                var value = pair.Value.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    dict[pair.Key] = value;
            }

            dict.Remove("rjson");
            return dict;
        }

        private static int TryParseInt(Microsoft.Extensions.Primitives.StringValues value)
        {
            if (value.Count == 0)
                return 0;

            return int.TryParse(value.ToString(), out int result) ? result : 0;
        }

        private ActionResult OnError(string message)
        {
            if (IsAjaxRequest)
                return Json(new { error = true, message });

            return Content($"<div class='videos__item' style='color: #fff; padding: 20px;'>{message}</div>", "text/html; charset=utf-8");
        }

        private bool IsAjaxRequest => HttpContext.Request.Headers["X-Requested-With"] == "XMLHttpRequest";

        private static string ResolveContentType(int serial)
        {
            return serial switch
            {
                1 => "season",
                2 => "episode",
                _ => "movie"
            };
        }
    }
}
