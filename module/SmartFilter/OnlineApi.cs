using Microsoft.AspNetCore.Http;  
using Microsoft.Extensions.Caching.Memory;  
using Shared.Models;  
using Shared.Models.Module;  
using System;  
using System.Collections.Generic;  
  
namespace SmartFilter  
{  
    public class OnlineApi  
    {  
        public static List<(string name, string url, string plugin, int index)> Invoke(  
            HttpContext httpContext,  
            IMemoryCache memoryCache,  
            RequestModel requestInfo,  
            string host,  
            OnlineEventsModel args)  
        {  
            if (!ModInit.conf.enable)   
            {  
                Console.WriteLine("🚫 SmartFilter: Module is disabled");  
                return new List<(string name, string url, string plugin, int index)>();  
            }  
  
            var results = new List<(string name, string url, string plugin, int index)>();

            var url = $"{host}/lite/smartfilter";
            var token = ResolveToken(requestInfo, httpContext);

            if (!string.IsNullOrWhiteSpace(token))
            {
                var separator = url.Contains('?') ? "&" : "?";
                url = $"{url}{separator}token={Uri.EscapeDataString(token)}";
            }

            results.Add(("SmartFilter Aggregator", url, "smartfilter", 0));

            Console.WriteLine($"📡 SmartFilter: Registered provider at {url}");
            return results;
        }

        private static string ResolveToken(RequestModel requestInfo, HttpContext httpContext)
        {
            string token = null;

            if (!EqualityComparer<RequestModel>.Default.Equals(requestInfo, default))
            {
                var type = requestInfo.GetType();
                var property = type.GetProperty("token") ?? type.GetProperty("Token");
                token = property?.GetValue(requestInfo) as string;

                if (string.IsNullOrWhiteSpace(token))
                {
                    var field = type.GetField("token") ?? type.GetField("Token");
                    token = field?.GetValue(requestInfo) as string;
                }
            }

            if (string.IsNullOrWhiteSpace(token))
                token = httpContext?.Request?.Query["token"].ToString();

            if (string.IsNullOrWhiteSpace(token))
                token = httpContext?.Request?.Headers["token"].ToString();

            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
    }
}
