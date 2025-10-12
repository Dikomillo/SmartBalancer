using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
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

                var engine = new SmartFilterEngine(memoryCache, host, HttpContext);
                var cacheResult = await InvokeCache($"smartfilter:{imdb_id}:{kinopoisk_id}:{title}:{year}:{serial}", 
                    TimeSpan.FromMinutes(ModInit.conf.cacheTimeMinutes), 
                    async () => await engine.AggregateProvidersAsync(imdb_id, kinopoisk_id, title, original_title, year, serial, original_language));

                var providerResults = cacheResult ?? new List<ProviderResult>();
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
                    var serialsResult = GetSerials.Process(validResults, title, original_title, host, HttpContext.Request.QueryString.Value);
                    
                    if (rjson)
                    {
                        // Модифицируем JSON ответ для фронтенда
                        var responseObj = new {
                            type = "season",
                            data = serialsResult,
                            providers = providerStatus
                        };
                        return Content(JsonConvert.SerializeObject(responseObj), "application/json; charset=utf-8");
                    }
                    else
                    {
                        return Content(JsonConvert.SerializeObject(serialsResult), "application/json; charset=utf-8");
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