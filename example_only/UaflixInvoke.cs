using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shared;
using Shared.Models.Online.Settings;
using Shared.Models;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Uaflix.Controllers;
using Shared.Engine;
using Uaflix.Models;
using System.Linq;
using Shared.Models.Templates;
using System.Net;

namespace Uaflix
{
    public class UaflixInvoke
    {
        private OnlinesSettings _init;
        private HybridCache _hybridCache;
        private Action<string> _onLog;
        private ProxyManager _proxyManager;

        public UaflixInvoke(OnlinesSettings init, HybridCache hybridCache, Action<string> onLog, ProxyManager proxyManager)
        {
            _init = init;
            _hybridCache = hybridCache;
            _onLog = onLog;
            _proxyManager = proxyManager;
        }

        public async Task<List<SearchResult>> Search(string imdb_id, long kinopoisk_id, string title, string original_title, int year, string search_query)
        {
            string memKey = $"UaFlix:search:{kinopoisk_id}:{imdb_id}:{search_query}";
            if (_hybridCache.TryGetValue(memKey, out List<SearchResult> res))
                return res;

            try
            {
                string filmTitle = !string.IsNullOrEmpty(search_query) ? search_query : (!string.IsNullOrEmpty(title) ? title : original_title);
                string searchUrl = $"{_init.host}/index.php?do=search&subaction=search&story={System.Web.HttpUtility.UrlEncode(filmTitle)}";
                var headers = new List<HeadersModel>() { new HeadersModel("User-Agent", "Mozilla/5.0"), new HeadersModel("Referer", _init.host) };

                var searchHtml = await Http.Get(searchUrl, headers: headers, proxy: _proxyManager.Get());
                var doc = new HtmlDocument();
                doc.LoadHtml(searchHtml);

                var filmNodes = doc.DocumentNode.SelectNodes("//a[contains(@class, 'sres-wrap')]");
                if (filmNodes == null) return null;

                res = new List<SearchResult>();
                foreach (var filmNode in filmNodes)
                {
                    var h2Node = filmNode.SelectSingleNode(".//h2");
                    if (h2Node == null) continue;

                    string filmUrl = filmNode.GetAttributeValue("href", "");
                    if (string.IsNullOrEmpty(filmUrl)) continue;

                    if (!filmUrl.StartsWith("http"))
                        filmUrl = _init.host + filmUrl;

                    var descNode = filmNode.SelectSingleNode(".//div[contains(@class, 'sres-desc')]");
                    int.TryParse(Regex.Match(descNode?.InnerText ?? "", @"\d{4}").Value, out int filmYear);

                    var posterNode = filmNode.SelectSingleNode(".//img");
                    string posterUrl = posterNode?.GetAttributeValue("src", "");
                    if (!string.IsNullOrEmpty(posterUrl) && !posterUrl.StartsWith("http"))
                        posterUrl = _init.host + posterUrl;

                    res.Add(new SearchResult
                    {
                        Title = h2Node.InnerText.Trim(),
                        Url = filmUrl,
                        Year = filmYear,
                        PosterUrl = posterUrl
                    });
                }

                if (res.Count > 0)
                {
                    _hybridCache.Set(memKey, res, cacheTime(20));
                    return res;
                }
            }
            catch (Exception ex)
            {
                _onLog($"UaFlix search error: {ex.Message}");
            }
            return null;
        }
        
