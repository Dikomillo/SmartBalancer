using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Shared.Models;
using Shared.Models.Module;
using System.Collections.Generic;
using System.Web;

namespace SmartFilter
{
    public class OnlineApi
    {
        public static List<(string name, string url, string plugin, int index)> Invoke(
            HttpContext httpContext,
            IMemoryCache memoryCache,
            RequestModel requestInfo,
            string host,
            OnlineEventsModel a)
        {
            string q =
                $"id={a.id}&imdb_id={HttpUtility.UrlEncode(a.imdb_id)}&kinopoisk_id={a.kinopoisk_id}" +
                $"&title={HttpUtility.UrlEncode(a.title)}&original_title={HttpUtility.UrlEncode(a.original_title)}" +
                $"&original_language={HttpUtility.UrlEncode(a.original_language)}&year={a.year}&serial={a.serial}";
            string url = $"{host}/lite/smartfilter?{q}";
            return new List<(string,string,string,int)>
            {
                ("SmartFilter", url, "smartfilter", 0)
            };
        }
    }
}
