using Newtonsoft.Json.Linq;  
using Shared.Models.Templates;  
using System;  
using System.Collections.Generic;  
using System.Text.RegularExpressions;  
using Newtonsoft.Json;  
  
namespace SmartFilter.parse  
{  
    public static class GetCinema  
    {  
        public static MovieTpl Process(List<ProviderResult> validResults, string title, string original_title)  
        {  
            var mtpl = new MovieTpl(title, original_title);  
            int processedItems = 0;  
            int skippedHtml = 0;  
  
            foreach (var result in validResults)  
            {  
                try  
                {  
                    // Проверяем HTML ответы  
                    if (IsHtmlResponse(result.JsonData))  
                    {  
                        Console.WriteLine($"⚠️ SmartFilter: Skipping HTML response from {result.ProviderName}");  
                        skippedHtml++;  
                        continue;  
                    }  
  
                    // Обрабатываем разные форматы ответов провайдеров  
                    JArray dataArray = null;  
  
                    // Сначала пытаемся определить, является ли ответ массивом  
                    if (result.JsonData.TrimStart().StartsWith("["))  
                    {  
                        // Прямой массив - парсим как JArray  
                        dataArray = JArray.Parse(result.JsonData);  
                    }  
                    else  
                    {  
                        // Парсим как JObject  
                        var json = JObject.Parse(result.JsonData);  
                          
                        // Стандартный формат с type: "movie"  
                        if (json["type"]?.ToString() == "movie" && json["data"] is JArray movieData)  
                        {  
                            dataArray = movieData;  
                        }  
                        // Формат без type, но с data массивом  
                        else if (json["data"] is JArray directData)  
                        {  
                            dataArray = directData;  
                        }  
                    }  
  
                    if (dataArray != null)  
                    {  
                        foreach (var item in dataArray)  
                        {  
                            string rawTranslate = item["translate"]?.ToString() ??   
                                                item["voice"]?.ToString() ??   
                                                item["translation"]?.ToString() ??   
                                                item["translation_name"]?.ToString() ??   
                                                "Оригинал";  
                              
                            string link = item["url"]?.ToString()?.Trim();  
                            string method = item["method"]?.ToString() ?? "play";  
                            string stream = item["stream"]?.ToString();  
                            string maxQuality = item["maxquality"]?.ToString() ??   
                                              item["max_quality"]?.ToString() ??   
                                              item["quality"]?.ToString() ?? "";  
                            string provider = result.ProviderName;  
  
                            if (string.IsNullOrEmpty(link))  
                                continue;  
  
                            string cleanVoice = ExtractCleanVoice(rawTranslate, maxQuality);  
  
                            // Извлекаем объект качеств (если есть)  
                            StreamQualityTpl? streamquality = null;  
                            if (item["quality"] is JObject qualityObj && qualityObj.HasValues)  
                            {  
                                var qualityStreams = new List<(string link, string quality)>();  
                                foreach (var q in qualityObj.Children<JProperty>())  
                                {  
                                    qualityStreams.Add((q.Value.ToString(), q.Name));  
                                }  
                                if (qualityStreams.Count > 0)  
                                    streamquality = new StreamQualityTpl(qualityStreams);  
                            }  
  
                            mtpl.Append(  
                                voiceOrQuality: cleanVoice,  
                                link: link,  
                                method: method,  
                                stream: stream,  
                                quality: maxQuality,  
                                details: provider,  
                                streamquality: streamquality  
                            );  
                              
                            processedItems++;  
                        }  
                    }  
                }  
                catch (JsonException ex)  
                {  
                    Console.WriteLine($"📛 SmartFilter: JSON parse error from {result.ProviderName}: {ex.Message}");  
                }  
                catch (Exception ex)  
                {  
                    Console.WriteLine($"❌ SmartFilter: Unexpected error from {result.ProviderName}: {ex.Message}");  
                }  
            }  
  
            Console.WriteLine($"✅ SmartFilter: Processed {processedItems} cinema items from {validResults.Count} providers, skipped {skippedHtml} HTML responses");  
            return mtpl;  
        }  
  
        private static bool IsHtmlResponse(string response)  
        {  
            if (string.IsNullOrEmpty(response))  
                return false;  
  
            return response.StartsWith("<") ||   
                   response.Contains("<!DOCTYPE") ||   
                   response.Contains("<html") ||   
                   response.Contains("<body");  
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
                @"\bWEBRip\b"  
            };  
  
            foreach (var pattern in qualityPatterns)  
            {  
                result = Regex.Replace(result, pattern, "", RegexOptions.IgnoreCase);  
            }  
  
            // Удаляем год в скобках  
            result = Regex.Replace(result, @"\s*\(.*?\d{4}.*?\)\s*", " ", RegexOptions.IgnoreCase);  
            result = Regex.Replace(result, @"\s*\b\d{4}\b\s*", " ");  
  
            // Удаляем жанры  
            var genreWords = new[]   
            {  
                "триллер", "детектив", "боевик", "драма", "комедия", "мелодрама", "фантастика", "фэнтези", "ужасы", "приключения",   
                "биография", "военный", "исторический", "криминал", "семейный", "спорт"  
            };  
  
            foreach (var genre in genreWords)  
            {  
                result = Regex.Replace(result, @"\b" + genre + @"\b", "", RegexOptions.IgnoreCase);  
            }  
  
            // Удаляем служебные слова  
            var noiseWords = new[]   
            {  
                "фильм", "сериал", "аниме", "мультфильм", "online", "онлайн", "смотреть", "movie", "series", "anime", "cartoon",   
                "watch", "stream", "full", "hd", "орудия", "weapons", "the", "and", "or", "in", "on", "at", "by", "for",   
                "новый", "лучший", "русский", "английский", "немецкий", "французский"  
            };  
  
            foreach (var word in noiseWords)  
            {  
                result = Regex.Replace(result, @"\b" + word + @"\b", "", RegexOptions.IgnoreCase);  
            }  
  
            // Удаляем лишние символы  
            result = Regex.Replace(result, @"^\s*[-/|—•]+\s*|\s*[-/|—•]+\s*$", "");  
            result = Regex.Replace(result, @"\s*[-/|—•]\s*", ", ");  
  
            // Убираем лишние пробелы  
            result = Regex.Replace(result, @"\s+", " ").Trim();  
  
            if (string.IsNullOrWhiteSpace(result) || Regex.IsMatch(result, @"^[\s,\.]+$"))  
                return "Оригинал";  
  
            return result;  
        }  
    }  
}