# AGENTS.md - Руководство по разработке модулей для Lampac

## Обзор модульной системы

Lampac использует динамическую систему загрузки модулей через `module/manifest.json` [1](#3-0) . Модули могут быть скомпилированными `.dll` файлами или исходным кодом `.cs`, который компилируется во время запуска [2](#3-1) .

## Структура модуля

### 1. Файл manifest.json

Каждый модуль должен иметь `manifest.json` в корне папки модуля [3](#3-2) :

```json
{
  "enable": true,
  "version": 3,
  "dll": "MyModule",
  "namespace": "MyModule",
  "online": "MyModule.Controllers.OnlineProvider",
  "initialization": "MyModule.ModInit",
  "middleware": "MyModule.Middleware",
  "index": 100,
  "references": ["Newtonsoft.Json.dll"]
}
```

**Обязательные поля:**
- `enable` - включить/выключить модуль
- `dll` - имя папки или `.dll` файла
- `version` - версия API модуля (рекомендуется `3`)

**Опциональные поля:**
- `namespace` - корневое пространство имен (для version >= 3)
- `online` - класс для интеграции с онлайн-агрегатором
- `initialization` - класс с методом `loaded()` для инициализации
- `middleware` - класс для обработки HTTP-запросов
- `index` - порядок загрузки (меньше = раньше)
- `references` - дополнительные зависимости

### 2. Структура папок

```
module/
  MyModule/
    manifest.json
    ModInit.cs          # Инициализация
    Middleware.cs       # HTTP middleware (опционально)
    Controllers/
      OnlineProvider.cs # Интеграция с агрегатором (опционально)
      MyController.cs   # Ваши контроллеры
    Models/
      MySettings.cs     # Модели данных
```

## Правила написания кода

### 1. Инициализация модуля

Класс инициализации должен иметь статический метод `loaded()` [4](#3-3) :

```csharp
namespace MyModule
{
    public static class ModInit
    {
        public static void loaded(InitspaceModel model)
        {
            // model.app - IApplicationBuilder
            // model.services - IServiceCollection
            // model.memoryCache - IMemoryCache
            // model.path - путь к модулю
            
            // Инициализация ресурсов
            Directory.CreateDirectory("cache/mymodule");
            
            // Запуск фоновых задач
            ThreadPool.QueueUserWorkItem(async _ => await BackgroundTask());
        }
    }
}
```

**Важно:** Не блокируйте метод `loaded()` долгими операциями - используйте `ThreadPool.QueueUserWorkItem` для фоновых задач [5](#3-4) .

### 2. Интеграция с онлайн-агрегатором

Для добавления источников контента в `/lite/events` создайте класс с методами `Invoke` и/или `InvokeAsync` [6](#3-5) :

```csharp
namespace MyModule.Controllers
{
    public static class OnlineProvider
    {
        // Синхронный метод
        public static List<(string name, string url, string plugin, int index)> Invoke(
            HttpContext context, 
            IMemoryCache cache, 
            RequestModel requestInfo, 
            string host, 
            OnlineEventsModel args)
        {
            var results = new List<(string, string, string, int)>();
            
            // args.id - TMDB ID
            // args.imdb_id - IMDB ID
            // args.kinopoisk_id - Kinopoisk ID
            // args.title - название
            // args.year - год
            // args.serial - тип контента (0=фильм, 1=сериал, -1=авто)
            
            if (args.serial == 0) // только фильмы
            {
                results.Add((
                    name: "MyProvider",
                    url: $"{host}/lite/myprovider?id={args.id}",
                    plugin: "myprovider",
                    index: 100
                ));
            }
            
            return results;
        }
        
        // Асинхронный метод (опционально)
        public static async Task<List<(string name, string url, string plugin, int index)>> InvokeAsync(
            HttpContext context, 
            IMemoryCache cache, 
            RequestModel requestInfo, 
            string host, 
            OnlineEventsModel args)
        {
            // Асинхронная логика
            await Task.Delay(100);
            return new List<(string, string, string, int)>();
        }
        
        // Для поиска (/lite/spider)
        public static List<(string name, string url, int index)> Spider(
            HttpContext context, 
            IMemoryCache cache, 
            RequestModel requestInfo, 
            string host, 
            OnlineSpiderModel args)
        {
            // args.title - поисковый запрос
            
            return new List<(string, string, int)>
            {
                ("MyProvider", $"{host}/lite/myprovider/search?q={args.title}", 100)
            };
        }
    }
}
```

### 3. Middleware для обработки запросов

Middleware позволяет перехватывать HTTP-запросы до контроллеров [7](#3-6) :

```csharp
namespace MyModule
{
    public static class Middleware
    {
        public static bool Invoke(bool first, HttpContext context, IMemoryCache cache)
        {
            // first = true - вызов до аутентификации
            // first = false - вызов после аутентификации
            
            if (context.Request.Path.StartsWithSegments("/mymodule"))
            {
                // Обработка запроса
                context.Response.WriteAsync("Hello from MyModule");
                return false; // прервать pipeline
            }
            
            return true; // продолжить обработку
        }
        
        public static async Task<bool> InvokeAsync(bool first, HttpContext context, IMemoryCache cache)
        {
            // Асинхронная версия
            return true;
        }
    }
}
```

### 4. Контроллеры

Наследуйтесь от `BaseController` для доступа к базовым сервисам:

```csharp
using Microsoft.AspNetCore.Mvc;
using Shared;

namespace MyModule.Controllers
{
    public class MyController : BaseController
    {
        [HttpGet]
        [Route("lite/myprovider")]
        public async Task<ActionResult> Index(long id, int serial)
        {
            // Доступные свойства:
            // - hybridCache - кэширование
            // - memoryCache - память
            // - requestInfo - информация о запросе (IP, пользователь)
            // - host - текущий хост
            
            // Использование кэша
            var result = await InvokeCache($"myprovider:{id}", 
                TimeSpan.FromMinutes(30), 
                async () => await FetchData(id));
            
            if (!result.IsSuccess)
                return OnError("Ошибка получения данных");
            
            return Content(result.Value, "application/json");
        }
        
        private async Task<string> FetchData(long id)
        {
            // Ваша логика
            return "{}";
        }
    }
}
```

## Конфигурация модуля

### 1. Создание файла конфигурации

Храните настройки в `module/MyModule.conf`:

```csharp
public class MyModuleSettings
{
    public bool enable { get; set; } = true;
    public string api_key { get; set; }
    public int timeout { get; set; } = 30;
}

public static class ModInit
{
    private static MyModuleSettings _conf;
    
    public static MyModuleSettings conf
    {
        get
        {
            if (_conf == null)
            {
                string path = "module/MyModule.conf";
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    _conf = JsonConvert.DeserializeObject<MyModuleSettings>(json);
                }
                else
                {
                    _conf = new MyModuleSettings();
                }
            }
            return _conf;
        }
    }
}
```

### 2. Интеграция с init.conf

Добавьте секцию в `init.conf` для глобальных настроек:

```json
{
  "MyModule": {
    "enable": true,
    "api_key": "your-key-here"
  }
}
```

Доступ через `AppInit.conf`:

```csharp
var settings = AppInit.conf.GetSection("MyModule");
```

## Работа с кэшем

### 1. HybridCache (рекомендуется)

Используйте `hybridCache` для трехуровневого кэширования [8](#3-7) :

```csharp
// Чтение
if (hybridCache.TryGetValue("mykey", out string value))
{
    return value;
}

// Запись
hybridCache.Set("mykey", "myvalue", TimeSpan.FromMinutes(30));
```

### 2. InvokeCache паттерн

Используйте `InvokeCache` для предотвращения cache stampede:

```csharp
var result = await InvokeCache<MyData>(
    key: $"provider:{id}",
    expiration: TimeSpan.FromMinutes(30),
    onget: async () => await FetchFromAPI(id)
);

if (result.IsSuccess)
{
    return result.Value;
}
```

## Работа с HTTP-запросами

### 1. Использование Http.Get/Post

```csharp
using Shared.Engine;

// GET запрос
string html = await Http.Get("https://api.example.com/data", 
    timeoutSeconds: 10,
    headers: HeadersModel.Init(
        ("Authorization", "Bearer token"),
        ("User-Agent", "MyModule/1.0")
    ));

// POST запрос
string response = await Http.Post("https://api.example.com/submit",
    "{\"key\":\"value\"}",
    timeoutSeconds: 10);
```

### 2. Использование прокси

```csharp
var proxyManager = new ProxyManager("myprovider", settings);
string html = await Http.Get(url, proxy: proxyManager.Get());

if (html != null)
    proxyManager.Success();
```

## Шаблоны ответов

### 1. Для фильмов (MovieTpl)

```csharp
var tpl = new MovieTpl();

tpl.Append("Озвучка 1", 
    method: "call",
    url: $"{host}/lite/myprovider/video?id=123",
    quality: "1080p");

return rjson ? tpl.ToJson() : tpl.ToHtml();
```

### 2. Для сериалов (SeasonTpl + EpisodeTpl)

```csharp
// Список сезонов (s == -1)
if (s == -1)
{
    var tpl = new SeasonTpl();
    tpl.Append("1 сезон", $"{host}/lite/myprovider?id={id}&s=1");
    tpl.Append("2 сезон", $"{host}/lite/myprovider?id={id}&s=2");
    return rjson ? tpl.ToJson() : tpl.ToHtml();
}

// Список эпизодов (s >= 0)
var etpl = new EpisodeTpl();
etpl.Append("1 серия", 
    method: "call",
    url: $"{host}/lite/myprovider/video?id=123&s={s}&e=1",
    s: s, 
    e: 1);

return rjson ? etpl.ToJson() : etpl.ToHtml();
```

## Обработка ошибок

```csharp
// В контроллере
if (string.IsNullOrEmpty(data))
    return OnError("Не удалось получить данные");

// С ротацией прокси
if (result == null)
{
    proxyManager.Refresh();
    return OnError("Ошибка подключения", proxyManager);
}
```

## Логирование

```csharp
// Консольный вывод
Console.WriteLine($"MyModule: processing request for id={id}");

// Для отладки
if (AppInit.conf.weblog)
{
    Console.WriteLine($"MyModule DEBUG: {JsonConvert.SerializeObject(data)}");
}
```

## Установка модуля через repository.yaml

Создайте `module/repository.yaml` для автоматической установки [9](#3-8) :

```yaml
- url: https://github.com/username/MyModule
  branch: main
  folders:
    - source: src
      module_name: MyModule
```

## Тестирование

### 1. Локальная разработка

```bash
# Компиляция происходит автоматически при запуске
dotnet run --project Lampac/Lampac.csproj

# Проверка загрузки модуля
curl http://localhost:9118/admin
```

### 2. Проверка интеграции

```bash
# Проверка в агрегаторе
curl "http://localhost:9118/lite/events?id=123&title=Test&

Wiki pages you might want to explore:
- [Lampac Overview (immisterio/Lampac)](/wiki/immisterio/Lampac#1)
- [System Architecture and Components (immisterio/Lampac)](/wiki/immisterio/Lampac#1.1)
- [Frontend Integration and API (immisterio/Lampac)](/wiki/immisterio/Lampac#7)

### Citations

**File:** Shared/AppInit.cs (L288-329)
```csharp
        #region modules
        public static List<RootModule> modules;

        public static void LoadModules()
        {
            if (modules != null)
                return;

            modules = null;

            if (File.Exists("module/manifest.json"))
            {
                var jss = new JsonSerializerSettings { Error = (se, ev) => 
                { 
                    ev.ErrorContext.Handled = true; 
                    Console.WriteLine("module/manifest.json - " + ev.ErrorContext.Error + "\n\n"); 
                }};

                var mods = JsonConvert.DeserializeObject<List<RootModule>>(File.ReadAllText("module/manifest.json"), jss);
                if (mods == null)
                    return;

                modules = new List<RootModule>();
                foreach (var mod in mods)
                {
                    if (!mod.enable || mod.dll == "Jackett.dll")
                        continue;

                    string path = File.Exists(mod.dll) ? mod.dll : $"{Environment.CurrentDirectory}/module/{mod.dll}";
                    if (File.Exists(path))
                    {
                        try
                        {
                            mod.assembly = Assembly.LoadFile(path);
                            mod.index = mod.index != 0 ? mod.index : (100 + modules.Count);
                            modules.Add(mod);
                        }
                        catch { }
                    }
                }
            }
        }
```

**File:** Lampac/Startup.cs (L174-332)
```csharp
            #region compilation modules
            if (AppInit.modules != null)
            {
                // mod.dll
                foreach (var mod in AppInit.modules)
                {
                    try
                    {
                        Console.WriteLine("load module: " + mod.dll);
                        mvcBuilder.AddApplicationPart(mod.assembly);
                    }
                    catch (Exception ex) { Console.WriteLine(ex.Message + "\n"); }
                }
            }

            //  dll  source
            if (File.Exists("module/manifest.json"))
            {
                var jss = new JsonSerializerSettings
                {
                    Error = (se, ev) =>
                    {
                        ev.ErrorContext.Handled = true;
                        Console.WriteLine("module/manifest.json - " + ev.ErrorContext.Error + "\n\n");
                    }
                };

                var mods = JsonConvert.DeserializeObject<List<RootModule>>(File.ReadAllText("module/manifest.json"), jss);
                if (mods == null)
                    return;

                #region CompilationMod
                List<PortableExecutableReference> references = null;

                void CompilationMod(RootModule mod)
                {
                    if (!mod.enable || AppInit.modules.FirstOrDefault(i => i.dll == mod.dll) != null)
                        return;

                    if (mod.dll.EndsWith(".dll"))
                    {
                        try
                        {
                            mod.assembly = Assembly.LoadFrom(mod.dll);

                            AppInit.modules.Add(mod);
                            mvcBuilder.AddApplicationPart(mod.assembly);
                            Console.WriteLine($"load module: {Path.GetFileName(mod.dll)}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to load reference {mod.dll}: {ex.Message}");
                        }

                        return;
                    }

                    string path = Directory.Exists(mod.dll) ? mod.dll : $"{Environment.CurrentDirectory}/module/{mod.dll}";
                    if (Directory.Exists(path))
                    {
                        var syntaxTree = new List<SyntaxTree>();

                        foreach (string file in Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories))
                        {
                            string _file = file.Replace("\\", "/").Replace(path.Replace("\\", "/"), "").Replace(Environment.CurrentDirectory.Replace("\\", "/"), "");
                            if (Regex.IsMatch(_file, "(\\.vs|bin|obj|Properties)/", RegexOptions.IgnoreCase))
                                continue;

                            syntaxTree.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file)));
                        }

                        if (references == null)
                        {
                            var dependencyContext = DependencyContext.Default;
                            var assemblies = dependencyContext.RuntimeLibraries
                                .SelectMany(library => library.GetDefaultAssemblyNames(dependencyContext))
                                .Select(Assembly.Load)
                                .ToList();

                            references = assemblies.Select(assembly => MetadataReference.CreateFromFile(assembly.Location)).ToList();
                        }

                        if (mod.references != null)
                        {
                            foreach (string refns in mod.references)
                            {
                                string dlrns = Path.Combine(Environment.CurrentDirectory, "module", "references", refns);
                                if (!File.Exists(dlrns))
                                    dlrns = Path.Combine(Environment.CurrentDirectory, "module", mod.dll, refns);

                                if (File.Exists(dlrns) && references.FirstOrDefault(a => Path.GetFileName(a.FilePath) == refns) == null)
                                {
                                    var assembly = Assembly.LoadFrom(dlrns);
                                    references.Add(MetadataReference.CreateFromFile(assembly.Location));
                                }
                            }
                        }

                        CSharpCompilation compilation = CSharpCompilation.Create(Path.GetFileName(mod.dll), syntaxTree, references: references, options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                        using (var ms = new MemoryStream())
                        {
                            var result = compilation.Emit(ms);

                            if (!result.Success)
                            {
                                Console.WriteLine($"\ncompilation error: {mod.dll}");
                                foreach (var diagnostic in result.Diagnostics)
                                {
                                    if (diagnostic.Severity == DiagnosticSeverity.Error)
                                        Console.WriteLine(diagnostic);
                                }
                                Console.WriteLine();
                            }
                            else
                            {
                                ms.Seek(0, SeekOrigin.Begin);
                                mod.assembly = Assembly.Load(ms.ToArray());

                                Console.WriteLine("compilation module: " + mod.dll);
                                mod.index = mod.index != 0 ? mod.index : (100 + AppInit.modules.Count);
                                AppInit.modules.Add(mod);
                                mvcBuilder.AddApplicationPart(mod.assembly);
                            }
                        }
                    }
                }
                #endregion

                foreach (var mod in mods)
                    CompilationMod(mod);

                foreach (string folderMod in Directory.GetDirectories("module/"))
                {
                    string manifest = $"{Environment.CurrentDirectory}/{folderMod}/manifest.json";
                    if (!File.Exists(manifest))
                        continue;

                    var mod = JsonConvert.DeserializeObject<RootModule>(File.ReadAllText(manifest), jss);
                    if (mod != null)
                    {
                        if (mod.dll == null)
                            mod.dll = folderMod.Split("/")[1];
                        else if (mod.dll.EndsWith(".dll"))
                            mod.dll = Path.Combine(folderMod, mod.dll);

                        CompilationMod(mod);
                    }
                }

                if (references != null)
                    CSharpEval.appReferences = references;
            }

            if (AppInit.modules != null)
                AppInit.modules = AppInit.modules.OrderBy(i => i.index).ToList();

            Console.WriteLine();
            #endregion
```

**File:** Lampac/Startup.cs (L346-379)
```csharp
            if (AppInit.modules != null)
            {
                foreach (var mod in AppInit.modules)
                {
                    try
                    {
                        if (mod.dll == "DLNA.dll")
                            mod.initspace = "DLNA.ModInit";

                        if (mod.dll == "SISI.dll")
                            mod.initspace = "SISI.ModInit";

                        if (mod.initspace != null && mod.assembly.GetType(mod.NamespacePath(mod.initspace)) is Type t && t.GetMethod("loaded") is MethodInfo m)
                        {
                            if (mod.version >= 2)
                            {
                                m.Invoke(null, [ new InitspaceModel()
                                {
                                    path = $"module/{mod.dll}",
                                    soks = new soks(),
                                    nws = new nws(),
                                    memoryCache = memoryCache,
                                    configuration = Configuration,
                                    services = serviceCollection,
                                    app = app
                                }]);
                            }
                            else
                                m.Invoke(null, []);
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"Module {mod.NamespacePath(mod.initspace)}: {ex.Message}\n\n"); }
                }
            }
```

**File:** Shared/Models/Module/RootModule.cs (L1-43)
```csharp
﻿using System.Reflection;

namespace Shared.Models.Module
{
    public class RootModule
    {
        public bool enable { get; set; }

        public int index { get; set; }

        public int version { get; set; }

        public string dll { get; set; }

        public string[] references { get; set; }

        public Assembly assembly { get; set; }


        public string @namespace { get; set; }

        public string initspace { get; set; }

        public string middlewares { get; set; }

        public string online { get; set; }

        public string sisi { get; set; }

        public string initialization { get; set; }

        public List<JacMod> jac { get; set; } = new List<JacMod>();


        public string NamespacePath(string val)
        {
            if (version >= 3 && !string.IsNullOrEmpty(@namespace))
                return $"{@namespace}.{val}";

            return val;
        }
    }
```

**File:** JacRed/ModInit.cs (L45-90)
```csharp
        public static void loaded()
        {
            Directory.CreateDirectory("cache/jacred");
            File.WriteAllText("module/JacRed.current.conf", JsonConvert.SerializeObject(conf, Formatting.Indented));

            ThreadPool.QueueUserWorkItem(async _ => await SyncCron.Run());
            ThreadPool.QueueUserWorkItem(async _ => await FileDB.Cron());
            ThreadPool.QueueUserWorkItem(async _ => await FileDB.CronFast());


            ThreadPool.QueueUserWorkItem(async _ =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromMinutes(5));

                    try
                    {
                        if (conf.typesearch == "jackett" || conf.merge == "jackett")
                        {
                            async ValueTask<bool> showdown(string name, TrackerSettings settings)
                            {
                                if (!settings.monitor_showdown)
                                    return false;

                                var proxyManager = new ProxyManager(name, settings);
                                string html = await Http.Get($"{settings.host}", timeoutSeconds: conf.Jackett.timeoutSeconds, proxy: proxyManager.Get(), weblog: false);
                                return html == null;
                            }

                            conf.Jackett.Rutor.showdown = await showdown("rutor", conf.Jackett.Rutor);
                            conf.Jackett.Megapeer.showdown = await showdown("megapeer", conf.Jackett.Megapeer);
                            conf.Jackett.TorrentBy.showdown = await showdown("torrentby", conf.Jackett.TorrentBy);
                            conf.Jackett.Kinozal.showdown = await showdown("kinozal", conf.Jackett.Kinozal);
                            conf.Jackett.NNMClub.showdown = await showdown("nnmclub", conf.Jackett.NNMClub);
                            conf.Jackett.Bitru.showdown = await showdown("bitru", conf.Jackett.Bitru);
                            conf.Jackett.Toloka.showdown = await showdown("toloka", conf.Jackett.Toloka);
                            conf.Jackett.Rutracker.showdown = await showdown("rutracker", conf.Jackett.Rutracker);
                            conf.Jackett.BigFanGroup.showdown = await showdown("bigfangroup", conf.Jackett.BigFanGroup);
                            conf.Jackett.Selezen.showdown = await showdown("selezen", conf.Jackett.Selezen);
                            conf.Jackett.Lostfilm.showdown = await showdown("lostfilm", conf.Jackett.Lostfilm);
                            conf.Jackett.Anilibria.showdown = await showdown("anilibria", conf.Jackett.Anilibria);
                            conf.Jackett.Animelayer.showdown = await showdown("animelayer", conf.Jackett.Animelayer);
                            conf.Jackett.Anifilm.showdown = await showdown("anifilm", conf.Jackett.Anifilm);
                        }
                    }
```

**File:** Online/OnlineModuleEntry.cs (L1-90)
```csharp
﻿using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Shared.Models.Module;

namespace Online
{
    public class OnlineModuleEntry
    {
        public RootModule mod;

        // version >= 3
        public Func<HttpContext, IMemoryCache, RequestModel, string, OnlineEventsModel, List<(string name, string url, string plugin, int index)>> Invoke = null;
        public Func<HttpContext, IMemoryCache, RequestModel, string, OnlineEventsModel, Task<List<(string name, string url, string plugin, int index)>>> InvokeAsync = null;
        public Func<HttpContext, IMemoryCache, RequestModel, string, OnlineSpiderModel, List<(string name, string url, int index)>> Spider = null;
        public Func<HttpContext, IMemoryCache, RequestModel, string, OnlineSpiderModel, Task<List<(string name, string url, int index)>>> SpiderAsync = null;

        // version < 3
        public Func<string, long, string, long, string, string, string, int, string, int, string, List<(string name, string url, string plugin, int index)>> Events = null;
        public Func<HttpContext, IMemoryCache, string, long, string, long, string, string, string, int, string, int, string, Task<List<(string name, string url, string plugin, int index)>>> EventsAsync = null;
        public static List<OnlineModuleEntry> onlineModulesCache = null;
        static readonly object _onlineModulesCacheLock = new object();

        public static void EnsureCache()
        {
            if (onlineModulesCache != null || AppInit.modules == null)
                return;

            lock (_onlineModulesCacheLock)
            {
                if (onlineModulesCache != null)
                    return;

                onlineModulesCache = new List<OnlineModuleEntry>();

                try
                {
                    foreach (var mod in AppInit.modules.Where(i => i.online != null))
                    {
                        try
                        {
                            var entry = new OnlineModuleEntry() { mod = mod };

                            var assembly = mod.assembly;
                            if (assembly == null)
                                continue;

                            var type = assembly.GetType(mod.NamespacePath(mod.online));
                            if (type == null)
                                continue;

                            if (mod.version >= 3)
                            {
                                try
                                {
                                    var m = type.GetMethod("Invoke");
                                    if (m != null)
                                    {
                                        entry.Invoke = (Func<HttpContext, IMemoryCache, RequestModel, string, OnlineEventsModel, List<(string name, string url, string plugin, int index)>>)Delegate.CreateDelegate(
                                            typeof(Func<HttpContext, IMemoryCache, RequestModel, string, OnlineEventsModel, List<(string name, string url, string plugin, int index)>>), m);
                                    }
                                }
                                catch { }

                                try
                                {
                                    var m2 = type.GetMethod("InvokeAsync");
                                    if (m2 != null)
                                    {
                                        entry.InvokeAsync = (Func<HttpContext, IMemoryCache, RequestModel, string, OnlineEventsModel, Task<List<(string name, string url, string plugin, int index)>>>)Delegate.CreateDelegate(
                                            typeof(Func<HttpContext, IMemoryCache, RequestModel, string, OnlineEventsModel, Task<List<(string name, string url, string plugin, int index)>>>), m2);
                                    }
                                }
                                catch { }

                                try
                                {
                                    var m3 = type.GetMethod("Spider");
                                    if (m3 != null)
                                    {
                                        entry.Spider = (Func<HttpContext, IMemoryCache, RequestModel, string, OnlineSpiderModel, List<(string name, string url, int index)>>)Delegate.CreateDelegate(
                                            typeof(Func<HttpContext, IMemoryCache, RequestModel, string, OnlineSpiderModel, List<(string name, string url, int index)>>), m3);
                                    }
                                }
                                catch { }

                                try
                                {
                                    var m4 = type.GetMethod("SpiderAsync");
                                    if (m4 != null)
                                    {
```

**File:** Lampac/Engine/Middlewares/Module.cs (L24-60)
```csharp
        async public Task InvokeAsync(HttpContext httpContext)
        {
            #region modules
            MiddlewareModuleEntry.EnsureCache();

            if (MiddlewareModuleEntry.middlewareModulesCache != null && MiddlewareModuleEntry.middlewareModulesCache.Count > 0)
            {
                foreach (var entry in MiddlewareModuleEntry.middlewareModulesCache)
                {
                    var mod = entry.mod;

                    try
                    {
                        if (first && (mod.version == 0 || mod.version == 1))
                            continue;

                        if (mod.version >= 2)
                        {
                            if (entry.Invoke != null)
                            {
                                bool next = entry.Invoke(first, httpContext, memoryCache);
                                if (!next)
                                    return;
                            }

                            if (entry.InvokeAsync != null)
                            {
                                bool next = await entry.InvokeAsync(first, httpContext, memoryCache);
                                if (!next)
                                    return;
                            }
                        }
                        else
                        {
                            if (entry.InvokeV1 != null)
                            {
                                bool next = entry.InvokeV1(httpContext, memoryCache);
```

**File:** Shared/Engine/ModuleRepository.cs (L1-150)
```csharp
﻿using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Shared.Engine
{
    /// <summary>
    /// Codex AI - Module Repository
    /// </summary>
    public static class ModuleRepository
    {
        private const string RepositoryFile = "module/repository.yaml";
        private const string StateFile = "module/.repository_state.json";

        private static readonly object SyncRoot = new object();
        private static readonly HttpClient HttpClient;

        private static ApplicationPartManager partManager;
        private static Dictionary<string, string> repositoryState;

        static ModuleRepository()
        {
            HttpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(60)
            };

            if (!HttpClient.DefaultRequestHeaders.UserAgent.Any())
                HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LampacModuleRepository/1.0");

            if (!HttpClient.DefaultRequestHeaders.Accept.Any())
                HttpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        public static void Configuration(IMvcBuilder mvcBuilder)
        {
            partManager = mvcBuilder?.PartManager;

            UpdateModules();
        }

        private static void UpdateModules()
        {
            if (!Monitor.TryEnter(SyncRoot))
            {
                Console.WriteLine("ModuleRepository: UpdateModules skipped because another update is running");
                return;
            }

            Console.WriteLine("ModuleRepository: UpdateModules start");

            try
            {
                var repositories = LoadConfiguration();
                if (repositories.Count == 0)
                {
                    Console.WriteLine("ModuleRepository: no repositories configured");
                    return;
                }

                Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, "module"));
                Console.WriteLine("ModuleRepository: ensured module directory exists");

                var state = LoadState();
                bool stateChanged = false;
                var modulesToCompile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var repository in repositories)
                {
                    try
                    {
                        if (!repository.IsValid)
                        {
                            Console.WriteLine($"ModuleRepository: skipping invalid repository '{repository?.Url}'");
                            continue;
                        }

                        bool missingModule = repository.Folders.Any(folder => !Directory.Exists(Path.Combine(Environment.CurrentDirectory, "module", folder.ModuleName)));
                        string commitSha = GetLatestCommitSha(repository);
                        if (string.IsNullOrEmpty(commitSha))
                        {
                            Console.WriteLine($"ModuleRepository: could not determine latest commit for {repository.Url}");
                            continue;
                        }

                        string stateKey = repository.StateKey;
                        if (!missingModule && state.TryGetValue(stateKey, out string storedSha) && string.Equals(storedSha, commitSha, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"ModuleRepository: repository '{repository.Url}' is up-to-date (sha={commitSha})");
                            continue;
                        }

                        if (DownloadAndExtract(repository, modulesToCompile))
                        {
                            state[stateKey] = commitSha;
                            stateChanged = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"ModuleRepository: error processing repository {repository?.Url} - {ex.Message}");
                    }
                }

                if (stateChanged)
                {
                    SaveState(state);
                    Console.WriteLine("ModuleRepository: state saved");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"module repository: {ex.Message}");
            }
            finally
            {
                Console.WriteLine("ModuleRepository: UpdateModules finished, releasing lock");
                Monitor.Exit(SyncRoot);
            }
        }

        private static List<RepositoryEntry> LoadConfiguration()
        {
            string path = Path.Combine(Environment.CurrentDirectory, RepositoryFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                Console.WriteLine($"ModuleRepository: repository config file not found at {path}");
                return new List<RepositoryEntry>();
            }

            try
            {
                string yaml = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(yaml))
                {
                    Console.WriteLine("ModuleRepository: repository config file is empty");
                    return new List<RepositoryEntry>();
                }

                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
```
