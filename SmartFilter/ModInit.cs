using Newtonsoft.Json;
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

        private static FileSystemWatcher _fsw;

        public static void InvalidateCache() => Interlocked.Increment(ref _configVersion);

        public static void loaded()
        {
            Directory.CreateDirectory("module/SmartFilter");

            // начальная запись текущей конфигурации
            File.WriteAllText("module/SmartFilter.current.conf", JsonConvert.SerializeObject(conf, Formatting.Indented));

            // попытка загрузить пользовательскую конфигурацию, если есть
            TryLoadExternalConf();
            InvalidateCache();

            // слежение за изменениями module/SmartFilter.conf
            try
            {
                _fsw = new FileSystemWatcher("module")
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
                string path = "module/SmartFilter.conf";
                if (!File.Exists(path))
                    return false;

                var json = File.ReadAllText(path);
                var newer = JsonConvert.DeserializeObject<Conf>(json);
                if (newer != null)
                {
                    conf = newer;
                    File.WriteAllText("module/SmartFilter.current.conf", JsonConvert.SerializeObject(conf, Formatting.Indented));
                    return true;
                }
            }
            catch { }
            return false;
        }

        public class Conf
        {
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