        public async Task<FilmInfo> GetFilmInfo(string filmUrl)
        {
            string memKey = $"UaFlix:filminfo:{filmUrl}";
            if (_hybridCache.TryGetValue(memKey, out FilmInfo res))
                return res;

            try
            {
                var headers = new List<HeadersModel>() { new HeadersModel("User-Agent", "Mozilla/5.0"), new HeadersModel("Referer", _init.host) };
                var filmHtml = await Http.Get(filmUrl, headers: headers, proxy: _proxyManager.Get());
                var doc = new HtmlDocument();
                doc.LoadHtml(filmHtml);
                
                var result = new FilmInfo
                {
                    Url = filmUrl
                };
                
                var titleNode = doc.DocumentNode.SelectSingleNode("//h1[@class='h1-title']");
                if (titleNode != null)
                {
                    result.Title = titleNode.InnerText.Trim();
                }
                
                var metaDuration = doc.DocumentNode.SelectSingleNode("//meta[@property='og:video:duration']");
                if (metaDuration != null)
                {
                    string durationStr = metaDuration.GetAttributeValue("content", "");
                    if (int.TryParse(durationStr, out int duration))
                    {
                        result.Duration = duration;
                    }
                }
                
                var metaActors = doc.DocumentNode.SelectSingleNode("//meta[@property='og:video:actor']");
                if (metaActors != null)
                {
                    string actorsStr = metaActors.GetAttributeValue("content", "");
                    result.Actors = actorsStr.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                          .Select(a => a.Trim())
                                          .ToList();
                }
                
                var metaDirector = doc.DocumentNode.SelectSingleNode("//meta[@property='og:video:director']");
                if (metaDirector != null)
                {
                    result.Director = metaDirector.GetAttributeValue("content", "");
                }
                
                var descNode = doc.DocumentNode.SelectSingleNode("//div[@id='main-descr']//div[@itemprop='description']");
                if (descNode != null)
                {
                    result.Description = descNode.InnerText.Trim();
                }
                
                var posterNode = doc.DocumentNode.SelectSingleNode("//img[@itemprop='image']");
                if (posterNode != null)
                {
                    result.PosterUrl = posterNode.GetAttributeValue("src", "");
                    if (!result.PosterUrl.StartsWith("http") && !string.IsNullOrEmpty(result.PosterUrl))
                    {
                        result.PosterUrl = _init.host + result.PosterUrl;
                    }
                }
                
                _hybridCache.Set(memKey, result, cacheTime(60));
                return result;
            }
            catch (Exception ex)
            {
                _onLog($"UaFlix GetFilmInfo error: {ex.Message}");
            }
            return null;
        }

