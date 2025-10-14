using Microsoft.AspNetCore.Http;  
using Microsoft.Extensions.Caching.Memory;  
using Shared.Models;  
using Shared.Models.Module;  
using System;  
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
            try  
            {  
                Console.WriteLine($"[SmartFilter] OnlineApi.Invoke called for: {a.title} (id={a.id})");  
                  
                if (!ModInit.conf.enable)  
                {  
                    Console.WriteLine("[SmartFilter] Module is disabled in config");  
                    return new List<(string, string, string, int)>();  
                }  
  
                string q =  
                    $"id={a.id}&imdb_id={HttpUtility.UrlEncode(a.imdb_id)}&kinopoisk_id={a.kinopoisk_id}" +  
                    $"&title={HttpUtility.UrlEncode(a.title)}&original_title={HttpUtility.UrlEncode(a.original_title)}" +  
                    $"&original_language={HttpUtility.UrlEncode(a.original_language)}&year={a.year}&serial={a.serial}";  
                  
                string url = $"{host}/lite/smartfilter?{q}";  
                  
                Console.WriteLine($"[SmartFilter] Registered provider URL: {url}");  
                  
                return new List<(string, string, string, int)>  
                {  
                    ("SmartFilter", url, "smartfilter", 0)  
                };  
            }  
            catch (Exception ex)  
            {  
                Console.WriteLine($"[SmartFilter] Error in OnlineApi.Invoke: {ex.Message}");  
                Console.WriteLine($"[SmartFilter] Stack trace: {ex.StackTrace}");  
                return new List<(string, string, string, int)>();  
            }  
        }  
    }  
}