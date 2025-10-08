Вы просите помочь создать документ `AGENTS.md` с правилами для написания кода модулей в Lampac<cite />. Изучу архитектуру модульной системы, чтобы составить руководство<cite />.

# AGENTS.md - Руководство по разработке модулей для Lampac

Документация - https://github.com/immisterio/Lampac


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
------------

Работа с истониками типо VoKino

Судя по вашему JSON ответу, проблема в следующем:

## Почему не работает VoKino

VoKino отсутствует в списке провайдеров вашего модуля агрегатора smartfilter. VoKino работает через специальную логику проверки доступности контента: [1](#0-0) 

VoKino добавляется в список онлайн-провайдеров через метод `SendOnline`, который проверяет наличие доступных балансеров (filmix, alloha, vibix, monframe, remux, ashdi, hdvb) через API vokino.org: [2](#0-1) 

Для работы VoKino требуется:
1. Токен авторизации (получается через `/lite/vokinotk`)
2. Настройка в `init.conf` с параметром `token`
3. Включение в списке провайдеров модуля smartfilter [3](#0-2) 

## Почему возникают ошибки 500

Многие провайдеры в вашем списке возвращают ошибку "Response status code does not indicate success: 500 (Internal Server Error)":
- FilmixTV
- GetsTV  
- IframeVideo
- iptv.online
- Kinotochka
- Mirage
- Redheadsound
- Vibix
- VideoCDN

Это происходит потому что:

1. **Отсутствует конфигурация** - многие провайдеры требуют дополнительные параметры. Например, FilmixTV требует `user_apitv` и `passwd_apitv`: [4](#0-3) 

2. **Не настроен доступ** - провайдеры могут требовать RCH (Remote Client Hub) доступ или особые настройки прокси: [5](#0-4) 

3. **Провайдер отключен** - параметр `enable: false` в конфигурации или провайдер находится в режиме `rip`

## Решение

1. **Для VoKino**: добавьте его в конфигурацию вашего модуля smartfilter с правильными параметрами. Сначала получите токен через `/lite/vokinotk`, затем добавьте в `init.conf`:
```json
"VoKino": {
  "enable": true,
  "token": "ваш_токен"
}
```

2. **Для провайдеров с ошибкой 500**: проверьте конфигурацию каждого провайдера в `init.conf`. Многие требуют:
   - Токены авторизации
   - Учетные данные (user/password)
   - Настройки прокси
   - Включение через параметр `enable: true`

3. **Проверьте checksearch**: система использует предварительную проверку доступности провайдеров: [6](#0-5) 

## Примечания

- **smartfilter** является внешним модулем и не входит в базовый код Lampac
- Работающие провайдеры (Aladdin, Collaps, HDVB, iRemux, Lumex, PidŦor, Rutube, VeoVeo) настроены правильно
- Параметр `checkOnlineSearch` может скрывать неработающие провайдеры, что объясняет сообщение "поиск не дал результатов"
- Проверьте настройки онлайн-балансеров VoKino в конфигурации: [7](#0-6)

### Citations

**File:** Online/OnlineApi.cs (L802-856)
```csharp
            #region VoKino
            if (kinopoisk_id > 0)
            {
                var myinit = loadKit(conf.VoKino, kitconf , (j, i, c) => 
                {
                    if (j.ContainsKey("online"))
                        i.online = c.online;

                    return i;
                });

                if (myinit.enable && !string.IsNullOrEmpty(myinit.token))
                {
                    async ValueTask vkino()
                    {
                        if (myinit.rhub || !conf.online.checkOnlineSearch)
                        {
                            VoKinoInvoke.SendOnline(myinit, online, null);
                        }
                        else
                        {
                            if (!hybridCache.TryGetValue($"vokino:view:{kinopoisk_id}", out JObject view))
                            {
                                view = await Http.Get<JObject>($"{myinit.corsHost()}/v2/view/{kinopoisk_id}?token={myinit.token}", timeoutSeconds: 4);
                                if (view != null)
                                    hybridCache.Set($"vokino:view:{kinopoisk_id}", view, cacheTime(20));
                            }

                            if (view != null && view.ContainsKey("online") && view["online"] is JObject onlineObj)
                                VoKinoInvoke.SendOnline(myinit, online, onlineObj);
                        }
                    };

                    if (AppInit.conf.accsdb.enable)
                    {
                        if (user != null)
                        {
                            if (myinit.group > user.group && myinit.group_hide) { }
                            else
                                await vkino();
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(AppInit.conf.accsdb.premium_pattern))
                                await vkino();
                        }
                    }
                    else
                    {
                        if (myinit.group > 0 && myinit.group_hide && (user == null || myinit.group > user.group)) { }
                        else
                            await vkino();
                    }
                }
            }
```

**File:** Online/OnlineApi.cs (L1066-1106)
```csharp
            #region checkOnlineSearch
            bool chos = conf.online.checkOnlineSearch && id > 0;

            if (chos && IO.File.Exists("isdocker"))
            {
                string version = await Http.Get($"http://{AppInit.conf.listen.localhost}:{AppInit.conf.listen.port}/version", timeoutSeconds: 4, headers: HeadersModel.Init("localrequest", AppInit.rootPasswd));
                if (version == null || !version.StartsWith(appversion))
                    chos = false;
            }

            if (chos)
            {
                string memkey = CrypTo.md5($"checkOnlineSearch:{id}:{serial}:{source?.Replace("tmdb", "")?.Replace("cub", "")}:{online.Count}:{(IsKitConf ? requestInfo.user_uid : null)}");

                if (!memoryCache.TryGetValue(memkey, out List<(string code, int index, bool work)> links) || !conf.multiaccess)
                {
                    var tasks = new List<Task>();
                    links = new List<(string code, int index, bool work)>(online.Count);
                    for (int i = 0; i < online.Count; i++)
                        links.Add(default);

                    memoryCache.Set(memkey, links, DateTime.Now.AddMinutes(5));

                    foreach (var o in online)
                    {
                        var tk = checkSearch(memkey, links, tasks.Count, o.init, o.index, o.name, o.url, o.plugin, id, imdb_id, kinopoisk_id, title, original_title, original_language, source, year, serial, life, rchtype);
                        tasks.Add(tk);
                    }

                    if (life)
                        return Json(new { life = true, memkey, title = (fix_title ? title : null) });

                    await Task.WhenAll(tasks);
                }

                if (life)
                    return Json(new { life = true, memkey });

                return ContentTo($"[{string.Join(",", links.Where(i => i.code != null).OrderByDescending(i => i.work).ThenBy(i => i.index).Select(i => i.code)).Replace("{localhost}", host)}]");
            }
            #endregion
```

**File:** Shared/Engine/Online/VoKino.cs (L35-76)
```csharp
        public static void SendOnline(VokinoSettings init, List<(dynamic init, string name, string url, string plugin, int index)> online, JObject view)
        {
            var on = init.online;

            void send(string name, int x)
            {
                string url = "{localhost}/lite/vokino?balancer=" + name.ToLower();

                string displayname = $"{init.displayname ?? "VoKino"}";
                if (name != "VoKino")
                    displayname = $"{name} ({init.displayname ?? "VoKino"})";

                if (init.onlyBalancerName)
                    displayname = name;

                online.Add((init, displayname, url, (name == "VoKino" ? "vokino" : $"vokino-{name.ToLower()}"), init.displayindex > 0 ? (init.displayindex + x) : online.Count));
            }

            if (on.vokino && (view == null || view.ContainsKey("Vokino")))
                send("VoKino", 1);

            if (on.filmix && (view == null || view.ContainsKey("Filmix")))
                send("Filmix", 2);

            if (on.alloha && (view == null || view.ContainsKey("Alloha")))
                send("Alloha", 3);

            if (on.vibix && (view == null || view.ContainsKey("Vibix")))
                send("Vibix", 4);

            if (on.monframe && (view == null || view.ContainsKey("MonFrame")))
                send("MonFrame", 5);

            if (on.remux && (view == null || view.ContainsKey("Remux")))
                send("Remux", 6);

            if (on.ashdi && (view == null || view.ContainsKey("Ashdi")))
                send("Ashdi", 7);

            if (on.hdvb && (view == null || view.ContainsKey("Hdvb")))
                send("HDVB", 8);
        }
```

**File:** Online/Controllers/VoKino.cs (L11-38)
```csharp
        #region vokinotk
        [HttpGet]
        [Route("lite/vokinotk")]
        async public Task<ActionResult> Token(string login, string pass)
        {
            string html = string.Empty;

            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(pass))
            {
                html = "Введите данные аккаунта <a href='http://vokino.tv'>vokino.tv</a> <br> <br><form method=\"get\" action=\"/lite/vokinotk\"><input type=\"text\" name=\"login\" placeholder=\"email\"> &nbsp; &nbsp; <input type=\"text\" name=\"pass\" placeholder=\"пароль\"><br><br><button>Добавить устройство</button></form> ";
            }
            else
            {
                string deviceid = new string(DateTime.Now.ToBinary().ToString().Reverse().ToArray()).Substring(0, 8);
                var token_request = await Http.Get<JObject>($"{AppInit.conf.VoKino.corsHost()}/v2/auth?email={HttpUtility.UrlEncode(login)}&passwd={HttpUtility.UrlEncode(pass)}&deviceid={deviceid}", proxy: proxyManager.Get(), headers: HeadersModel.Init("user-agent", "lampac"));

                if (token_request == null)
                    return Content($"нет доступа к {AppInit.conf.VoKino.corsHost()}", "text/html; charset=utf-8");

                string authToken = token_request.Value<string>("authToken");
                if (string.IsNullOrEmpty(authToken))
                    return Content(token_request.Value<string>("error") ?? "Не удалось получить токен", "text/html; charset=utf-8");

                html = "Добавьте в init.conf<br><br>\"VoKino\": {<br>&nbsp;&nbsp;\"enable\": true,<br>&nbsp;&nbsp;\"token\": \"" + authToken + "\"<br>}";
            }

            return Content(html, "text/html; charset=utf-8");
        }
```

**File:** Online/Controllers/FilmixTV.cs (L35-36)
```csharp
            if (string.IsNullOrEmpty(init.user_apitv))
                return OnError("user_apitv", gbcache: false);
```

**File:** Online/Controllers/Redheadsound.cs (L19-21)
```csharp
            var rch = new RchClient(HttpContext, host, init, requestInfo);
            if (rch.IsNotSupport("web,cors", out string rch_error))
                return ShowError(rch_error);
```

**File:** Shared/Models/Online/VoKino/ViewOnline.cs (L1-21)
```csharp
﻿namespace Shared.Models.Online.VoKino
{
    public class ViewOnline
    {
        public bool vokino { get; set; } = true;

        public bool filmix { get; set; } = true;

        public bool alloha { get; set; } = true;

        public bool hdvb { get; set; } = true;

        public bool remux { get; set; } = true;

        public bool monframe { get; set; } = true;

        public bool ashdi { get; set; } = true;

        public bool vibix { get; set; } = true;
    }
}
```


---------

Вы спрашиваете о том, как устроены различные типы источников контента в Lampac для фильмов, сериалов, аниме и мультфильмов<cite />. Покажу примеры реализации для разных категорий контента<cite />.

## Типы контента и их обработка

В Lampac используется система типов контента, которая определяется в торрент-трекерах и онлайн-источниках [1](#2-0) :

- **movie** - фильмы
- **serial** - сериалы  
- **multfilm** - мультфильмы
- **multserial** - мультсериалы
- **anime** - аниме
- **docuserial** / **documovie** - документальные
- **tvshow** - ТВ-шоу

## Примеры для разных источников

### 1. Фильмы (movie)

**Онлайн-источник Alloha** возвращает фильмы через `MovieTpl` [2](#2-1) :

```json
{
  "type": "movie",
  "voice": {...},
  "data": [
    {
      "method": "call",
      "url": "/lite/alloha/video?t=66&token_movie=...",
      "stream": "/lite/alloha/video.m3u8?...",
      "quality": "1080p",
      "voice_name": "Дубляж"
    }
  ]
}
```

**Торрент-источники** для фильмов используют категории [3](#2-2) :
- Rutor: категории 1, 5, 7, 12, 17
- Megapeer: 79, 80, 76
- Kinozal: множество категорий для разных жанров [4](#2-3) 

### 2. Сериалы (serial)

**Онлайн-источник Lumex** для сериалов [5](#2-4) :

При `s == -1` возвращает список сезонов:
```json
{
  "type": "season",
  "data": [
    {"id": 1, "name": "1 сезон", "url": "/lite/lumex?s=1"},
    {"id": 2, "name": "2 сезон", "url": "/lite/lumex?s=2"}
  ]
}
```

При `s >= 0` возвращает эпизоды через `EpisodeTpl`:
```json
{
  "type": "episode",
  "voice": {...},
  "data": [
    {
      "s": 1,
      "e": 1,
      "name": "1 серия",
      "url": "...",
      "stream": "..."
    }
  ]
}
```

**Торрент-источники** для сериалов [6](#2-5) :
- Rutor: 4, 16, 7, 12, 6, 17
- Megapeer: 5, 6, 55, 57, 76
- Rutracker: множество категорий [7](#2-6) 

### 3. Аниме (anime)

**Онлайн-источник AnimeLib** с API-токеном [8](#2-7) :

Сначала поиск по названию, затем получение эпизодов:
```json
{
  "type": "episode",
  "voice": {
    "data": [
      {"name": "AniLibria", "active": true}
    ]
  },
  "data": [
    {
      "s": 1,
      "e": 1,
      "name": "Название / 1 серия",
      "url": "/lite/animelib/video?id=123"
    }
  ]
}
```

**Онлайн-источник AnimeGo** с переводами [9](#2-8) :

Поддерживает множественные переводы через `VoiceTpl`.

**Торрент-источники** для аниме [10](#2-9) :
- Rutor: категория 10
- Kinozal: категория 20 [11](#2-10) 
- Специализированные: Anilibria, AnimeLayer, Anifilm

### 4. Мультфильмы (multfilm)

**Торрент-источники** различают мультфильмы и мультсериалы [12](#2-11) :

- **multfilm** (мультфильмы): Toloka категории 19, 139, 84
- **multserial** (мультсериалы): Toloka категории 174, 44, 125

**Rutracker** для мультфильмов [13](#2-12) :
- multfilm: 2343, 930, 2365, 208, 539, 209
- multserial: 921, 815, 1460

### 5. VideoCDN - универсальный источник

**VideoCDN** поддерживает все типы контента [14](#2-13) :

Для фильмов (`content_type: "movie"` или `"anime"`):
```json
{
  "type": "movie",
  "data": [...]
}
```

Для сериалов - сначала сезоны, затем эпизоды с переводами [15](#2-14) .

### 6. Документальные (docuserial/documovie)

**Торрент-источники** для документальных [16](#2-15) :

Обрабатываются отдельно с параметром `is_serial == 4`:
- Rutracker: множество категорий [17](#2-16) 
- Toloka: категории 226-230, 12, 131 [18](#2-17) 

### 7. ТВ-шоу (tvshow)

**Торрент-источники** для ТВ-шоу [19](#2-18) :
- Rutor: категория 6
- Kinozal: категории 1006, 48, 49, 50, 38 [20](#2-19) 
- Rutracker: категории 24, 1959, 939 и другие [21](#2-20) 

## Логика определения типа контента

Система использует параметр `is_serial` для фильтрации [22](#2-21) :

- `is_serial == 1` → movie, multfilm, anime, documovie
- `is_serial == 2` → serial, multserial, anime, docuserial, tvshow  
- `is_serial == 3` → tvshow
- `is_serial == 4` → docuserial, documovie
- `is_serial == 5` → anime
- `is_serial == -1` → все типы

## Notes

Все онлайн-источники используют единую систему шаблонов (`MovieTpl`, `SeasonTpl`, `EpisodeTpl`), которые автоматически формируют правильный JSON/HTML в зависимости от типа контента<cite />. Торрент-источники классифицируют контент по категориям трекеров, которые затем маппятся на стандартные типы через систему фильтрации<cite />. Для аниме существуют как специализированные источники (AnimeLib, AnimeGo, Anilibria), так и общие категории на торрент-трекерах<cite />.

Wiki pages you might want to explore:
- [Lampac Overview (immisterio/Lampac)](/wiki/immisterio/Lampac#1)
- [Key Features and Content Sources (immisterio/Lampac)](/wiki/immisterio/Lampac#1.2)

### Citations

**File:** JacRed/ApiController.cs (L300-329)
```csharp
                switch (type)
                {
                    case "movie":
                        categoryDesc = "Movies";
                        categoryIds.Add(2000);
                        break;

                    case "serial":
                        categoryDesc = "TV";
                        categoryIds.Add(5000);
                        break;

                    case "documovie":
                    case "docuserial":
                        categoryDesc = "TV/Documentary";
                        categoryIds.Add(5080);
                        break;

                    case "tvshow":
                        categoryDesc = "TV/Foreign";
                        categoryIds.Add(5020);
                        categoryIds.Add(2010);
                        break;

                    case "anime":
                        categoryDesc = "TV/Anime";
                        categoryIds.Add(5070);
                        break;
                }
            }
```

**File:** Online/Controllers/Alloha.cs (L62-82)
```csharp
                #region Фильм
                var mtpl = new MovieTpl(title, original_title);
                bool directors_cut = data.Value<bool>("available_directors_cut");

                foreach (var translation in data.Value<JObject>("translation_iframe").ToObject<Dictionary<string, Dictionary<string, object>>>())
                {
                    string link = $"{host}/lite/alloha/video?t={translation.Key}&token_movie={result.data.Value<string>("token_movie")}" + defaultargs;
                    string streamlink = accsArgs($"{link.Replace("/video", "/video.m3u8")}&play=true");

                    bool uhd = false;
                    if (translation.Value.TryGetValue("uhd", out object _uhd))
                        uhd = _uhd.ToString().ToLower() == "true" && init.m4s;

                    if (directors_cut && translation.Key == "66")
                        mtpl.Append("Режиссерская версия", $"{link}&directors_cut=true", "call", $"{streamlink}&directors_cut=true", voice_name: uhd ? "2160p" : translation.Value["quality"].ToString(), quality: uhd ? "2160p" : "");

                    mtpl.Append(translation.Value["name"].ToString(), link, "call", streamlink, voice_name: uhd ? "2160p" : translation.Value["quality"].ToString(), quality: uhd ? "2160p" : "");
                }

                return ContentTo(rjson ? mtpl.ToJson() : mtpl.ToHtml());
                #endregion
```

**File:** JacRed/Engine/JackettApi.cs (L91-117)
```csharp
                    RutorController.search(host, torrents, search, "1"),  // movie
                    RutorController.search(host, torrents, search, "5"),  // movie
                    RutorController.search(host, torrents, search, "7"),  // multfilm
                    RutorController.search(host, torrents, search, "12"), // documovie
                    RutorController.search(host, torrents, search, "17", true, "1"), // UKR

                    MegapeerController.search(host, torrents, search, "79"),  // Наши фильмы
                    MegapeerController.search(host, torrents, search, "80"),  // Зарубежные фильмы
                    MegapeerController.search(host, torrents, search, "76"),  // Мультипликация

                    TorrentByController.search(host, torrents, search, "1"), // movie
                    TorrentByController.search(host, torrents, search, "2"), // movie
                    TorrentByController.search(host, torrents, search, "5"), // multfilm

                    KinozalController.search(host, torrents, search, new string[] { "movie", "multfilm", "tvshow" }),
                    NNMClubController.search(host, torrents, search, new string[] { "movie", "multfilm", "documovie" }),
                    TolokaController.search(host, torrents, search, new string[] { "movie", "multfilm", "documovie" }),
                    RutrackerController.search(host, torrents, search, new string[] { "movie", "multfilm", "documovie" }),
                    BitruController.search(host, torrents, search, new string[] { "movie" }),
                    SelezenController.search(host, torrents, search),
                    BigFanGroup.search(host, torrents, search, new string[] { "movie", "multfilm", "documovie" })
                };

                modpars(tasks, "movie");

                await Task.WhenAll(tasks);
                #endregion
```

**File:** JacRed/Engine/JackettApi.cs (L121-154)
```csharp
                #region Сериал
                var tasks = new List<Task>
                {
                    RutorController.search(host, torrents, search, "4"),  // serial
                    RutorController.search(host, torrents, search, "16"), // serial
                    RutorController.search(host, torrents, search, "7"),  // multserial
                    RutorController.search(host, torrents, search, "12"), // docuserial
                    RutorController.search(host, torrents, search, "6"),  // tvshow
                    RutorController.search(host, torrents, search, "17", true, "4"), // UKR

                    MegapeerController.search(host, torrents, search, "5"),  // serial
                    MegapeerController.search(host, torrents, search, "6"),  // serial
                    MegapeerController.search(host, torrents, search, "55"), // docuserial
                    MegapeerController.search(host, torrents, search, "57"), // tvshow
                    MegapeerController.search(host, torrents, search, "76"), // multserial

                    TorrentByController.search(host, torrents, search, "3"),  // serial
                    TorrentByController.search(host, torrents, search, "5"),  // multserial
                    TorrentByController.search(host, torrents, search, "4"),  // tvshow
                    TorrentByController.search(host, torrents, search, "12"), // tvshow

                    KinozalController.search(host, torrents, search, new string[] { "serial", "multserial", "tvshow" }),
                    NNMClubController.search(host, torrents, search, new string[] { "serial", "multserial", "docuserial" }),
                    TolokaController.search(host, torrents, search, new string[] { "serial", "multserial", "docuserial" }),
                    RutrackerController.search(host, torrents, search, new string[] { "serial", "multserial", "docuserial" }),
                    BitruController.search(host, torrents, search, new string[] { "serial" }),
                    LostfilmController.search(host, torrents, search),
                    BigFanGroup.search(host, torrents, search, new string[] { "serial", "multserial", "docuserial", "tvshow" })
                };

                modpars(tasks, "serial");

                await Task.WhenAll(tasks);
                #endregion
```

**File:** JacRed/Engine/JackettApi.cs (L156-175)
```csharp
            else if (is_serial == 3)
            {
                #region tvshow
                var tasks = new List<Task>
                {
                    RutorController.search(host, torrents, search, "6"),
                    MegapeerController.search(host, torrents, search, "57"),
                    TorrentByController.search(host, torrents, search, "4"),
                    TorrentByController.search(host, torrents, search, "12"),
                    KinozalController.search(host, torrents, search, new string[] { "tvshow" }),
                    NNMClubController.search(host, torrents, search, new string[] { "docuserial", "documovie" }),
                    TolokaController.search(host, torrents, search, new string[] { "docuserial", "documovie" }),
                    RutrackerController.search(host, torrents, search, new string[] { "tvshow" }),
                    BigFanGroup.search(host, torrents, search, new string[] { "tvshow" })
                };

                modpars(tasks, "tvshow");

                await Task.WhenAll(tasks);
                #endregion
```

**File:** JacRed/Engine/JackettApi.cs (L196-216)
```csharp
            {
                #region anime
                string animesearch = title ?? query;

                var tasks = new List<Task>
                {
                    RutorController.search(host, torrents, animesearch, "10"),
                    TorrentByController.search(host, torrents, animesearch, "6"),
                    KinozalController.search(host, torrents, animesearch, new string[] { "anime" }),
                    NNMClubController.search(host, torrents, animesearch, new string[] { "anime" }),
                    RutrackerController.search(host, torrents, animesearch, new string[] { "anime" }),
                    TolokaController.search(host, torrents, search, new string[] { "anime" }),
                    AniLibriaController.search(host, torrents, animesearch),
                    AnimeLayerController.search(host, torrents, animesearch),
                    AnifilmController.search(host, torrents, animesearch)
                };

                modpars(tasks, "anime");

                await Task.WhenAll(tasks);
                #endregion
```

**File:** JacRed/Controllers/KinozalController.cs (L122-142)
```csharp
                    case "1002":
                    case "8":
                    case "6":
                    case "15":
                    case "17":
                    case "35":
                    case "39":
                    case "13":
                    case "14":
                    case "24":
                    case "11":
                    case "10":
                    case "9":
                    case "47":
                    case "18":
                    case "37":
                    case "12":
                    case "7":
                    case "16":
                        types = new string[] { "movie" };
                        break;
```

**File:** JacRed/Controllers/KinozalController.cs (L151-153)
```csharp
                    case "20":
                        types = new string[] { "anime" };
                        break;
```

**File:** JacRed/Controllers/KinozalController.cs (L154-160)
```csharp
                    case "1006":
                    case "48":
                    case "49":
                    case "50":
                    case "38":
                        types = new string[] { "tvshow" };
                        break;
```

**File:** Shared/Engine/Online/Lumex.cs (L217-240)
```csharp
                #region Сериал
                string enc_title = HttpUtility.UrlEncode(title);
                string enc_original_title = HttpUtility.UrlEncode(original_title);

                try
                {
                    if (s == -1)
                    {
                        var tpl = new SeasonTpl(result.media.Length);

                        foreach (var media in result.media.OrderBy(s => s.season_id))
                        {
                            string link = host + $"lite/lumex?content_id={content_id}&content_type={content_type}&kinopoisk_id={kinopoisk_id}&imdb_id={imdb_id}&rjson={rjson}&title={enc_title}&original_title={enc_original_title}&clarification={clarification}&s={media.season_id}{args}";    

                            tpl.Append($"{media.season_id} сезон", link, media.season_id);
                        }

                        return rjson ? tpl.ToJson() : tpl.ToHtml();
                    }
                    else
                    {
                        #region Перевод
                        var vtpl = new VoiceTpl();
                        var tmpVoice = new HashSet<int>();
```

**File:** JacRed/Controllers/RutrackerController.cs (L142-184)
```csharp
                    case "2343":
                    case "930":
                    case "2365":
                    case "208":
                    case "539":
                    case "209":
                        types = new string[] { "multfilm" };
                        break;
                    case "921":
                    case "815":
                    case "1460":
                        types = new string[] { "multserial" };
                        break;
                    case "842":
                    case "235":
                    case "242":
                    case "819":
                    case "1531":
                    case "721":
                    case "1102":
                    case "1120":
                    case "1214":
                    case "489":
                    case "387":
                    case "9":
                    case "81":
                    case "119":
                    case "1803":
                    case "266":
                    case "193":
                    case "1690":
                    case "1459":
                    case "825":
                    case "1248":
                    case "1288":
                    case "325":
                    case "534":
                    case "694":
                    case "704":
                    case "915":
                    case "1939":
                        types = new string[] { "serial" };
                        break;
```

**File:** JacRed/Controllers/RutrackerController.cs (L190-227)
```csharp
                    case "709":
                        types = new string[] { "documovie" };
                        break;
                    case "46":
                    case "671":
                    case "2177":
                    case "2538":
                    case "251":
                    case "98":
                    case "97":
                    case "851":
                    case "2178":
                    case "821":
                    case "2076":
                    case "56":
                    case "2123":
                    case "876":
                    case "2139":
                    case "1467":
                    case "1469":
                    case "249":
                    case "552":
                    case "500":
                    case "2112":
                    case "1327":
                    case "1468":
                    case "2168":
                    case "2160":
                    case "314":
                    case "1281":
                    case "2110":
                    case "979":
                    case "2169":
                    case "2164":
                    case "2166":
                    case "2163":
                        types = new string[] { "docuserial", "documovie" };
                        break;
```

**File:** JacRed/Controllers/RutrackerController.cs (L228-240)
```csharp
                    case "24":
                    case "1959":
                    case "939":
                    case "1481":
                    case "113":
                    case "115":
                    case "882":
                    case "1482":
                    case "393":
                    case "2537":
                    case "532":
                    case "827":
                        types = new string[] { "tvshow" };
```

**File:** Online/Controllers/Anime/AnimeLib.cs (L114-192)
```csharp
                #region Серии
                string memKey = $"animelib:playlist:{uri}";

                return await InvkSemaphore(init, memKey, async () =>
                {
                    if (!hybridCache.TryGetValue(memKey, out Episode[] episodes))
                    {
                        string req_uri = $"{init.corsHost()}/api/episodes?anime_id={uri}";

                        var root = rch.enable ? await rch.Get<JObject>(req_uri, headers) :
                                                await Http.Get<JObject>(req_uri, timeoutSeconds: 8, httpversion: 2, proxy: proxyManager.Get(), headers: headers);

                        if (root == null || !root.ContainsKey("data"))
                            return OnError(proxyManager, refresh_proxy: !rch.enable);

                        episodes = root["data"].ToObject<Episode[]>();

                        if (episodes.Length == 0)
                            return OnError();

                        if (!rch.enable)
                            proxyManager.Success();

                        hybridCache.Set(memKey, episodes, cacheTime(30, init: init));
                    }

                    #region Перевод
                    memKey = $"animelib:video:{episodes.First().id}";
                    if (!hybridCache.TryGetValue(memKey, out Player[] players))
                    {
                        if (rch.IsNotConnected())
                            return ContentTo(rch.connectionMsg);

                        string req_uri = $"{init.corsHost()}/api/episodes/{episodes.First().id}";

                        var root = rch.enable ? await rch.Get<JObject>(req_uri, headers) :
                                                await Http.Get<JObject>(req_uri, httpversion: 2, timeoutSeconds: 8, proxy: proxyManager.Get(), headers: headers);

                        if (root == null || !root.ContainsKey("data"))
                            return OnError(proxyManager, refresh_proxy: !rch.enable);

                        players = root["data"]["players"].ToObject<Player[]>();
                        hybridCache.Set(memKey, players, cacheTime(30, init: init));
                    }

                    var vtpl = new VoiceTpl(players.Length);
                    string activTranslate = t;

                    foreach (var player in players)
                    {
                        if (player.player != "Animelib")
                            continue;

                        if (string.IsNullOrEmpty(activTranslate))
                            activTranslate = player.team.name;

                        vtpl.Append(player.team.name, activTranslate == player.team.name, $"{host}/lite/animelib?rjson={rjson}&title={HttpUtility.UrlEncode(title)}&uri={HttpUtility.UrlEncode(uri)}&t={HttpUtility.UrlEncode(player.team.name)}");
                    }
                    #endregion

                    var etpl = new EpisodeTpl(episodes.Length);

                    foreach (var episode in episodes)
                    {
                        string name = string.IsNullOrEmpty(episode.name) ? title : $"{title} / {episode.name}";

                        string link = $"{host}/lite/animelib/video?id={episode.id}&voice={HttpUtility.UrlEncode(activTranslate)}&title={HttpUtility.UrlEncode(title)}";

                        etpl.Append($"{episode.number} серия", name, episode.season, episode.number, link, "call", streamlink: accsArgs($"{link}&play=true"));
                    }

                    if (rjson)
                        return ContentTo(etpl.ToJson(vtpl));

                    return ContentTo(vtpl.ToHtml() + etpl.ToHtml());
                });
                #endregion
            }
        }
```

**File:** Online/Controllers/Anime/AnimeGo.cs (L150-176)
```csharp
                    #region Перевод
                    var vtpl = new VoiceTpl(cache.translations.Count);
                    if (string.IsNullOrWhiteSpace(t))
                        t = cache.translation;

                    foreach (var translation in cache.translations)
                    {
                        string link = $"{host}/lite/animego?pid={pid}&title={HttpUtility.UrlEncode(title)}&s={s}&t={translation.id}";
                        vtpl.Append(translation.name, t == translation.id, link);
                    }
                    #endregion

                    var etpl = new EpisodeTpl(cache.links.Count);
                    string sArhc = s.ToString();

                    foreach (var l in cache.links)
                    {
                        string hls = accsArgs($"{host}/lite/animego/{l.uri}&t={t ?? cache.translation}");

                        etpl.Append($"{l.episode} серия", title, sArhc, l.episode, hls, "play", headers: headers_stream);
                    }

                    if (rjson)
                        return ContentTo(etpl.ToJson(vtpl));

                    return ContentTo(vtpl.ToHtml() + etpl.ToHtml());
                });
```

**File:** JacRed/Controllers/TolokaController.cs (L248-262)
```csharp
                    case "19":
                    case "139":
                    case "84":
                        types = new string[] { "multfilm" };
                        break;
                    case "32":
                    case "173":
                    case "124":
                        types = new string[] { "serial" };
                        break;
                    case "174":
                    case "44":
                    case "125":
                        types = new string[] { "multserial" };
                        break;
```

**File:** JacRed/Controllers/TolokaController.cs (L263-271)
```csharp
                    case "226":
                    case "227":
                    case "228":
                    case "229":
                    case "230":
                    case "12":
                    case "131":
                        types = new string[] { "docuserial", "documovie" };
                        break;
```

**File:** Online/Controllers/VideoCDN.cs (L87-149)
```csharp
            if (player.content_type is "movie" or "anime")
            {
                #region Фильм
                var mtpl = new MovieTpl(title, original_title, player.media.Length);

                foreach (var media in player.media)
                {
                    string hash = CrypTo.md5($"{init.clientId}:{content_type}:{content_id}:{media.playlist}:{requestInfo.IP}");
                    string link = accsArgs($"{host}/lite/videocdn/video?rjson={rjson}&content_id={content_id}&content_type={content_type}&playlist={HttpUtility.UrlEncode(media.playlist)}&max_quality={media.max_quality}&translation_id={media.translation_id}&hash={hash}");
                    string streamlink = link.Replace("/videocdn/video", "/videocdn/video.m3u8") + "&play=true";

                    mtpl.Append(media.translation_name, link, "call", streamlink, quality: media.max_quality?.ToString());
                }

                return ContentTo(rjson ? mtpl.ToJson() : mtpl.ToHtml());
                #endregion
            }
            else
            {
                #region Сериал
                string enc_title = HttpUtility.UrlEncode(title);
                string enc_original_title = HttpUtility.UrlEncode(original_title);

                if (s == -1)
                {
                    var tpl = new SeasonTpl(player.media.First().max_quality?.ToString(), player.media.Length);

                    foreach (var media in player.media.OrderBy(s => s.season_id))
                    {
                        string link = $"{host}/lite/videocdn?rjson={rjson}&content_id={content_id}&content_type={content_type}&title={enc_title}&original_title={enc_original_title}&s={media.season_id}";
                        tpl.Append($"{media.season_id} сезон", link, media.season_id);
                    }

                    return ContentTo(rjson ? tpl.ToJson() : tpl.ToHtml());
                }
                else
                {
                    #region Перевод
                    var vtpl = new VoiceTpl();
                    var tmpVoice = new HashSet<int>();

                    foreach (var media in player.media)
                    {
                        if (media.season_id != s)
                            continue;

                        foreach (var episode in media.episodes)
                        {
                            foreach (var voice in episode.media)
                            {
                                if (tmpVoice.Contains(voice.translation_id))
                                    continue;

                                tmpVoice.Add(voice.translation_id);

                                if (string.IsNullOrEmpty(t))
                                    t = voice.translation_id.ToString();

                                vtpl.Append(voice.translation_name, t == voice.translation_id.ToString(), $"{host}/lite/videocdn?rjson={rjson}&content_id={content_id}&content_type={content_type}&title={enc_title}&original_title={enc_original_title}&s={s}&t={voice.translation_id}");
                            }
                        }
                    }
                    #endregion
```

**File:** Shared/Engine/Online/VideoCDN.cs (L245-300)
```csharp
            if (result.type is "movie" or "anime")
            {
                #region Фильм
                if (result.movie == null || result.movie.Count == 0)
                    return string.Empty;

                var mtpl = new MovieTpl(title, original_title, result.movie.Count);

                foreach (var voice in result.movie)
                {
                    result.voices.TryGetValue(voice.Key, out string name);
                    if (string.IsNullOrEmpty(name))
                    {
                        if (result.movie.Count > 1)
                            continue;

                        name = "По умолчанию";
                    }

                    var streamquality = new StreamQualityTpl();

                    foreach (Match m in Regex.Matches(voice.Value, $"\\[(1080|720|480|360)p?\\]([^\\[\\|,\n\r\t ]+\\.(mp4|m3u8))"))
                    {
                        string link = m.Groups[2].Value;
                        if (string.IsNullOrEmpty(link))
                            continue;

                        if (usehls && !link.Contains(".m3u"))
                            link += ":hls:manifest.m3u8";
                        else if (!usehls && link.Contains(".m3u"))
                            link = link.Replace(":hls:manifest.m3u8", "");

                        streamquality.Insert(onstream($"{scheme}:{link}"), $"{m.Groups[1].Value}p");
                    }

                    mtpl.Append(name, streamquality.Firts().link, streamquality: streamquality);
                }

                return rjson ? mtpl.ToJson() : mtpl.ToHtml();
                #endregion
            }
            else
            {
                #region Сериал
                string enc_title = HttpUtility.UrlEncode(title);
                string enc_original_title = HttpUtility.UrlEncode(original_title);

                try
                {
                    if (result.serial == null || result.serial.Count == 0)
                        return string.Empty;

                    if (s == -1)
                    {
                        var seasons = new HashSet<int>();

```

**File:** JacRed/Engine/RedApi.cs (L152-168)
```csharp
                                else if (is_serial == 4)
                                {
                                    #region docuserial / documovie
                                    if (t.types.Contains("docuserial") || t.types.Contains("documovie"))
                                    {
                                        if (year > 0)
                                        {
                                            if (t.relased >= year - 1)
                                                AddTorrents(t);
                                        }
                                        else
                                        {
                                            AddTorrents(t);
                                        }
                                    }
                                    #endregion
                                }
```

**File:** JacRed/Engine/RedApi.cs (L241-270)
```csharp
                                if (is_serial == 1)
                                {
                                    if (t.types.Contains("movie") || t.types.Contains("multfilm") || t.types.Contains("anime") || t.types.Contains("documovie"))
                                        AddTorrents(t);
                                }
                                else if (is_serial == 2)
                                {
                                    if (t.types.Contains("serial") || t.types.Contains("multserial") || t.types.Contains("anime") || t.types.Contains("docuserial") || t.types.Contains("tvshow"))
                                        AddTorrents(t);
                                }
                                else if (is_serial == 3)
                                {
                                    if (t.types.Contains("tvshow"))
                                        AddTorrents(t);
                                }
                                else if (is_serial == 4)
                                {
                                    if (t.types.Contains("docuserial") || t.types.Contains("documovie"))
                                        AddTorrents(t);
                                }
                                else if (is_serial == 5)
                                {
                                    if (t.types.Contains("anime"))
                                        AddTorrents(t);
                                }
                                else
                                {
                                    AddTorrents(t);
                                }
                            }
```


---------

# Ответы на вопросы по созданию модуля для агрегатора Lampac

## 1. Формат данных провайдера

Каждый провайдер возвращает данные через **шаблоны** (MovieTpl, SeasonTpl, EpisodeTpl), которые имеют два метода вывода: `ToJson()` и `ToHtml()`.

### Формат JSON (когда rjson=true):

**Для фильмов** (MovieTpl): [1](#0-0) 

Структура: `{ type: "movie", voice: {...}, data: [...] }`, где в `data` массив объектов с полями: `method`, `url`, `stream`, `headers`, `quality`, `subtitles`, `translate`, `maxquality`, `details`, `year`, `title`, `hls_manifest_timeout`, `vast`.

**Для сезонов** (SeasonTpl): [2](#0-1) 

Структура: `{ type: "season", maxquality: "...", voice: {...}, data: [...] }`, где в `data` массив объектов с полями: `id`, `method`, `url`, `name`.

**Для эпизодов** (EpisodeTpl): [3](#0-2) 

Структура: `{ type: "episode", voice: {...}, data: [...] }`, где в `data` массив объектов с полями: `method`, `url`, `stream`, `headers`, `quality`, `subtitles`, `s` (номер сезона), `e` (номер эпизода), `details`, `name`, `title`, `hls_manifest_timeout`, `vast`.

### Нужна ли нормализация?

**Нет, агрегатор не нормализует данные**. В системе агрегации провайдеры просто собираются в список и сортируются по индексу: [4](#0-3) 

Каждый провайдер возвращает готовую структуру через свои шаблоны, агрегатор просто склеивает ссылки на провайдеры с метаданными (name, url, balanser).

## 2. Структура данных для сериалов

**Да, провайдеры уже отдают готовые структуры season/episode**. Провайдеры самостоятельно формируют правильный тип контента в зависимости от запроса:

- Если `s == -1` → используется `SeasonTpl` для списка сезонов
- Если `s >= 0` → используется `EpisodeTpl` для списка эпизодов сезона

Пример из контроллера Lumex показывает типичную логику: [5](#0-4) 

**Формат с season/episode внутри movie не используется** — всегда используются отдельные типы контента: "movie", "season" или "episode".

## 3. Ответ при rjson=false

**Да, нужно продолжать формировать HTML** для обратной совместимости с Lampa. Когда `rjson=false`, шаблоны генерируют HTML-разметку:

**MovieTpl.ToHtml()** создает HTML с `data-json` атрибутами: [6](#0-5) 

**SeasonTpl.ToHtml()** создает HTML для списка сезонов: [7](#0-6) 

**EpisodeTpl.ToHtml()** создает HTML для списка эпизодов: [8](#0-7) 

HTML формат используется для рендеринга в интерфейсе Lampa, JSON — для программного доступа.

## 4. Данные для прогресса плагина

Система отслеживания прогресса использует endpoint `/lifeevents`, который возвращает структуру: [9](#0-8) 

Формат данных для прогресса: `{ ready: bool, tasks: int, online: [...] }`, где в массиве `online` объекты вида: [10](#0-9) 

То есть: `{ name, url, index, show, balanser, rch }`. 

Дополнительно определяется **качество видео** из ответа провайдера: [11](#0-10) 

**Время ответа провайдера** отслеживается через глобальную систему статистики: [12](#0-11) 

Однако в текущей реализации прогресса **responseTime не передается** в структуру для плагина — только статус работоспособности (`work: bool`) и наличие результатов (`show: bool`).

## 5. Особенности кэширования

### Кэширование результатов поиска провайдеров:

TTL определяется через метод `cacheTime()` в базовом контроллере, обычно 10-40 минут: [13](#0-12) 

### Система кэширования (HybridCache):

Используется трехуровневая система: [14](#0-13) 

Конфигурация TTL для кэша задается через параметр `cache.extend`: [15](#0-14) 

### Прогресс агрегации:

Прогресс загрузки от провайдеров хранится в `memoryCache` и инвалидируется автоматически после завершения проверки всех провайдеров: [16](#0-15) 

**Примечание по SmartFilter**: В кодовой базе не найдено компонента с именем "SmartFilter" или "SmartFilter.conf". Возможно, это концепция из вашей разработки или внешней документации, которой нет в текущей версии Lampac.

## Notes

### Для создания модуля:

Модули для агрегации провайдеров должны реализовывать методы из `OnlineModuleEntry`: [17](#0-16) 

- **Version >= 3**: `Invoke` / `InvokeAsync` для основных событий, `Spider` / `SpiderAsync` для поиска
- **Version < 3**: `Events` / `EventsAsync` (legacy)

Модель данных для основных событий: [18](#0-17) 

Модель для поиска: [19](#0-18) 

Ваш модуль должен возвращать `List<(string name, string url, string plugin, int index)>` — список провайдеров с их метаданными.

### Citations

**File:** Shared/Models/Templates/MovieTpl.cs (L36-77)
```csharp
        public string ToHtml(bool reverse = false)
        {
            if (data.Count == 0)
                return string.Empty;

            bool firstjson = true;
            var html = new StringBuilder();
            html.Append("<div class=\"videos__line\">");

            if (reverse)
                data.Reverse();

            foreach (var i in data) 
            {
                string datajson = JsonSerializer.Serialize(new
                {
                    i.method,
                    url = i.link,
                    i.stream,
                    headers = i.headers != null ? i.headers.ToDictionary(k => k.name, v => v.val) : null,
                    quality = i.streamquality?.ToObject(),
                    subtitles = i.subtitles?.ToObject(),
                    translate = i.voiceOrQuality,
                    maxquality = i.streamquality?.MaxQuality() ?? i.quality,
                    i.voice_name,
                    i.details,
                    year = int.TryParse(i.year, out int _year) ? _year : 0,
                    title = $"{title ?? original_title} ({i.voiceOrQuality})",
                    i.hls_manifest_timeout,
                    vast = i.vast ?? AppInit.conf.vast

                }, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault });

                html.Append($"<div class=\"videos__item videos__movie selector {(firstjson ? "focused" : "")}\" media=\"\" data-json='{datajson}'><div class=\"videos__item-imgbox videos__movie-imgbox\"></div><div class=\"videos__item-title\">{HttpUtility.HtmlEncode(i.voiceOrQuality)}</div></div>");
                firstjson = false;

                if (!string.IsNullOrEmpty(i.quality))
                    html.Append($"<!--{i.quality}p-->");
            }

            return html.ToString() + "</div>";
        }
```

**File:** Shared/Models/Templates/MovieTpl.cs (L79-110)
```csharp
        public string ToJson(bool reverse = false, in VoiceTpl? vtpl = null)
        {
            if (data.Count == 0)
                return "[]";

            if (reverse)
                data.Reverse();

            string name = title ?? original_title;

            return JsonSerializer.Serialize(new
            {
                type = "movie",
                voice = vtpl?.ToObject(),
                data = data.Select(i => new
                {
                    i.method,
                    url = i.link,
                    i.stream,
                    headers = i.headers != null ? i.headers.ToDictionary(k => k.name, v => v.val) : null,
                    quality = i.streamquality?.ToObject(),
                    subtitles = i.subtitles?.ToObject(),
                    translate = i.voiceOrQuality,
                    maxquality = i.streamquality?.MaxQuality() ?? i.quality,
                    details = (i.voice_name == null && i.details == null) ? null : (i.voice_name + i.details),
                    year = int.TryParse(i.year, out int _year) ? _year : 0,
                    title = $"{name} ({i.voiceOrQuality})",
                    i.hls_manifest_timeout,
                    vast = i.vast ?? AppInit.conf.vast
                })
            }, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault });
        }
```

**File:** Shared/Models/Templates/SeasonTpl.cs (L36-59)
```csharp
        public string ToHtml(in VoiceTpl? vtpl = null)
        {
            if (data.Count == 0)
                return string.Empty;

            bool firstjson = true;
            var html = new StringBuilder();

            if (vtpl != null)
                html.Append(vtpl?.ToHtml());

            html.Append("<div class=\"videos__line\">");

            if (!string.IsNullOrEmpty(quality))
                html.Append($"<!--q:{quality}-->");

            foreach (var i in data) 
            {
                html.Append("<div class=\"videos__item videos__season selector " + (firstjson ? "focused" : "") + "\" data-json='{\"method\":\"link\",\"url\":\"" + i.link + "\"}'><div class=\"videos__season-layers\"></div><div class=\"videos__item-imgbox videos__season-imgbox\"><div class=\"videos__item-title videos__season-title\">" + HttpUtility.HtmlEncode(i.name) + "</div></div></div>");
                firstjson = false;
            }

            return html.ToString() + "</div>";
        }
```

**File:** Shared/Models/Templates/SeasonTpl.cs (L61-79)
```csharp
        public string ToJson(in VoiceTpl? vtpl = null)
        {
            if (data.Count == 0)
                return "[]";

            return JsonSerializer.Serialize(new
            {
                type = "season",
                maxquality = quality,
                voice = vtpl?.ToObject(),
                data = data.Select(i => new
                {
                    i.id,
                    method = "link",
                    url = i.link,
                    i.name
                })
            }, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        }
```

**File:** Shared/Models/Templates/EpisodeTpl.cs (L26-57)
```csharp
        public string ToHtml()
        {
            if (data.Count == 0)
                return string.Empty;

            bool firstjson = true;
            var html = new StringBuilder();
            html.Append("<div class=\"videos__line\">");

            foreach (var i in data) 
            {
                string datajson = JsonSerializer.Serialize(new
                {
                    i.method,
                    url = i.link,
                    i.title,
                    stream = i.streamlink,
                    headers = i.headers != null ? i.headers.ToDictionary(k => k.name, v => v.val) : null,
                    quality = i.streamquality?.ToObject(),
                    subtitles = i.subtitles?.ToObject(),
                    i.voice_name,
                    i.hls_manifest_timeout,
                    vast = i.vast ?? AppInit.conf.vast

                }, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault });

                html.Append($"<div class=\"videos__item videos__movie selector {(firstjson ? "focused" : "")}\" media=\"\" s=\"{i.s}\" e=\"{i.e}\" data-json='{datajson}'><div class=\"videos__item-imgbox videos__movie-imgbox\"></div><div class=\"videos__item-title\">{HttpUtility.HtmlEncode(i.name)}</div></div>");
                firstjson = false;
            }

            return html.ToString() + "</div>";
        }
```

**File:** Shared/Models/Templates/EpisodeTpl.cs (L59-85)
```csharp
        public string ToJson(in VoiceTpl? vtpl = null)
        {
            if (data.Count == 0)
                return "[]";

            return JsonSerializer.Serialize(new
            {
                type = "episode",
                voice = vtpl?.ToObject(),
                data = data.Select(i => new
                {
                    i.method,
                    url = i.link,
                    stream = i.streamlink,
                    headers = i.headers != null ? i.headers.ToDictionary(k => k.name, v => v.val) : null,
                    quality = i.streamquality?.ToObject(),
                    subtitles = i.subtitles?.ToObject(),
                    s = int.TryParse(i.s, out int _s) ? _s : 0,
                    e = int.TryParse(i.e, out int _e) ? _e : 0,
                    details = i.voice_name,
                    i.name,
                    i.title,
                    i.hls_manifest_timeout,
                    vast = i.vast ?? AppInit.conf.vast
                })
            }, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        }
```

**File:** Online/OnlineApi.cs (L564-590)
```csharp
        public ActionResult LifeEvents(string memkey, long id, string imdb_id, long kinopoisk_id, int serial)
        {
            string json = null;
            JsonResult error(string msg) => Json(new { accsdb = true, ready = true, online = new string[] { }, msg });

            List<(string code, int index, bool work)> _links = null;
            if (memoryCache.TryGetValue(memkey, out List<(string code, int index, bool work)> links))
                _links = links.ToList();

            if (_links != null && _links.Count(i => i.code != null) > 0)
            {
                bool ready = _links.Count == _links.Count(i => i.code != null);
                string online = string.Join(",", _links.Where(i => i.code != null).OrderByDescending(i => i.work).ThenBy(i => i.index).Select(i => i.code));

                if (ready && !online.Contains("\"show\":true"))
                {
                    if (string.IsNullOrEmpty(imdb_id) && 0 >= kinopoisk_id)
                        return error($"Добавьте \"IMDB ID\" {(serial == 1 ? "сериала" : "фильма")} на https://themoviedb.org/{(serial == 1 ? "tv" : "movie")}/{id}/edit?active_nav_item=external_ids");

                    return error($"Не удалось найти онлайн для {(serial == 1 ? "сериала" : "фильма")}");
                }

                json = "{" + $"\"ready\":{ready.ToString().ToLower()},\"tasks\":{_links.Count},\"online\":[{online.Replace("{localhost}", host)}]" + "}";
            }

            return Content(json ?? "{\"ready\":false,\"tasks\":0,\"online\":[]}", contentType: "application/javascript; charset=utf-8");
        }
```

**File:** Online/OnlineApi.cs (L1108-1109)
```csharp
            string online_result = string.Join(",", online.OrderBy(i => i.index).Select(i => "{\"name\":\"" + i.name + "\",\"url\":\"" + i.url + "\",\"balanser\":\"" + i.plugin + "\"}"));
            return ContentTo($"[{online_result.Replace("{localhost}", host)}]");
```

**File:** Online/OnlineApi.cs (L1137-1153)
```csharp
                #region определение качества
                if (work && life)
                {
                    foreach (string q in new string[] { "2160", "1080", "720", "480", "360" })
                    {
                        if (res.Contains("<!--q:"))
                        {
                            quality = " - " + Regex.Match(res, "<!--q:([^>]+)-->").Groups[1].Value;
                            break;
                        }
                        else if (res.Contains($"\"{q}p\"") || res.Contains($">{q}p<") || res.Contains($"<!--{q}p-->"))
                        {
                            quality = $" - {q}p";
                            break;
                        }
                    }

```

**File:** Online/OnlineApi.cs (L1275-1275)
```csharp
                links[indexList] = ("{" + $"\"name\":\"{name}\",\"url\":\"{uri}\",\"index\":{index},\"show\":{work.ToString().ToLower()},\"balanser\":\"{plugin}\",\"rch\":{rch.ToString().ToLower()}" + "}", index, work);
```

**File:** Online/Controllers/Lumex.cs (L29-76)
```csharp
        [HttpGet]
        [Route("lite/lumex")]
        async public ValueTask<ActionResult> Index(long content_id, string content_type, string imdb_id, long kinopoisk_id, string title, string original_title, string t, int clarification, int s = -1, int serial = -1, bool origsource = false, bool rjson = false, bool similar = false)
        {
            var init = await loadKit(AppInit.conf.Lumex);
            if (await IsBadInitialization(init, rch: false))
                return badInitMsg;

            if (init.priorityBrowser == "firefox")
            {
                if (Firefox.Status == PlaywrightStatus.disabled)
                    return OnError("Firefox disabled");
            }
            else if (init.priorityBrowser != "http")
            {
                if (Chromium.Status == PlaywrightStatus.disabled)
                    return OnError("Chromium disabled");
            }

            var proxyManager = new ProxyManager(init);
            var proxy = proxyManager.BaseGet();

            var oninvk = new LumexInvoke
            (
               init,
               (url, referer) => Http.Get(init.cors(url), referer: referer, timeoutSeconds: 8, proxy: proxy.proxy, headers: httpHeaders(init)),
               streamfile => HostStreamProxy(init, streamfile, proxy: proxy.proxy),
               host,
               requesterror: () => proxyManager.Refresh()
            );

            if (similar || (content_id == 0 && kinopoisk_id == 0 && string.IsNullOrEmpty(imdb_id)))
            {
                string memKey = $"lumex:search:{title}:{original_title}:{clarification}";

                return await InvkSemaphore(init, memKey, async () =>
                {
                    if (!hybridCache.TryGetValue(memKey, out SimilarTpl search))
                    {
                        search = await oninvk.Search(title, original_title, serial, clarification, database);
                        if (search.data?.Count == 0)
                            return OnError("search");

                        hybridCache.Set(memKey, search, cacheTime(40, init: init));
                    }

                    return ContentTo(rjson ? search.ToJson() : search.ToHtml());
                });
```

**File:** Lampac/Engine/Middlewares/RequestStatistics.cs (L1-50)
```csharp
using Microsoft.AspNetCore.Http;
using Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Lampac.Engine.Middlewares
{
    public class RequestStatistics
    {
        private readonly RequestDelegate _next;

        public RequestStatistics(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            bool trackStats = !(context.Request.Path.StartsWithSegments("/ws") || context.Request.Path.StartsWithSegments("/nws"));
            Stopwatch stopwatch = null;

            if (trackStats && AppInit.conf.openstat.enable)
                stopwatch = RequestStatisticsTracker.StartRequest();

            try
            {
                await _next(context);
            }
            finally
            {
                if (trackStats && AppInit.conf.openstat.enable)
                    RequestStatisticsTracker.CompleteRequest(stopwatch);
            }
        }
    }

    public static class RequestStatisticsTracker
    {
        static int activeHttpRequests;
        static readonly ConcurrentQueue<(DateTime timestamp, double durationMs)> ResponseTimes = new();

        public static int ActiveHttpRequests => Volatile.Read(ref activeHttpRequests);

        internal static Stopwatch StartRequest()
        {
            Interlocked.Increment(ref activeHttpRequests);
```

**File:** Shared/Engine/HybridCache.cs (L1-50)
```csharp
﻿using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared.Models.SQL;
using System.Collections.Concurrent;
using System.Threading;

namespace Shared.Engine
{
    public struct HybridCache
    {
        #region HybridCache
        static IMemoryCache memoryCache;

        static Timer _clearTimer;

        static DateTime _nextClearDb = DateTime.Now.AddMinutes(20);

        static ConcurrentDictionary<string, (DateTime extend, HybridCacheSqlModel cache)> tempDb;

        public static void Configure(IMemoryCache mem)
        {
            memoryCache = mem;

            tempDb = new ConcurrentDictionary<string, (DateTime extend, HybridCacheSqlModel value)>();
            _clearTimer = new Timer(UpdateDB, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }

        static bool updatingDb = false;
        async static void UpdateDB(object state)
        {
            if (updatingDb || tempDb.Count == 0)
                return;

            try
            {
                updatingDb = true;

                var sqlDb = HybridCacheDb.Write;
                sqlDb.ChangeTracker.Clear();

                if (DateTime.Now > _nextClearDb)
                {
                    _nextClearDb = DateTime.Now.AddMinutes(20);

                    var now = DateTime.Now;

                    await sqlDb.files
                         .Where(i => now > i.ex)
```

**File:** Shared/AppInit.cs (L1-50)
```csharp
﻿using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Shared.Engine;
using Shared.Models;
using Shared.Models.AppConf;
using Shared.Models.Base;
using Shared.Models.Browser;
using Shared.Models.DLNA;
using Shared.Models.Merchant;
using Shared.Models.Module;
using Shared.Models.Online.Settings;
using Shared.Models.ServerProxy;
using Shared.Models.SISI.Base;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using YamlDotNet.Serialization;

namespace Shared
{
    public class AppInit
    {
        #region static
        public static string rootPasswd;

        public static bool Win32NT => Environment.OSVersion.Platform == PlatformID.Win32NT;

        public static bool IsDefaultApnOrCors(string apn) => apn != null && Regex.IsMatch(apn, "(apn.monster|apn.watch|cfhttp.top|lampac.workers.dev)");

        static FileSystemWatcher fileWatcher;

        static AppInit()
        {
            updateConf();
            if (File.Exists("init.conf"))
                lastUpdateConf = File.GetLastWriteTime("init.conf");

            updateYamlConf();
            if (File.Exists("init.yaml") && File.GetLastWriteTime("init.yaml") > lastUpdateConf)
                lastUpdateConf = File.GetLastWriteTime("init.yaml");

            LoadModules();

            #region watcherInit
            if (conf.watcherInit == "system")
            {
                fileWatcher = new FileSystemWatcher
                {
                    Path = Directory.GetCurrentDirectory(),
                    Filter = "init.conf",
```

**File:** Online/OnlineModuleEntry.cs (L7-20)
```csharp
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
```

**File:** Shared/Models/Module/OnlineEventsModel.cs (L1-39)
```csharp
﻿namespace Shared.Models.Module
{
    public class OnlineEventsModel
    {
        public OnlineEventsModel(long id, string imdb_id, long kinopoisk_id, string title, string original_title, string original_language, int year, string source, string rchtype, int serial, bool life, bool islite, string account_email, string uid, string token)
        {
            this.id = id;
            this.imdb_id = imdb_id;
            this.kinopoisk_id = kinopoisk_id;
            this.title = title;
            this.original_title = original_title;
            this.original_language = original_language;
            this.year = year;
            this.source = source;
            this.rchtype = rchtype;
            this.serial = serial;
            this.life = life;
            this.islite = islite;
            this.account_email = account_email;
            this.uid = uid;
            this.token = token;
        }

        public long id { get; set; }
        public string imdb_id { get; set; }
        public long kinopoisk_id { get; set; }
        public string title { get; set; }
        public string original_title { get; set; }
        public string original_language { get; set; }
        public int year { get; set; }
        public string source { get; set; }
        public string rchtype { get; set; }
        public int serial { get; set; }
        public bool life { get; set; }
        public bool islite { get; set; }
        public string account_email { get; set; }
        public string uid { get; set; }
        public string token { get; set; }
    }
```

**File:** Shared/Models/Module/OnlineSpiderModel.cs (L1-15)
```csharp
namespace Shared.Models.Module
{
    public class OnlineSpiderModel
    {
        public OnlineSpiderModel(string title, bool isanime)
        {
            this.title = title;
            this.isanime = isanime;
        }

        public string title { get; set; }
        public bool isanime { get; set; }
        public bool requireRhub { get; set; }
    }
}
```
