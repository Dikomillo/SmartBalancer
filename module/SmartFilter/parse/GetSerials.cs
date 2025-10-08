using Newtonsoft.Json.Linq;  
using Shared.Models.Templates;  
using System;  
using System.Collections.Generic;  
using System.Linq;  
using System.Text.RegularExpressions;  
  
namespace SmartFilter.parse  
{  
    public static class GetSerials  
    {  
        public static object Process(List<ProviderResult> validResults, string title, string original_title, string host, string queryString)  
        {  
            var seasons = new List<JObject>();  
            var episodes = new List<JObject>();  
              
            // Определяем, запрашиваем ли мы конкретный сезон  
            var queryParams = System.Web.HttpUtility.ParseQueryString(queryString);  
            int requestedSeason = 0;  
            if (int.TryParse(queryParams["s"], out int s))  
                requestedSeason = s;  
  
            Console.WriteLine($"📺 SmartFilter: Processing serials for '{title}' - requested season: {requestedSeason}");  
  
            foreach (var result in validResults)  
            {  
                try  
                {  
                    var json = JObject.Parse(result.JsonData);  
                    string type = json["type"]?.ToString();  
  
                    if (type == "season" && json["data"] is JArray seasonArray)  
                    {  
                        Console.WriteLine($"🎬 SmartFilter: Found {seasonArray.Count} seasons from {result.ProviderName}");  
                          
                        foreach (var item in seasonArray)  
                        {  
                            var seasonId = item["id"] ?? item["s"] ?? item["season"] ?? 1;  
                            var seasonNumber = item["season"] ?? item["s"] ?? seasonId;  
                            var seasonTitle = item["title"]?.ToString() ?? $"Сезон {seasonNumber}";  
                              
                            seasons.Add(new JObject  
                            {  
                                ["id"] = seasonId,  
                                ["name"] = seasonTitle,  
                                ["url"] = $"{host}/lite/smartfilter{queryString.Replace("rjson=true", "").TrimEnd('&')}&s={seasonNumber}",  
                                ["provider"] = result.ProviderName  
                            });  
                        }  
                    }  
                    else if (type == "episode" && json["data"] is JArray episodeArray && requestedSeason > 0)  
                    {  
                        Console.WriteLine($"📹 SmartFilter: Found {episodeArray.Count} episodes from {result.ProviderName}");  
                          
                        foreach (var item in episodeArray)  
                        {  
                            var episodeNumber = item["episode"] ?? item["e"] ?? episodes.Count + 1;  
                            var episodeTitle = item["title"]?.ToString() ?? $"Серия {episodeNumber}";  
                            var translate = item["translate"]?.ToString() ?? item["voice"]?.ToString() ?? "Оригинал";  
                            var cleanVoice = ExtractCleanVoice(translate);  
                              
                            var episodeObj = new JObject  
                            {  
                                ["title"] = $"{episodeTitle} ({cleanVoice})",  
                                ["url"] = item["url"]?.ToString(),  
                                ["stream"] = item["stream"]?.ToString(),  
                                ["quality"] = item["maxquality"]?.ToString() ?? item["quality"]?.ToString(),  
                                ["translate"] = cleanVoice,  
                                ["provider"] = result.ProviderName,  
                                ["episode"] = episodeNumber  
                            };  
  
                            // Добавляем объект качеств если есть  
                            if (item["quality"] is JObject qualityObj && qualityObj.HasValues)  
                            {  
                                episodeObj["qualities"] = qualityObj;  
                            }  
  
                            episodes.Add(episodeObj);  
                        }  
                    }  
                    else if (type == "movie" && json["data"] is JArray movieArray)  
                    {  
                        // Обрабатываем как сезоны для сериалов, которые возвращают movie формат  
                        Console.WriteLine($"🎭 SmartFilter: Processing movie format as seasons from {result.ProviderName}");  
                          
                        var groupedBySeason = movieArray  
                            .Where(item => item["season"] != null)  
                            .GroupBy(item => item["season"]?.ToString() ?? "1")  
                            .ToList();  
  
                        foreach (var seasonGroup in groupedBySeason)  
                        {  
                            seasons.Add(new JObject  
                            {  
                                ["id"] = seasonGroup.Key,  
                                ["name"] = $"Сезон {seasonGroup.Key}",  
                                ["url"] = $"{host}/lite/smartfilter{queryString.Replace("rjson=true", "").TrimEnd('&')}&s={seasonGroup.Key}",  
                                ["provider"] = result.ProviderName,  
                                ["episodes_count"] = seasonGroup.Count()  
                            });  
                        }  
                    }  
                }  
                catch (Exception ex)  
                {  
                    Console.WriteLine($"❌ SmartFilter: Error parsing provider {result.ProviderName}: {ex.Message}");  
                }  
            }  
  
            if (requestedSeason > 0)  
            {  
                Console.WriteLine($"📊 SmartFilter: Returning {episodes.Count} episodes for season {requestedSeason}");  
                return new  
                {  
                    type = "episode",  
                    data = episodes.OrderBy(e => e["episode"]).ToArray()  
                };  
            }  
            else  
            {  
                Console.WriteLine($"📊 SmartFilter: Returning {seasons.Count} seasons");  
                return new  
                {  
                    type = "season",   
                    data = seasons.OrderBy(s => int.Parse(s["id"]?.ToString() ?? "1")).ToArray()  
                };  
            }  
        }  
  
        private static string ExtractCleanVoice(string translate, string maxQuality = "")  
        {  
            if (string.IsNullOrWhiteSpace(translate))   
                return "Оригинал";  
  
            string result = translate;  
  
            // Удаляем упоминание качества, если оно совпадает с maxQuality  
            if (!string.IsNullOrEmpty(maxQuality))  
            {  
                result = Regex.Replace(result,   
                    @"\b" + Regex.Escape(maxQuality) + @"\b", "",   
                    RegexOptions.IgnoreCase);  
            }  
  
            // Удаляем паттерны качества  
            var qualityPatterns = new[]   
            {  
                @"\b\d{3,4}p?\b",   
                @"\bHD\b",   
                @"\bFullHD\b",   
                @"\b4K\b",   
                @"\bUltra HD\b",   
                @"\bHDRip\b",   
                @"\bBDRip\b",   
                @"\bWEB-DL\b",   
                @"\bWEBRip\b",  
                @"\bSDR\b",  
                @"\bHDR\b"  
            };  
  
            foreach (var pattern in qualityPatterns)  
            {  
                result = Regex.Replace(result, pattern, "", RegexOptions.IgnoreCase);  
            }  
  
            // Удаляем год в скобках  
            result = Regex.Replace(result, @"\s*\(.*?\d{4}.*?\)\s*", " ", RegexOptions.IgnoreCase);  
            result = Regex.Replace(result, @"\s*\b\d{4}\b\s*", " ");  
  
            // Удаляем лишние символы и пробелы  
            result = Regex.Replace(result, @"^\s*[-/|—•\[\]]+\s*|\s*[-/|—•\[\]]+\s*$", "");  
            result = Regex.Replace(result, @"\s*[-/|—•]\s*", ", ");  
            result = Regex.Replace(result, @"\s*,\s*,\s*", ", ");  
            result = Regex.Replace(result, @"\s+", " ").Trim();  
  
            if (string.IsNullOrWhiteSpace(result) || Regex.IsMatch(result, @"^[\s,\.]+$"))  
                return "Оригинал";  
  
            return result;  
        }  
    }  
}