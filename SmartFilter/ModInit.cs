using Newtonsoft.Json;
using Shared.Models.Module;
using System;
using System.IO;
using System.Threading;

namespace SmartFilter
{
    public class ModInit
    {
        public static Conf conf = new Conf();

        // Версия конфигурации для инвалидации кэша
        private static int _configVersion = 0;
        public static int ConfigVersion => _configVersion;

        private static FileSystemWatcher? _fsw;
        private static string _moduleRoot = "module";

        private static string ModuleDirectory => Path.Combine(_moduleRoot, "SmartFilter");
        private static string CurrentConfigPath => Path.Combine(_moduleRoot, "SmartFilter.current.conf");
        private static string ExternalConfigPath => Path.Combine(_moduleRoot, "SmartFilter.conf");

        public static void InvalidateCache() => Interlocked.Increment(ref _configVersion);

        public static void loaded(InitspaceModel initspace)
        {
            _moduleRoot = Path.GetDirectoryName(initspace?.path ?? string.Empty) ?? "module";

            Directory.CreateDirectory(ModuleDirectory);

            // начальная запись текущей конфигурации
            File.WriteAllText(CurrentConfigPath, JsonConvert.SerializeObject(conf, Formatting.Indented));

            // попытка загрузить пользовательскую конфигурацию, если есть
            TryLoadExternalConf();
            InvalidateCache();

            // слежение за изменениями module/SmartFilter.conf
            try
            {
                _fsw = new FileSystemWatcher(_moduleRoot)
                {
                    Filter = "SmartFilter.conf",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName
                };
                _fsw.Changed += (_, __) => { if (TryLoadExternalConf()) InvalidateCache(); };
                _fsw.Created += (_, __) => { if (TryLoadExternalConf()) InvalidateCache(); };
                _fsw.Renamed += (_, __) => { if (TryLoadExternalConf()) InvalidateCache(); };
                _fsw.EnableRaisingEvents = true;
            }
            catch { /* необязательная оптимизация */ }
        }

        private static bool TryLoadExternalConf()
        {
            try
            {
                if (!File.Exists(ExternalConfigPath))
                    return false;

                var json = File.ReadAllText(ExternalConfigPath);
                var newer = JsonConvert.DeserializeObject<Conf>(json);
                if (newer != null)
                {
                    conf = newer;
                    File.WriteAllText(CurrentConfigPath, JsonConvert.SerializeObject(conf, Formatting.Indented));
                    return true;
                }
            }
            catch { }
            return false;
        }

        public class Conf
        {
    	    public bool enable = true;

            public int timeout = 30_000;
            public int cacheMinutes = 20;
            public int parallel = 6;
            public string[] includeProviders = Array.Empty<string>();
            public string[] excludeProviders = Array.Empty<string>();
            public int collectTop = 4;

            // Приоритеты качества (от большего к меньшему)
            public string[] qualityPriority = new[] { "2160p", "1440p", "1080p", "720p", "480p", "360p" };
            public bool enableQualityPriority = true;

            // Ограничения профиля
            public bool allow4K = false;  // отключает 1440p/2160p при сборе качеств
            public bool allowHDR = false; // отключает HDR/DV/HLG
        }
    }
}