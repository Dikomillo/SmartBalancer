using System;
using System.IO;
using System.Text.Json;
using Shared.Models.Module;

namespace SmartFilter
{
    public class ModInit
    {
        static (ModInit, DateTime) cacheconf = default;

        public static ModInit conf
        {
            get
            {
                string configPath = "module/SmartFilter/SmartFilter.conf";
                if (cacheconf.Item1 == null)
                {
                    if (!File.Exists(configPath))
                    {
                        Console.WriteLine($"SmartFilter: Config file {configPath} not found, using defaults");
                        return new ModInit();
                    }
                }

                var lastWriteTime = File.Exists(configPath) ? File.GetLastWriteTime(configPath) : DateTime.MinValue;
                if (cacheconf.Item2 != lastWriteTime)
                {
                    try
                    {
                        if (File.Exists(configPath))
                        {
                            string json = File.ReadAllText(configPath);
                            if (!string.IsNullOrEmpty(json))
                            {
                                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                                cacheconf.Item1 = JsonSerializer.Deserialize<ModInit>(json, options);
                                Console.WriteLine($"SmartFilter: Config loaded from {configPath}");
                            }
                        }

                        if (cacheconf.Item1 == null)
                            cacheconf.Item1 = new ModInit();

                        cacheconf.Item2 = lastWriteTime;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"SmartFilter: Error loading config: {ex.Message}");
                        cacheconf.Item1 = new ModInit();
                        cacheconf.Item2 = lastWriteTime;
                    }
                }

                return cacheconf.Item1;
            }
        }

        public static void loaded(InitspaceModel init)
        {
            try
            {
                Directory.CreateDirectory("cache/smartfilter");
                var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                File.WriteAllText("module/SmartFilter/SmartFilter.current.conf", JsonSerializer.Serialize(conf, options));

                Console.WriteLine("✅ SmartFilter Aggregator module loaded successfully");
                Console.WriteLine($"🔧 SmartFilter: Configuration - enable: {conf.enable}, maxParallelRequests: {conf.maxParallelRequests}");
                Console.WriteLine($"⏰ SmartFilter: Cache settings - cacheTimeMinutes: {conf.cacheTimeMinutes}");
                Console.WriteLine($"🌐 SmartFilter: Request timeout: {conf.requestTimeoutSeconds} seconds");
                Console.WriteLine($"🚫 SmartFilter: Excluded providers: [{string.Join(", ", conf.excludeProviders)}]");
                if (conf.includeOnlyProviders.Length > 0)
                    Console.WriteLine($"✅ SmartFilter: Include only providers: [{string.Join(", ", conf.includeOnlyProviders)}]");
                
                Console.WriteLine($"🔄 SmartFilter: Retry settings - enableRetry: {conf.enableRetry}, maxAttempts: {conf.maxRetryAttempts}");
                Console.WriteLine($"📝 SmartFilter: Detailed logging: {conf.detailedLogging}");
                
                // Добавляем лог готовности
                Console.WriteLine("🚀 SmartFilter: Module is ready and operational!");
                Console.WriteLine($"📍 SmartFilter: Available at /lite/smartfilter endpoint");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ SmartFilter: Error in loaded method: {ex.Message}");
            }
        }

        public bool enable { get; set; } = true;
        public int maxParallelRequests { get; set; } = 15;
        public int cacheTimeMinutes { get; set; } = 30;
        public int requestTimeoutSeconds { get; set; } = 30;
        public string[] excludeProviders { get; set; } = new string[0];
        public string[] includeOnlyProviders { get; set; } = new string[0];
        
        // Добавляем новые настройки
        public bool enableRetry { get; set; } = true;
        public int maxRetryAttempts { get; set; } = 3;
        public int retryDelayMs { get; set; } = 1000;
        public bool detailedLogging { get; set; } = true;
    }
}