        public async Task<PaginationInfo> GetPaginationInfo(string filmUrl)
        {
            string memKey = $"UaFlix:pagination:{filmUrl}";
            if (_hybridCache.TryGetValue(memKey, out PaginationInfo res))
                return res;

            try
            {
                var headers = new List<HeadersModel>() { new HeadersModel("User-Agent", "Mozilla/5.0"), new HeadersModel("Referer", _init.host) };
                var filmHtml = await Http.Get(filmUrl, headers: headers, proxy: _proxyManager.Get());
                var filmDoc = new HtmlDocument();
                filmDoc.LoadHtml(filmHtml);
                
                var paginationInfo = new PaginationInfo
                {
                    SerialUrl = filmUrl
                };

                var allEpisodes = new List<EpisodeLinkInfo>();
                var seasonUrls = new HashSet<string>();

                var seasonNodes = filmDoc.DocumentNode.SelectNodes("//div[contains(@class, 'sez-wr')]//a");
                if (seasonNodes == null)
                    seasonNodes = filmDoc.DocumentNode.SelectNodes("//div[contains(@class, 'fss-box')]//a");
                if (seasonNodes != null && seasonNodes.Count > 0)
                {
                    foreach (var node in seasonNodes)
                    {
                        string pageUrl = node.GetAttributeValue("href", null);
                        if (!string.IsNullOrEmpty(pageUrl))
                        {
                            if (!pageUrl.StartsWith("http"))
                                pageUrl = _init.host + pageUrl;
                            
                            seasonUrls.Add(pageUrl);
                        }
                    }
                }
                else
                {
                    seasonUrls.Add(filmUrl);
                }

                var seasonTasks = seasonUrls.Select(url => Http.Get(url, headers: headers, proxy: _proxyManager.Get()).AsTask());
                var seasonPagesHtml = await Task.WhenAll(seasonTasks);

                foreach (var html in seasonPagesHtml)
                {
                    var pageDoc = new HtmlDocument();
                    pageDoc.LoadHtml(html);

                    var episodeNodes = pageDoc.DocumentNode.SelectNodes("//div[contains(@class, 'frels')]//a[contains(@class, 'vi-img')]");
                    if (episodeNodes != null)
                    {
                        foreach (var episodeNode in episodeNodes)
                        {
                            string episodeUrl = episodeNode.GetAttributeValue("href", "");
                            if (!episodeUrl.StartsWith("http"))
                                episodeUrl = _init.host + episodeUrl;

                            var match = Regex.Match(episodeUrl, @"season-(\d+).*?episode-(\d+)");
                            if (match.Success)
                            {
                                allEpisodes.Add(new EpisodeLinkInfo
                                {
                                    url = episodeUrl,
                                    title = episodeNode.SelectSingleNode(".//div[@class='vi-rate']")?.InnerText.Trim() ?? $"Епізод {match.Groups[2].Value}",
                                    season = int.Parse(match.Groups[1].Value),
                                    episode = int.Parse(match.Groups[2].Value)
                                });
                            }
                        }
                    }
                }

                paginationInfo.Episodes = allEpisodes.OrderBy(e => e.season).ThenBy(e => e.episode).ToList();

                if (paginationInfo.Episodes.Any())
                {
                    var uniqueSeasons = paginationInfo.Episodes.Select(e => e.season).Distinct().OrderBy(se => se);
                    foreach (var season in uniqueSeasons)
                    {
                        paginationInfo.Seasons[season] = 1;
                    }
                }

                if (paginationInfo.Episodes.Count > 0)
                {
                    _hybridCache.Set(memKey, paginationInfo, cacheTime(20));
                    return paginationInfo;
                }
            }
            catch (Exception ex)
            {
                _onLog($"UaFlix GetPaginationInfo error: {ex.Message}");
            }
            return null;
        }
        
