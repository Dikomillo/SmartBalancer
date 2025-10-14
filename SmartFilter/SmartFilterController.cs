using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using Shared.Engine;
using Shared.Models;
using Shared.Models.Base;
using Shared.Models.Templates;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace SmartFilter
{
    [Route("lite/smartfilter")]
    public class SmartFilterController : BaseOnlineController
    {
        [HttpGet]
        public async Task<ActionResult> Index(
            long id,
            string imdb_id,
            long kinopoisk_id,
            string title,
            string original_title,
            string original_language,
            int year,
            int serial = -1,
            int s = -1,
            string t = null,
            string p = null,
            bool rjson = false,
            int collect = 0
        )
        {
            var conf = ModInit.conf;
            string host = AppInit.Host(HttpContext);

            var events = await EventsClient.GetAsync(HttpContext, memoryCache, host,
                id, imdb_id, kinopoisk_id, title, original_title, original_language, year, serial);

            if (events.Count == 0)
                return ContentTo(rjson ? "[]" : "Нет источников");

            events = ProviderFilter.Apply(events, conf.includeProviders, conf.excludeProviders);
            if (events.Count == 0)
                return ContentTo(rjson ? "[]" : "Нет источников");

            var active = ProviderFilter.PickActive(events, p);

            var vtpl = VoiceBuilder.Build(host, events, active,
                id, imdb_id, kinopoisk_id, title, original_title, original_language, year, serial, s, t, rjson);

            if (collect == 1 && rjson)
            {
                var merged = await ProviderProxy.CollectEpisodesAsync(
                    HttpContext, memoryCache, events.Take(conf.collectTop).ToList(), s, t, conf.timeout, conf.parallel);

                var etplMerged = ResultMerge.ToEpisodeTpl(memoryCache, merged);
                return ContentTo(etplMerged.ToJson(vtpl));
            }

            var prov = await ProviderProxy.FetchAsync(HttpContext, memoryCache, active.url, s, t, rjson, conf.timeout);

            if (!rjson)
                return ContentTo(vtpl.ToHtml() + (prov.html ?? string.Empty));

            if (prov.type == "episode" && prov.episodes != null)
            {
                var etpl = ResultMerge.ToEpisodeTpl(memoryCache, prov.episodes);
                return ContentTo(etpl.ToJson(vtpl));
            }

            if (prov.type == "movie" && prov.rawJson != null)
            {
                var etpl = ResultMerge.FromMovie(memoryCache, prov.rawJson, title, s: s > 0 ? s : 1);
                return ContentTo(etpl.ToJson(vtpl));
            }

            if (prov.type == "season" && prov.rawJson != null)
            {
                var (stpl, ok) = SeasonHelper.FromJson(host, prov.rawJson,
                    id, imdb_id, kinopoisk_id, title, original_title, original_language, year, serial, t, rjson);

                if (rjson)
                    return ContentTo(ok ? stpl.ToJson(vtpl) : prov.rawJson);

                return ContentTo(vtpl.ToHtml() + (ok ? stpl.ToHtml() : string.Empty));
            }

            return ContentTo(prov.rawJson ?? "{}");
        }
    }

    // ---------- /lite/events ----------

    static class EventsClient
    {
        public static async Task<List<(string name, string url, string plugin, int index)>> GetAsync(
            HttpContext ctx, IMemoryCache cache, string host,
            long id, string imdb_id, long kp, string title, string original_title, string original_language, int year, int serial)
        {
            string url = $"{host}/lite/events?id={id}&imdb_id={HttpUtility.UrlEncode(imdb_id)}&kinopoisk_id={kp}" +
                         $"&title={HttpUtility.UrlEncode(title)}&original_title={HttpUtility.UrlEncode(original_title)}" +
                         $"&original_language={HttpUtility.UrlEncode(original_language)}&year={year}&serial={serial}&islite=true";

            string ckey = "sf:events:" + url;
            if (!cache.TryGetValue(ckey, out JArray arr))
            {
                var header = HeadersModel.Init(("localrequest", AppInit.rootPasswd));
                arr = await Http.Get<JArray>(url, timeoutSeconds: 8, headers: header);
                if (arr != null) cache.Set(ckey, arr, TimeSpan.FromMinutes(ModInit.conf.cacheMinutes));
            }

            var list = new List<(string,string,string,int)>();
            if (arr == null) return list;

            foreach (var it in arr)
            {
                string name = it.Value<string>("name");
                string u = it.Value<string>("url");
                string plugin = it.Value<string>("plugin");
                int index = it.Value<int?>("index") ?? 0;

                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(u)) continue;
                u = UrlUtil.EnsureQuery(u, "rjson", "true");
                list.Add((name, u, plugin, index));
            }

            list = list.Where(i => !string.Equals(i.Item3, "smartfilter", StringComparison.OrdinalIgnoreCase)).ToList();
            return list.OrderBy(i => i.Item4).ToList();
        }
    }

    // ---------- фильтры и выбор ----------

    static class ProviderFilter
    {
        public static List<(string name, string url, string plugin, int index)> Apply(
            List<(string name, string url, string plugin, int index)> src, string[] include, string[] exclude)
        {
            if (include != null && include.Length > 0)
                src = src.Where(i => include.Contains(i.plugin, StringComparer.OrdinalIgnoreCase)).ToList();

            if (exclude != null && exclude.Length > 0)
                src = src.Where(i => !exclude.Contains(i.plugin, StringComparer.OrdinalIgnoreCase)).ToList();

            return src;
        }

        public static (string name, string url, string plugin, int index) PickActive(
            List<(string name, string url, string plugin, int index)> list, string plugin)
        {
            if (list == null || list.Count == 0)
                throw new InvalidOperationException("No providers available");

            if (!string.IsNullOrEmpty(plugin))
            {
                var hit = list.FirstOrDefault(i => string.Equals(i.plugin, plugin, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(hit.plugin)) return hit;
            }
            return list.First();
        }
    }

    // ---------- построение VoiceTpl ----------

    static class VoiceBuilder
    {
        public static VoiceTpl Build(
            string host,
            List<(string name, string url, string plugin, int index)> list,
            (string name, string url, string plugin, int index) active,
            long id, string imdb_id, long kp, string title, string original_title, string original_language, int year, int serial,
            int s, string t, bool rjson)
        {
            var vtpl = new VoiceTpl();

            foreach (var it in list)
            {
                string link = host + "/lite/smartfilter?" +
                    $"id={id}&imdb_id={HttpUtility.UrlEncode(imdb_id)}&kinopoisk_id={kp}" +
                    $"&title={HttpUtility.UrlEncode(title)}&original_title={HttpUtility.UrlEncode(original_title)}" +
                    $"&original_language={HttpUtility.UrlEncode(original_language)}&year={year}&serial={serial}" +
                    $"&s={s}&t={HttpUtility.UrlEncode(t)}&p={HttpUtility.UrlEncode(it.plugin)}&rjson={(rjson ? "true" : "false")}";

                vtpl.Append(it.name, it.plugin == active.plugin, link);
            }
            return vtpl;
        }
    }

    // ---------- прокси провайдеров ----------

    static class ProviderProxy
    {
        public static async Task<(string type, List<JObject>? episodes, string? html, string? rawJson)> FetchAsync(
            HttpContext ctx, IMemoryCache cache, string url, int s, string t, bool rjson, int timeoutMs)
        {
            url = UrlUtil.EnsureQuery(url, "s", s.ToString());
            if (!string.IsNullOrEmpty(t)) url = UrlUtil.EnsureQuery(url, "t", t);

            string ckey = "sf:prov:" + url + $"|rjson={rjson}";
            if (cache.TryGetValue(ckey, out (string type, List<JObject>? episodes, string? html, string? rawJson) cached))
                return cached;

            var header = HeadersModel.Init(("localrequest", AppInit.rootPasswd));

            if (!rjson)
            {
                string html = await Http.Get(url, timeoutSeconds: Math.Max(2, timeoutMs / 1000), headers: header);
                var res = (type: "html", episodes: (List<JObject>?)null, html: html ?? string.Empty, rawJson: (string?)null);
                cache.Set(ckey, res, TimeSpan.FromMinutes(ModInit.conf.cacheMinutes));
                return res;
            }

            string json = await Http.Get(url, timeoutSeconds: Math.Max(2, timeoutMs / 1000), headers: header);
            if (string.IsNullOrEmpty(json))
            {
                (string type, List<JObject> episodes, string? html, string rawJson) empty =
                    ("episode", new List<JObject>(), null, "{}");
                cache.Set(ckey, empty, TimeSpan.FromMinutes(5));
                return empty;
            }

            try
            {
                var root = JToken.Parse(json);
                string type = root.Value<string>("type") ?? DetectType(root);

                if (type == "episode")
                {
                    var data = root["data"] as JArray ?? new JArray();
                    var list = data.OfType<JObject>().ToList();
                    var res = (type: "episode", episodes: list, html: (string?)null, rawJson: json);
                    cache.Set(ckey, res, TimeSpan.FromMinutes(ModInit.conf.cacheMinutes));
                    return res;
                }

                var pass = (type: type ?? "unknown", episodes: (List<JObject>?)null, html: (string?)null, rawJson: json);
                cache.Set(ckey, pass, TimeSpan.FromMinutes(ModInit.conf.cacheMinutes));
                return pass;
            }
            catch
            {
                var bad = (type: "unknown", episodes: (List<JObject>?)null, html: (string?)null, rawJson: (string?)null);
                cache.Set(ckey, bad, TimeSpan.FromMinutes(5));
                return bad;
            }
        }

        public static async Task<List<JObject>> CollectEpisodesAsync(
            HttpContext ctx, IMemoryCache cache,
            List<(string name, string url, string plugin, int index)> providers,
            int s, string t, int timeoutMs, int parallel)
        {
            var results = new ConcurrentBag<JObject>();
            using var cts = new CancellationTokenSource(timeoutMs);
            var throttler = new SemaphoreSlim(Math.Max(1, parallel));

            var tasks = providers.Select(async p =>
            {
                try
                {
                    await throttler.WaitAsync(cts.Token);
                    try
                    {
                        var one = await FetchAsync(ctx, cache, p.url, s, t, true, timeoutMs);
                        if (one.type == "episode" && one.episodes != null)
                        {
                            foreach (var ep in one.episodes)
                            {
                                ep["details"] ??= p.name;
                                results.Add(ep);
                            }
                        }
                    }
                    finally { throttler.Release(); }
                }
                catch (OperationCanceledException) { /* общий таймаут */ }
                catch { }
            });

            await Task.WhenAll(tasks);
            return results.ToList();
        }

        static string DetectType(JToken root)
        {
            if (root["data"] is JArray arr)
            {
                var first = arr.FirstOrDefault() as JObject;
                if (first?["e"] != null) return "episode";
                if (first?["s"] != null || first?["season"] != null || first?["order"] != null) return "season";
            }
            return "unknown";
        }
    }

    // ---------- качество: кэш IMemoryCache + md5 ключи + приоритеты + синонимы + логирование ----------

    static class QualityHelper
    {
        private static readonly Dictionary<string, string[]> QualitySynonyms = new()
        {
            { "2160p", new[] { "2160p", "4K", "UHD", "Ultra HD", "4K HDR" } },
            { "1440p", new[] { "1440p", "2K", "QHD" } },
            { "1080p", new[] { "1080p", "1080p Ultra", "Full HD", "FHD" } },
            { "720p",  new[] { "720p", "HD" } },
            { "480p",  new[] { "480p", "SD" } },
            { "360p",  new[] { "360p" } }
        };

        private static readonly ConcurrentDictionary<string, int> _errorCounts = new();

        public static (StreamQualityTpl tpl, string firstLink) Build(
            IMemoryCache memoryCache,
            JToken quality,
            string[] qualityPriority,
            bool enablePriority,
            bool allow4K,
            bool allowHDR,
            Action<string, Exception> logger = null,
            string provider = null)
        {
            if (quality == null)
            {
                Log(logger, provider, "Quality JSON is null");
                return (new StreamQualityTpl(), null);
            }

            string raw = quality.ToString(Formatting.None);
            string flags = $"{enablePriority}_{allow4K}_{allowHDR}";
            string pr = qualityPriority != null ? string.Join(",", qualityPriority) : "";
            string cacheKey = $"qh:v{ModInit.ConfigVersion}:{CrypTo.md5(raw)}:{CrypTo.md5(pr)}:{flags}";

            if (memoryCache.TryGetValue(cacheKey, out (StreamQualityTpl tpl, string firstLink) cached))
                return cached;

            var result = BuildInternal(quality, qualityPriority, enablePriority, allow4K, allowHDR, logger, provider);

            memoryCache.Set(cacheKey, result, TimeSpan.FromMinutes(ModInit.conf.cacheMinutes));
            return result;
        }

        private static (StreamQualityTpl tpl, string firstLink) BuildInternal(
            JToken quality,
            string[] qualityPriority,
            bool enablePriority,
            bool allow4K,
            bool allowHDR,
            Action<string, Exception> logger,
            string provider)
        {
            var tpl = new StreamQualityTpl();
            var items = new List<(string label, string link)>();

            try
            {
                if (quality is JArray arr)
                {
                    foreach (var q in arr.OfType<JObject>())
                    {
                        try
                        {
                            string link = q.Value<string>("link") ?? q.Value<string>("url");
                            string label = q.Value<string>("name") ?? q.Value<string>("label") ?? q.Value<string>("quality");

                            if (string.IsNullOrEmpty(link))
                            {
                                Log(logger, provider, $"Missing link in quality object: {Sanitize(q)}");
                                continue;
                            }

                            if (string.IsNullOrEmpty(label))
                            {
                                Log(logger, provider, $"Missing label in quality object: {Sanitize(q)}");
                                continue;
                            }

                            if (!allow4K && IsHighResolution(label))
                                continue;

                            if (!allowHDR && IsHdr(label))
                                continue;

                            items.Add((label, link));
                        }
                        catch (Exception ex)
                        {
                            Log(logger, provider, $"Failed to parse quality item: {Sanitize(q)}", ex);
                        }
                    }
                }
                else
                {
                    Log(logger, provider, $"Unexpected quality format (not JArray): {quality.Type}");
                }

                if (items.Count == 0)
                    Log(logger, provider, $"No valid quality items found in: {Sanitize(quality)}");
            }
            catch (Exception ex)
            {
                Log(logger, provider, $"Critical error in QualityHelper.Build: {Sanitize(quality)}", ex);
            }

            if (enablePriority && qualityPriority != null && qualityPriority.Length > 0 && items.Count > 1)
            {
                items = items
                    .OrderBy(i => GetQualityPriority(i.label, qualityPriority))
                    .ThenByDescending(i => NumericRes(i.label))
                    .ToList();
            }

            foreach (var it in items)
                tpl.Append(it.link, it.label);

            return (tpl, items.Count > 0 ? items[0].link : null);
        }

        private static int GetQualityPriority(string label, string[] priorities)
        {
            for (int idx = 0; idx < priorities.Length; idx++)
            {
                if (QualitySynonyms.TryGetValue(priorities[idx], out var syn) &&
                    syn.Any(s => label.Contains(s, StringComparison.OrdinalIgnoreCase)))
                    return idx;

                if (label.Contains(priorities[idx], StringComparison.OrdinalIgnoreCase))
                    return idx;
            }
            return int.MaxValue;
        }

        private static bool IsHighResolution(string label) =>
            HasSyn(label, "2160p") || HasSyn(label, "1440p");

        private static bool IsHdr(string label) =>
            Regex.IsMatch(label, "\\bHDR\\b|Dolby\\s*Vision|\\bDV\\b|\\bHLG\\b", RegexOptions.IgnoreCase);

        private static bool HasSyn(string label, string key) =>
            QualitySynonyms.TryGetValue(key, out var syn) && syn.Any(s => label.Contains(s, StringComparison.OrdinalIgnoreCase));

        private static int NumericRes(string label)
        {
            var m = Regex.Match(label, "(\\d{3,4})");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        private static string Sanitize(JToken t) =>
            Regex.Replace(t?.ToString(Formatting.None) ?? "", @"([?&])(token|sig|signature|key|auth)=[^&]+", "$1$2=***", RegexOptions.IgnoreCase);

        private static void Log(Action<string, Exception> logger, string provider, string msg, Exception ex = null)
        {
            if (logger == null) return;
            string key = $"{provider ?? "unknown"}:{msg}";
            int n = _errorCounts.AddOrUpdate(key, 1, (_, v) => v + 1);
            if (n == 1 || n % 100 == 0)
                logger($"[QualityHelper][{provider ?? "unknown"}] {msg} (x{n})", ex);
        }
    }

    // ---------- сезоны ----------

    static class SeasonHelper
    {
        public static (SeasonTpl tpl, bool ok) FromJson(
            string host, string rawJson,
            long id, string imdb_id, long kp, string title, string original_title, string original_language, int year, int serial,
            string t, bool rjson)
        {
            var tpl = new SeasonTpl();
            try
            {
                var root = JToken.Parse(rawJson);
                var arr = root["data"] as JArray;
                if (arr == null || arr.Count == 0) return (tpl, false);

                var seen = new HashSet<int>();
                foreach (var item in arr.OfType<JObject>())
                {
                    int season =
                          item.Value<int?>("s")
                        ?? item.Value<int?>("season")
                        ?? item.Value<int?>("order")
                        ?? 0;

                    if (season <= 0 || !seen.Add(season))
                        continue;

                    string link = host + "/lite/smartfilter?" +
                        $"id={id}&imdb_id={HttpUtility.UrlEncode(imdb_id)}&kinopoisk_id={kp}" +
                        $"&title={HttpUtility.UrlEncode(title)}&original_title={HttpUtility.UrlEncode(original_title)}" +
                        $"&original_language={HttpUtility.UrlEncode(original_language)}&year={year}&serial={serial}" +
                        $"&s={season}&t={HttpUtility.UrlEncode(t)}&rjson={(rjson ? "true" : "false")}";

                    tpl.Append($"{season} сезон", link, season);
                }

                return (tpl, tpl.data != null && tpl.data.Count > 0);
            }
            catch { return (tpl, false); }
        }
    }

    // ---------- склейка/дедуп ----------

    static class ResultMerge
    {
        public static EpisodeTpl ToEpisodeTpl(IMemoryCache memoryCache, IEnumerable<JObject> src)
        {
            var etpl = new EpisodeTpl();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var conf = ModInit.conf;

            foreach (var ep in src)
            {
                string name = ep.Value<string>("name") ?? "Эпизод";
                string title = ep.Value<string>("title") ?? "";
                string s = SafeInt(ep["s"], 1).ToString();
                string e = SafeInt(ep["e"], 1).ToString();

                string link = ep.Value<string>("stream") ?? ep.Value<string>("url") ?? ep.Value<string>("link");
                if (string.IsNullOrEmpty(link)) continue;

                string key = $"{s}:{e}:{Norm(link)}";
                if (seen.Contains(key)) continue;
                seen.Add(key);

                string provider = ep.Value<string>("details");

                var (qtpl, preferLink) = QualityHelper.Build(
                    memoryCache,
                    ep["quality"],
                    conf.qualityPriority,
                    conf.enableQualityPriority,
                    conf.allow4K,
                    conf.allowHDR,
                    (msg, ex) => Console.WriteLine($"[QualityHelper] {msg}" + (ex != null ? $"\n{ex}" : "")),
                    provider);

                if (!string.IsNullOrEmpty(preferLink))
                    link = preferLink;

                SubtitleTpl? subtitles = null;
                if (ep["subtitles"] is JArray subsArr && subsArr.Count > 0)
                {
                    var stpl = new SubtitleTpl(subsArr.Count);
                    foreach (var sub in subsArr.OfType<JObject>())
                    {
                        string label = sub.Value<string>("label") ?? sub.Value<string>("lang") ?? "Unknown";
                        string url = sub.Value<string>("url");
                        if (!string.IsNullOrEmpty(url))
                            stpl.Append(label, url);
                    }

                    if (!stpl.IsEmpty())
                        subtitles = stpl;
                }
                string method = ep.Value<string>("method") == "call" ? "call" : "play";
                string streamlink = ep.Value<string>("stream") ?? ep.Value<string>("url");

                etpl.Append(name, title, s, e, link, method, qtpl, subtitles,
                            streamlink: streamlink,
                            voice_name: provider);
            }

            return etpl;

            static int SafeInt(JToken t, int def)
                => t != null && int.TryParse(t.ToString(), out var v) ? v : def;

            static string Norm(string u)
                => Regex.Replace(u ?? "", "[?&]?(sig|token|expires|ts)=[^&]+", "", RegexOptions.IgnoreCase);
        }

        public static EpisodeTpl FromMovie(IMemoryCache memoryCache, string rawJson, string title, int s = 1)
        {
            var etpl = new EpisodeTpl();
            var conf = ModInit.conf;

            try
            {
                var root = JToken.Parse(rawJson);
                var arr = root["data"] as JArray;
                if (arr == null || arr.Count == 0)
                    return etpl;

                var m = arr.First as JObject;
                string link = m?.Value<string>("stream") ?? m?.Value<string>("url") ?? m?.Value<string>("link");
                if (string.IsNullOrEmpty(link))
                    return etpl;

                var (qtpl, preferLink) = QualityHelper.Build(
                    memoryCache,
                    m?["quality"],
                    conf.qualityPriority,
                    conf.enableQualityPriority,
                    conf.allow4K,
                    conf.allowHDR,
                    (msg, ex) => Console.WriteLine($"[QualityHelper] {msg}" + (ex != null ? $"\n{ex}" : "")),
                    provider: m?.Value<string>("details"));

                if (!string.IsNullOrEmpty(preferLink))
                    link = preferLink;

                SubtitleTpl? subtitles = null;
                if (m?["subtitles"] is JArray subsArr && subsArr.Count > 0)
                {
                    var stpl = new SubtitleTpl(subsArr.Count);
                    foreach (var sub in subsArr.OfType<JObject>())
                    {
                        string label = sub.Value<string>("label") ?? sub.Value<string>("lang") ?? "Unknown";
                        string url = sub.Value<string>("url");
                        if (!string.IsNullOrEmpty(url))
                            stpl.Append(label, url);
                    }

                    if (!stpl.IsEmpty())
                        subtitles = stpl;
                }
                string method = m?.Value<string>("method") == "call" ? "call" : "play";
                string streamlink = m?.Value<string>("stream") ?? m?.Value<string>("url");

                etpl.Append("Фильм", title ?? "Movie", s.ToString(), "1", link, method, qtpl, subtitles, streamlink: streamlink);
                return etpl;
            }
            catch { return etpl; }
        }
    }

    // ---------- утилиты ----------

    static class UrlUtil
    {
        public static string EnsureQuery(string url, string key, string value)
        {
            if (string.IsNullOrEmpty(url)) return url;
            url = Regex.Replace(url, $"([?&]){Regex.Escape(key)}=[^&]*", $"$1{key}={HttpUtility.UrlEncode(value)}");
            if (!Regex.IsMatch(url, $"[?&]{Regex.Escape(key)}="))
                url += (url.Contains("?") ? "&" : "?") + $"{key}={HttpUtility.UrlEncode(value)}";
            return url;
        }
    }
}
