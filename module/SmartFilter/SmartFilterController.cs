using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Shared;
using Shared.Engine;
using Shared.Models; // CacheResult<T>
using Online;        // RchClient, ProxyManager, BaseController helpers
using SmartFilter.parse; // SerialProcessResult (ваш тип)

namespace SmartFilter.Controllers
{
    [ApiController]
    public class SmartFilterController : BaseController
    {
        /// <summary>
        /// Сводный список сериалов/эпизодов, рассчитанный SmartFilter'ом.
        /// Совместим с Lampa Lite: GET /lite/smartfilter/serials?title=...&original_title=...&rjson=true
        /// </summary>
        [HttpGet]
        [Route("lite/smartfilter/serials")]
        public async ValueTask<ActionResult> Serials(
            string title,
            string original_title,
            bool rjson = false)
        {
            var init = await Initialization();
            if (await IsBadInitialization(init, rch: true))
                return badInitMsg;

            var proxyManager = new ProxyManager(init);
            var rch = new RchClient(HttpContext, host, init, requestInfo, keepalive: -1);
            if (rch.IsNotConnected())
                return ContentTo(rch.connectionMsg);

            // Ключ кеша учитывает заголовки и IP через rch.ipkey
            string key = rch.ipkey(
                $"smartfilter:serials:{(title ?? string.Empty).ToLower()}:{(original_title ?? string.Empty).ToLower()}:{(rjson ? 1 : 0)}",
                proxyManager
            );

            // onget: Func<CacheResult<T>, ValueTask<dynamic>> — возвращаем либо res.Fail(...), либо сам T
            var cache = await InvokeCache<SerialProcessResult>(
                key,
                cacheTime(20, init: init),
                rch.enable ? null : proxyManager,
                async res =>
                {
                    // Ваша бизнес-логика сборки результата.
                    // Ниже оставлен типичный паттерн Lampac: готовим данные, валидируем и возвращаем T.

                    // TODO: замените на реальный сбор источников/результатов
                    var validResults = Enumerable.Empty<object>();

                    var result = GetSerials.Process(
                        validResults,
                        title,
                        original_title,
                        host,
                        HttpContext.Request.QueryString.Value ?? string.Empty,
                        rjson
                    );

                    if (result == null || (result.SeasonCount == 0 && result.EpisodeCount == 0))
                        return res.Fail("no content");

                    return result; // OK → внутри InvokeCache обернётся в CacheResult<T>
                }
            );

            if (!cache.IsSuccess || cache.Value == null)
                return OnError(cache.ErrorMsg, gbcache: !rch.enable);

            // Формат ответа: JSON/HTML — оставляем выбор через параметр rjson
            return ContentTo(rjson ? cache.Value.ToJson() : cache.Value.ToHtml());
        }

        /// <summary>
        /// Агрегация провайдеров для SmartFilter'a (пример).
        /// GET /lite/smartfilter/providers?title=...&original_title=...&rjson=true
        /// </summary>
        [HttpGet]
        [Route("lite/smartfilter/providers")]
        public async ValueTask<ActionResult> Providers(
            string title = null,
            string original_title = null,
            bool rjson = true)
        {
            var init = await Initialization();
            if (await IsBadInitialization(init, rch: true))
                return badInitMsg;

            var proxyManager = new ProxyManager(init);
            var rch = new RchClient(HttpContext, host, init, requestInfo, keepalive: -1);
            if (rch.IsNotConnected())
                return ContentTo(rch.connectionMsg);

            string key = rch.ipkey(
                $"smartfilter:providers:{(title ?? string.Empty).ToLower()}:{(original_title ?? string.Empty).ToLower()}",
                proxyManager
            );

            var cache = await InvokeCache<List<ProviderResult>>(
                key,
                cacheTime(40, init: init),
                rch.enable ? null : proxyManager,
                async res =>
                {
                    // TODO: подставьте вашу агрегацию провайдеров (например, engine.AggregateProvidersAsync)
                    var providers = new List<ProviderResult>();

                    if (providers == null || providers.Count == 0)
                        return res.Fail("no providers");

                    return providers;
                }
            );

            if (!cache.IsSuccess || cache.Value == null || cache.Value.Count == 0)
                return OnError(cache.ErrorMsg, gbcache: !rch.enable);

            if (rjson)
                return Json(new { type = "providers", data = cache.Value });

            // Простой HTML-ответ (для отладки)
            var html = "<ul>" + string.Join("", cache.Value.Select(p =>
                $"<li>{System.Net.WebUtility.HtmlEncode(p.Name)} — {System.Net.WebUtility.HtmlEncode(p.Url)}</li>")
            ) + "</ul>";
            return Content(html, "text/html; charset=utf-8");
        }

        /// <summary>
        /// Пример поиска (SmartFilter Search) — показывает тот же паттерн InvokeCache.
        /// GET /lite/smartfilter/search?query=...
        /// </summary>
        [HttpGet]
        [Route("lite/smartfilter/search")]
        public async ValueTask<ActionResult> Search(string query)
        {
            var init = await Initialization();
            if (await IsBadInitialization(init, rch: true))
                return badInitMsg;

            var proxyManager = new ProxyManager(init);
            var rch = new RchClient(HttpContext, host, init, requestInfo, keepalive: -1);
            if (rch.IsNotConnected())
                return ContentTo(rch.connectionMsg);

            if (string.IsNullOrWhiteSpace(query))
                return ContentTo("Query is empty");

            string key = rch.ipkey($"smartfilter:search:{query.Trim().ToLower()}", proxyManager);

            var cache = await InvokeCache<JArray>(
                key,
                cacheTime(20, init: init),
                rch.enable ? null : proxyManager,
                async res =>
                {
                    // TODO: замените на реальный источник поиска
                    // Пример: запрос к вашему бэкенду/провайдерам
                    var results = new JArray();

                    if (results == null || !results.HasValues)
                        return res.Fail("results");

                    return results;
                }
            );

            if (!cache.IsSuccess || cache.Value == null)
                return OnError(cache.ErrorMsg, gbcache: !rch.enable);

            return ContentTo(cache.Value.ToString());
        }

        #region Внутренние модели/DTO (при необходимости)
        public class ProviderResult
        {
            public string Name { get; set; }
            public string Url { get; set; }
        }
        #endregion
    }
}
