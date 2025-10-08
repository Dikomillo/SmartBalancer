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
              
            results.Add(("SmartFilter Aggregator", url, "smartfilter", 0));  
              
            Console.WriteLine($"📡 SmartFilter: Registered provider at {url}");  
            return results;  
        }  
    }  
}