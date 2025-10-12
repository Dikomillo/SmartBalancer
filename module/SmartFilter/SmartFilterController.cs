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
using System.Threading.Tasks;

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
                return Json(new { ready = false, total = 0, completed = 0, progress = 0, items = 0, providers = Array.Empty<object>() });

            var snapshot = SmartFilterProgress.Snapshot(memoryCache, key);
            if (snapshot == null)
                return Json(new { ready = false, total = 0, completed = 0, progress = 0, items = 0, providers = Array.Empty<object>() });

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
            [FromQuery] string provider = null,
            [FromQuery] bool rjson = false)
        {
            try
            {
                bool checkSearch = HttpContext.Request.Query.ContainsKey("checksearch");

                if (checkSearch)
                {
                    var responseObject = new JObject
                    {
                        ["type"] = ResolveContentType(serial, provider),
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
                var aggregationTask = InvokeCache(cacheKey,
                    TimeSpan.FromMinutes(Math.Max(1, ModInit.conf.cacheTimeMinutes)),
                    () => engine.AggregateProvidersAsync(imdb_id, kinopoisk_id, title, original_title, year, serial, original_language, provider, requestedSeason, progressKey));

                AggregationResult aggregation = null;
                if (aggregationTask.IsCompleted)
                {
                    aggregation = await SafeAwait(aggregationTask);
                }

                if (rjson)
                {
                    if (aggregation == null)
                    {
                        var snapshot = SmartFilterProgress.Snapshot(memoryCache, progressKey) ?? new ProgressSnapshot();
                        var response = BuildProgressResponse(snapshot, progressKey, ResolveAggregationType(serial, provider, requestedSeason), title, original_title);
                        return Content(response.ToString(Formatting.None), "application/json; charset=utf-8");
                    }

                    var responseObject = BuildAggregationResponse(aggregation, progressKey, title, original_title);
                    return Content(responseObject.ToString(Formatting.None), "application/json; charset=utf-8");
                }

                aggregation ??= await aggregationTask;

                if (aggregation == null || IsEmpty(aggregation.Data))
                    return OnError("Контент не найден");

                var html = BuildHtmlFromAggregation(aggregation, title, original_title);
                if (string.IsNullOrEmpty(html))
                    return OnError("Контент не найден");

                return Content(html, "text/html; charset=utf-8");
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

        private static bool IsEmpty(JToken token)
        {
            if (token == null)
                return true;

            if (token is JArray array)
                return array.Count == 0;

            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if (property.Value is JArray nested && nested.Count > 0)
                        return false;
                }

                return !obj.Properties().Any();
            }

            return !token.HasValues;
        }

        private async Task<AggregationResult> SafeAwait(Task<AggregationResult> task)
        {
            try
            {
                return await task;
            }
            catch
            {
                return null;
            }
        }

        private JObject BuildProgressResponse(ProgressSnapshot snapshot, string progressKey, string fallbackType, string title, string originalTitle)
        {
            snapshot ??= new ProgressSnapshot();

            var providers = snapshot.Providers?.Select(p => new
            {
                name = p.Name,
                plugin = p.Plugin,
                status = p.Status,
                items = p.Items,
                responseTime = p.ResponseTime,
                error = p.Error
            }) ?? Enumerable.Empty<object>();

            var response = new JObject
            {
                ["status"] = snapshot.Ready ? "done" : "pending",
                ["progress"] = snapshot.ProgressPercentage,
                ["ready"] = snapshot.Ready,
                ["total"] = snapshot.Total,
                ["completed"] = snapshot.Completed,
                ["items"] = snapshot.Items,
                ["providers"] = JArray.FromObject(providers),
                ["progressKey"] = progressKey,
                ["type"] = fallbackType
            };

            if (snapshot.Partial != null)
                response["results"] = snapshot.Partial.DeepClone();
            else
                response["results"] = new JArray();

            if (snapshot.Metadata != null)
                response["metadata"] = JObject.FromObject(snapshot.Metadata);

            if (snapshot.Partial != null)
            {
                var partialAggregation = new AggregationResult
                {
                    Type = fallbackType,
                    Data = snapshot.Partial.DeepClone(),
                    Providers = snapshot.Providers?.Select(p => p.Clone()).ToList(),
                    Metadata = snapshot.Metadata
                };

                // DeepWiki: docs/architecture/online.md – preserve template compatibility during aggregation progress
                var lampacPayload = LampacResponseBuilder.Build(partialAggregation, title, originalTitle);
                if (lampacPayload != null)
                {
                    response["type"] = lampacPayload.Value<string>("type") ?? fallbackType;

                    foreach (var property in lampacPayload)
                    {
                        if (property.Key.Equals("type", StringComparison.OrdinalIgnoreCase))
                            continue;

                        response[property.Key] = property.Value.DeepClone();
                    }
                }
            }

            if (!response.ContainsKey("data"))
                response["data"] = new JArray();

            return response;
        }

        private JObject BuildAggregationResponse(AggregationResult aggregation, string progressKey, string title, string originalTitle)
        {
            var providers = aggregation.Providers?.Select(p => new
            {
                name = p.Name,
                plugin = p.Plugin,
                status = p.Status,
                items = p.Items,
                responseTime = p.ResponseTime,
                error = p.Error
            }) ?? Enumerable.Empty<object>();

            var responseObject = new JObject
            {
                ["status"] = "done",
                ["type"] = aggregation.Type,
                ["providers"] = JArray.FromObject(providers),
                ["results"] = aggregation.Data?.DeepClone() ?? new JArray(),
                ["progressKey"] = progressKey
            };

            if (aggregation.Metadata != null)
                responseObject["metadata"] = JObject.FromObject(aggregation.Metadata);

            // DeepWiki: docs/architecture/online.md – align SmartFilter payload with Lampac provider templates
            var lampacPayload = LampacResponseBuilder.Build(aggregation, title, originalTitle);
            if (lampacPayload != null)
            {
                responseObject["type"] = lampacPayload.Value<string>("type") ?? aggregation.Type;

                foreach (var property in lampacPayload)
                {
                    if (property.Key.Equals("type", StringComparison.OrdinalIgnoreCase))
                        continue;

                    responseObject[property.Key] = property.Value.DeepClone();
                }
            }

            if (!responseObject.ContainsKey("data"))
                responseObject["data"] = new JArray();

            return responseObject;
        }

        private string BuildHtmlFromAggregation(AggregationResult aggregation, string title, string originalTitle)
        {
            if (aggregation?.Data == null)
                return null;

            var items = aggregation.Data as JArray ?? new JArray();
            if (items.Count == 0)
                return null;

            var builder = new System.Text.StringBuilder();
            builder.Append("<div class='smartfilter-list'>");
            builder.Append($"<h2 style='padding:10px 15px'>{System.Net.WebUtility.HtmlEncode(title ?? originalTitle ?? "SmartFilter")}</h2>");
            builder.Append("<ul style='list-style:none;margin:0;padding:0'>");

            foreach (var item in items.OfType<JObject>())
            {
                string name = item.Value<string>("title") ?? "Элемент";
                string quality = item.Value<string>("quality_label") ?? string.Empty;
                string voice = item.Value<string>("voice_label") ?? string.Empty;
                string url = item.Value<string>("url") ?? string.Empty;

                builder.Append("<li style='padding:10px 15px;border-bottom:1px solid rgba(255,255,255,0.1)'>");
                builder.Append($"<div style='font-weight:600'>{System.Net.WebUtility.HtmlEncode(name)}</div>");
                builder.Append("<div style='font-size:12px;opacity:0.75'>");
                if (!string.IsNullOrEmpty(quality))
                    builder.Append($"<span>Качество: {System.Net.WebUtility.HtmlEncode(quality)}</span> ");
                if (!string.IsNullOrEmpty(voice))
                    builder.Append($"<span>Озвучка: {System.Net.WebUtility.HtmlEncode(voice)}</span> ");
                builder.Append("</div>");
                if (!string.IsNullOrEmpty(url))
                    builder.Append($"<div style='margin-top:6px;word-break:break-all'><a href='{System.Net.WebUtility.HtmlEncode(url)}' target='_blank'>Перейти</a></div>");
                builder.Append("</li>");
            }

            builder.Append("</ul></div>");
            return builder.ToString();
        }

        private static string ResolveContentType(int serial, string provider)
        {
            return serial switch
            {
                1 => string.IsNullOrWhiteSpace(provider) ? "similar" : "season",
                2 => "episode",
                _ => "movie"
            };
        }

        private static string ResolveAggregationType(int serial, string provider, int requestedSeason)
        {
            if (serial == 1)
            {
                if (requestedSeason > 0)
                    return "episode";

                return string.IsNullOrWhiteSpace(provider) ? "similar" : "season";
            }

            return serial == 2 ? "episode" : "movie";
        }
    }
}