        public async Task<Uaflix.Models.PlayResult> ParseEpisode(string url)
        {
            var result = new Uaflix.Models.PlayResult() { streams = new List<(string, string)>() };
            try
            {
                string html = await Http.Get(url, headers: new List<HeadersModel>() { new HeadersModel("User-Agent", "Mozilla/5.0"), new HeadersModel("Referer", _init.host) }, proxy: _proxyManager.Get());
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var videoNode = doc.DocumentNode.SelectSingleNode("//video");
                if (videoNode != null)
                {
                    string videoUrl = videoNode.GetAttributeValue("src", "");
                    if (!string.IsNullOrEmpty(videoUrl))
                    {
                        result.streams.Add((videoUrl, "1080p"));
                        return result;
                    }
                }

                var iframe = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'video-box')]//iframe");
                if (iframe != null)
                {
                    string iframeUrl = iframe.GetAttributeValue("src", "").Replace("&", "&");
                    if (iframeUrl.StartsWith("//"))
                        iframeUrl = "https:" + iframeUrl;

                    if (iframeUrl.Contains("ashdi.vip/serial/"))
                    {
                        result.ashdi_url = iframeUrl;
                        return result;
                    }
                    
                    if (iframeUrl.Contains("zetvideo.net"))
                        result.streams = await ParseAllZetvideoSources(iframeUrl);
                    else if (iframeUrl.Contains("ashdi.vip"))
                    {
                        result.streams = await ParseAllAshdiSources(iframeUrl);
                        var idMatch = Regex.Match(iframeUrl, @"_(\d+)|vod/(\d+)");
                        if (idMatch.Success)
                        {
                            string ashdiId = idMatch.Groups[1].Success ? idMatch.Groups[1].Value : idMatch.Groups[2].Value;
                            result.subtitles = await GetAshdiSubtitles(ashdiId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _onLog($"ParseEpisode error: {ex.Message}");
            }
            _onLog($"ParseEpisode result: streams.count={result.streams.Count}, ashdi_url={result.ashdi_url}");
            return result;
        }

        async Task<List<(string link, string quality)>> ParseAllZetvideoSources(string iframeUrl)
        {
            var result = new List<(string link, string quality)>();
            var html = await Http.Get(iframeUrl, headers: new List<HeadersModel>() { new HeadersModel("User-Agent", "Mozilla/5.0"), new HeadersModel("Referer", "https://zetvideo.net/") }, proxy: _proxyManager.Get());
            if (string.IsNullOrEmpty(html)) return result;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            
            var script = doc.DocumentNode.SelectSingleNode("//script[contains(text(), 'file:')]");
            if (script != null)
            {
                var match = Regex.Match(script.InnerText, @"file:\s*""([^""]+\.m3u8)");
                if (match.Success)
                {
                    result.Add((match.Groups[1].Value, "1080p"));
                    return result;
                }
            }

            var sourceNodes = doc.DocumentNode.SelectNodes("//source[contains(@src, '.m3u8')]");
            if (sourceNodes != null)
            {
                foreach (var node in sourceNodes)
                {
                    result.Add((node.GetAttributeValue("src", null), node.GetAttributeValue("label", null) ?? node.GetAttributeValue("res", null) ?? "1080p"));
                }
            }
            return result;
        }

        async Task<List<(string link, string quality)>> ParseAllAshdiSources(string iframeUrl)
        {
            var result = new List<(string link, string quality)>();
            var html = await Http.Get(iframeUrl, headers: new List<HeadersModel>() { new HeadersModel("User-Agent", "Mozilla/5.0"), new HeadersModel("Referer", "https://ashdi.vip/") }, proxy: _proxyManager.Get());
             if (string.IsNullOrEmpty(html)) return result;
             
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var sourceNodes = doc.DocumentNode.SelectNodes("//source[contains(@src, '.m3u8')]");
            if (sourceNodes != null)
            {
                foreach (var node in sourceNodes)
                {
                    result.Add((node.GetAttributeValue("src", null), node.GetAttributeValue("label", null) ?? node.GetAttributeValue("res", null) ?? "1080p"));
                }
            }
            return result;
        }

        async Task<SubtitleTpl?> GetAshdiSubtitles(string id)
        {
            var html = await Http.Get($"https://ashdi.vip/vod/{id}", headers: new List<HeadersModel>() { new HeadersModel("User-Agent", "Mozilla/5.0"), new HeadersModel("Referer", "https://ashdi.vip/") }, proxy: _proxyManager.Get());
            string subtitle = new Regex("subtitle(\")?:\"([^\"]+)\"").Match(html).Groups[2].Value;
            if (!string.IsNullOrEmpty(subtitle))
            {
                var match = new Regex("\\[([^\\]]+)\\](https?://[^\\,]+)").Match(subtitle);
                var st = new Shared.Models.Templates.SubtitleTpl();
                while (match.Success)
                {
                    st.Append(match.Groups[1].Value, match.Groups[2].Value);
                    match = match.NextMatch();
                }
                if (!st.IsEmpty())
                    return st;
            }
            return null;
        }
        public static TimeSpan cacheTime(int multiaccess, int home = 5, int mikrotik = 2, OnlinesSettings init = null, int rhub = -1)
        {
            if (init != null && init.rhub && rhub != -1)
                return TimeSpan.FromMinutes(rhub);

            int ctime = AppInit.conf.mikrotik ? mikrotik : AppInit.conf.multiaccess ? init != null && init.cache_time > 0 ? init.cache_time : multiaccess : home;
            if (ctime > multiaccess)
                ctime = multiaccess;

            return TimeSpan.FromMinutes(ctime);
        }
    }
}