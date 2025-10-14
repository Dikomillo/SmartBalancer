# Codex Agent Rules — Lampac Project

**Пиши и отвечай на русском языке.** 

Основной пепозиторий:
- https://deepwiki.com/immisterio/Lampac - документация 
- https://github.com/immisterio/Lampac/main/ - основной репозиторий проекта (не мой)



## 📘 Общие правила

- Этот проект использует документацию из DeepWiki репозитория `immisterio/Lampac`.
- https://deepwiki.com/immisterio/Lampac - документация 
- Все архитектурные и кодовые решения принимаются **только после опроса DeepWiki**.
- Перед любым кодом агент обязан понять и подтвердить что понял всю документацию.
- Если уверенность < 99 % — код не писать, продолжать уточнения.

---

## 🧭 Этап 1 — Анализ
   Прочитать минимум:
   - `https://deepwiki.com/immisterio/Lampac`
   - `https://github.com/immisterio/Lampac/main/`
   - `релевантные гайды/модули`  


---

## 🧠 Этап 2 — Подтверждение понимания
- Составить краткое резюме структуры.
- Перечислить найденные интерфейсы/точки подключения.
- Сформировать план реализации.
- Проверить, что все вопросы закрыты.

⚠️ Если не уверен — вернуться к Этапу 1.

---

## 🛠 Этап 3 — Кодинг
- Перед созданием каждого файла сверяться с DeepWiki и GitHub Lampac
- Все решения обосновывать на основе  документации.
- После завершения тесты не нужны.
- Не выдумывать API; использовать только подтверждённые сигнатуры.
- Следовать соглашениям репозитория (имена, структура, формат ответов для Lampa).
- По окончании — финальная сверка с архитектурными требованиями.
- Соблюдение логики
- Не выдумать ничего, лучше спросить и потом кодить.
- Современное написание кода, кортко, понятно, расширяемо, читаемо, легкая поддержка, динамически продумаваем все что бы код не ломался при малейших изменениях.
---

## 🧪 Этап 4 — Тесты и интеграция
- не нужны

---

## 🤖 Поведение агента
- Никогда не писать код, не поняв документацию.
- Всегда ссылаться на источники из DeepWiki.
- Всегда проходить этап анализа перед реализацией.
- При сомнениях — **вопрос, а не код**.

# README: Создание модуля-агрегатора для Lampac

## Обзор

Агрегатор в Lampac — это модуль, который опрашивает несколько провайдеров параллельно, объединяет их ответы и формирует единый вывод для просмотра контента (фильмы, сериалы, аниме).

## Архитектура

### Основные компоненты

1. **Контроллер** (`YourAggregatorController.cs`) - наследуется от `BaseOnlineController` [1](#5-0) 
2. **Настройки** (`YourAggregatorSettings.cs`) - наследуется от `BaseSettings`
3. **Манифест** (`manifest.json`) - регистрация модуля
4. **Методы интеграции** - `Invoke()` и `InvokeAsync()` для `/lite/events` [2](#5-1) 

### Структура файлов

```
module/YourAggregator/
├── manifest.json          # Регистрация модуля
├── YourAggregatorController.cs  # Главный контроллер
├── YourAggregatorSettings.cs    # Настройки
└── OnlineApi.cs          # Интеграция с /lite/events
```

## Реализация

### 1. Манифест (manifest.json)

```json
{
  "enable": true,
  "version": 3,
  "dll": "YourAggregator",
  "namespace": "YourAggregator",
  "initspace": "ModInit",
  "online": "YourAggregatorController",
  "name": "YourAggregator",
  "index": 5
}
```

### 2. Контроллер

**Маршрут:** `/lite/youraggregator` [3](#5-2) 

**Параметры запроса:**
- `id`, `imdb_id`, `kinopoisk_id` - идентификаторы
- `title`, `original_title` - названия
- `s` - сезон (`-1` = выбор сезона, `1+` = конкретный сезон) [4](#5-3) 
- `t` - озвучка (ID перевода) [5](#5-4) 
- `rjson` - формат вывода (JSON/HTML)

**Основная логика:**

```csharp
[Route("lite/youraggregator")]
public class YourAggregatorController : BaseOnlineController
{
    [HttpGet]
    public async Task<ActionResult> Index(long id, string imdb_id, 
        long kinopoisk_id, string title, int s = -1, string t = null, bool rjson = false)
    {
        var init = await loadKit(AppInit.conf.YourAggregator);
        if (await IsBadInitialization(init))
            return badInitMsg;

        // 1. Опрос провайдеров
        var results = await QueryProviders(id, imdb_id, kinopoisk_id, title);
        
        // 2. Формирование вывода
        return s == -1 ? RenderSeasons(results, rjson) : 
                         RenderEpisodes(results, s, t, rjson);
    }
}
```

### 3. Параллельный опрос провайдеров

**Получение URL провайдеров:** [6](#5-5) 

```csharp
async Task<List<string>> QueryProviders(long id, string imdb_id, long kinopoisk_id, string title)
{
    var urls = new List<string>();
    var args = new OnlineEventsModel(imdb_id, kinopoisk_id.ToString(), title);
    
    // Вызов Invoke() каждого провайдера
    foreach (var provider in OnlineModuleEntry.onlineModulesCache)
    {
        if (provider.Invoke != null)
        {
            var results = provider.Invoke(HttpContext, memoryCache, requestInfo, host, args);
            urls.AddRange(results.Select(r => r.url));
        }
    }
    
    // Параллельный опрос
    var responses = await Task.WhenAll(
        urls.Select(url => FetchAndParse(url))
    );
    
    return responses.Where(r => r != null).ToList();
}
```

### 4. Кэширование

**Используйте `InvokeCache` с семафорами:** [7](#5-6) 

```csharp
var cache = await InvokeCache<List<Result>>(
    $"youraggregator:{id}:{imdb_id}:{kinopoisk_id}",
    cacheTime(20, init: init),  // 20 минут
    proxyManager,
    async res => {
        var data = await QueryProviders(...);
        if (data == null || data.Count == 0)
            return res.Fail("no data");
        return data;
    }
);
```

**Время кэша по типу доступа:**
- `multiaccess` - минуты
- `home` - часы  
- `mikrotik` - дни

### 5. Формирование вывода

#### Фильмы (MovieTpl) [8](#5-7) 

```csharp
var mtpl = new MovieTpl(title, original_title);
foreach (var item in results)
{
    mtpl.Append(
        item.voice,           // Название озвучки
        item.link,            // Ссылка на видео
        quality: item.quality // Качество (1080p, 720p)
    );
}
return ContentTo(rjson ? mtpl.ToJson() : mtpl.ToHtml());
```

#### Сериалы - Сезоны (SeasonTpl) [9](#5-8) 

```csharp
if (s == -1)
{
    var tpl = new SeasonTpl();
    var seasons = new HashSet<int>();
    
    foreach (var item in results)
    {
        if (!seasons.Contains(item.season))
        {
            seasons.Add(item.season);
            tpl.Append(
                $"{item.season} сезон",
                $"{host}/lite/youraggregator?s={item.season}&...",
                item.season
            );
        }
    }
    return ContentTo(rjson ? tpl.ToJson() : tpl.ToHtml());
}
```

#### Сериалы - Озвучки (VoiceTpl) [10](#5-9) 

```csharp
var vtpl = new VoiceTpl();
var voices = new HashSet<string>();

foreach (var item in results.Where(r => r.season == s))
{
    if (!voices.Contains(item.voice))
    {
        voices.Add(item.voice);
        vtpl.Append(
            item.voice,
            t == item.voiceId,  // Активная озвучка
            $"{host}/lite/youraggregator?s={s}&t={item.voiceId}&..."
        );
    }
}
```

#### Сериалы - Серии (EpisodeTpl) [11](#5-10) 

```csharp
var etpl = new EpisodeTpl();
string sArhc = s.ToString();

foreach (var item in results.Where(r => r.season == s && r.voiceId == t))
{
    etpl.Append(
        $"{item.episode} серия",  // Название
        title,                     // Название сериала
        sArhc,                     // Сезон как строка
        item.episode.ToString(),   // Номер серии
        item.stream                // Ссылка на видео
    );
}

// Комбинированный вывод
return rjson ? etpl.ToJson(vtpl) : vtpl.ToHtml() + etpl.ToHtml();
```

### 6. Интеграция с /lite/events

**OnlineApi.cs:**

```csharp
public static List<(string name, string url, string plugin, int index)> Invoke(
    HttpContext httpContext,
    IMemoryCache memoryCache,
    RequestModel requestInfo,
    string host,
    OnlineEventsModel args)
{
    var url = $"{host}/lite/youraggregator?id={args.id}&imdb_id={args.imdb_id}&kinopoisk_id={args.kinopoisk_id}";
    return new List<(string,string,string,int)>
    {
        ("YourAggregator", url, "youraggregator", 0)
    };
}
```

## Критические нюансы

### 1. Провайдеры возвращают URL, не данные

Провайдеры в `/lite/events` возвращают **ссылки** на свои endpoints, а не готовые данные. [6](#5-5)  Вам нужно:
1. Получить URL через `Invoke()`
2. Сделать HTTP GET к каждому URL
3. Распарсить ответ (HTML/JSON)

### 2. Семафоры предотвращают cache stampede

`InvokeCache` автоматически блокирует параллельные запросы с одинаковым ключом. [7](#5-6)  Генерируйте уникальные ключи кэша.

### 3. RCH для недоступных провайдеров

Если провайдер недоступен с VPS, используйте Remote Client Hub: [12](#5-11) 

```csharp
var rch = new RchClient(HttpContext, host, init, requestInfo);
if (rch.IsNotSupport("web", out string rch_error))
    return ShowError(rch_error);
```

### 4. Трёхуровневая навигация обязательна

Для сериалов строго соблюдайте порядок:
1. `s=-1` → Сезоны (SeasonTpl)
2. `s=1` → Озвучки (VoiceTpl)  
3. `s=1&t=voice1` → Серии (EpisodeTpl)

### 5. Качество видео

Если провайдер не возвращает качество, используйте таблицу по умолчанию: [13](#5-12) 

### 6. Обработка ошибок

Ошибки в одном провайдере не должны ломать весь агрегатор. [14](#5-13)  Используйте `try-catch` в параллельных задачах.

## Пример полного цикла

1. Пользователь кликает на "YourAggregator" в `/lite/events`
2. Lampac вызывает `/lite/youraggregator?id=123&imdb_id=tt456`
3. Контроллер опрашивает провайдеры через их `Invoke()`
4. Получает URL: `["/lite/alloha?id=123", "/lite/rezka?id=123"]`
5. Делает HTTP GET к каждому URL параллельно
6. Парсит ответы, извлекает озвучки/серии/качество
7. Формирует `MovieTpl`/`SeasonTpl`/`EpisodeTpl`
8. Возвращает HTML/JSON через `ContentTo()`

## Ссылки

- Примеры провайдеров: `Online/Controllers/` [15](#5-14) 
- Шаблоны: `Shared/Models/Templates/`
- Базовый контроллер: `Shared/BaseOnlineController.cs`

Wiki pages you might want to explore:
- [Lampac Overview (immisterio/Lampac)](/wiki/immisterio/Lampac#1)
- [Content Aggregation System (immisterio/Lampac)](/wiki/immisterio/Lampac#6)
- [Remote Client Hub (RCH) System (immisterio/Lampac)](/wiki/immisterio/Lampac#8.3)

### Citations

**File:** Online/Controllers/Alloha.cs (L1-10)
```csharp
﻿using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared.Models.Online.Alloha;
using Shared.Models.Online.Settings;
using Shared.PlaywrightCore;

namespace Online.Controllers
{
    public class Alloha : BaseOnlineController
```

**File:** Online/Controllers/Alloha.cs (L36-36)
```csharp
        [Route("lite/alloha")]
```

**File:** Online/Controllers/Alloha.cs (L62-81)
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
```

**File:** Online/OnlineApi.cs (L628-702)
```csharp

            #region modules
            OnlineModuleEntry.EnsureCache();

            if (OnlineModuleEntry.onlineModulesCache != null && OnlineModuleEntry.onlineModulesCache.Count > 0)
            {
                var args = new OnlineEventsModel(id, imdb_id, kinopoisk_id, title, original_title, original_language, year, source, rchtype, serial, life, islite, account_email, uid, token);

                foreach (var entry in OnlineModuleEntry.onlineModulesCache)
                {
                    try
                    {
                        #region version >= 3 methods
                        if (entry.Invoke != null)
                        {
                            try
                            {
                                var result = entry.Invoke(HttpContext, memoryCache, requestInfo, host, args);
                                if (result != null && result.Count > 0)
                                {
                                    foreach (var r in result)
                                        online.Add((null, r.name, r.url, r.plugin, r.index));
                                }
                            }
                            catch { }
                        }

                        if (entry.InvokeAsync != null)
                        {
                            try
                            {
                                var result = await entry.InvokeAsync(HttpContext, memoryCache, requestInfo, host, args);
                                if (result != null && result.Count > 0)
                                {
                                    foreach (var r in result)
                                        online.Add((null, r.name, r.url, r.plugin, r.index));
                                }
                            }
                            catch { }
                        }
                        #endregion

                        #region version < 3 legacy methods
                        if (entry.Events != null)
                        {
                            try
                            {
                                var result = entry.Events(host, id, imdb_id, kinopoisk_id, title, original_title, original_language, year, source, serial, account_email);
                                if (result != null && result.Count > 0)
                                {
                                    foreach (var r in result)
                                        online.Add((null, r.name, r.url, r.plugin, r.index));
                                }
                            }
                            catch { }
                        }

                        if (entry.EventsAsync != null)
                        {
                            try
                            {
                                var result = await entry.EventsAsync(HttpContext, memoryCache, host, id, imdb_id, kinopoisk_id, title, original_title, original_language, year, source, serial, account_email);
                                if (result != null && result.Count > 0)
                                {
                                    foreach (var r in result)
                                        online.Add((null, r.name, r.url, r.plugin, r.index));
                                }
                            }
                            catch { }
                        }
                        #endregion
                    }
                    catch (Exception ex) { Console.WriteLine($"Modules {entry.mod?.NamespacePath(entry.mod.online)}: {ex.Message}\n\n"); }
                }
            }
```

**File:** Online/OnlineApi.cs (L1230-1265)
```csharp
                            case "hydraflix":
                            case "movpi":
                            case "videasy":
                            case "vidlink":
                            case "autoembed":
                            case "veoveo":
                            case "vokino-vibix":
                            case "vokino-monframe":
                            case "vokino-remux":
                            case "vokino-ashdi":
                            case "vokino-hdvb":
                                quality = " ~ 1080p";
                                break;
                            case "voidboost":
                            case "animedia":
                            case "animevost":
                            case "animebesst":
                            case "kodik":
                            case "kinotochka":
                            case "rhs":
                                quality = " ~ 720p";
                                break;
                            case "kinokrad":
                            case "kinoprofi":
                            case "seasonvar":
                                quality = " - 480p";
                                break;
                            case "cdnmovies":
                                quality = " - 360p";
                                break;
                            default:
                                break;
                        }

                        if (balanser == "vokino")
                            quality = res.Contains("4K HDR") ? " - 4K HDR" : res.Contains("4K ") ? " - 4K" : quality;
```

**File:** Online/Controllers/VeoVeo.cs (L79-79)
```csharp
                    if (s == -1)
```

**File:** Online/Controllers/VeoVeo.cs (L81-94)
```csharp
                        var tpl = new SeasonTpl();
                        var hash = new HashSet<int>();

                        foreach (var item in cache.Value)
                        {
                            var season = item["season"].Value<int>("order");
                            if (hash.Contains(season))
                                continue;

                            hash.Add(season);
                            string link = $"{host}/lite/veoveo?rjson={rjson}&kinopoisk_id={kinopoisk_id}&imdb_id={imdb_id}&title={HttpUtility.UrlEncode(title)}&original_title={HttpUtility.UrlEncode(original_title)}&s={season}";
                            tpl.Append($"{season} сезон", link, season);
                        }

```

**File:** Online/Controllers/VeoVeo.cs (L97-116)
```csharp
                    else
                    {
                        var episodes = cache.Value.Where(i => i["season"].Value<int>("order") == s);

                        var etpl = new EpisodeTpl(episodes.Count());
                        string sArhc = s.ToString();

                        foreach (var episode in episodes.OrderBy(i => i.Value<int>("order")))
                        {
                            string name = episode.Value<string>("title");
                            string file = episode.Value<string>("m3u8MasterFilePath");

                            if (string.IsNullOrEmpty(file))
                                continue;

                            string stream = HostStreamProxy(init, file, proxy: proxy);
                            etpl.Append(name ?? $"{episode.Value<int>("order")} серия", title ?? original_title, sArhc, episode.Value<int>("order").ToString(), stream, vast: init.vast);
                        }

                        return rjson ? etpl.ToJson() : etpl.ToHtml();
```

**File:** Shared/Engine/Online/Kodik.cs (L254-282)
```csharp
                    #region Перевод
                    var vtpl = new VoiceTpl();
                    HashSet<string> hash = new HashSet<string>();

                    foreach (var item in results)
                    {
                        string id = item.id;
                        if (string.IsNullOrEmpty(id))
                            continue;

                        string name = item.translation.title ?? "оригинал";
                        if (hash.Contains(name))
                            continue;

                        if (item.last_season != s)
                        {
                            if (item.seasons == null || !item.seasons.ContainsKey(s.ToString()))
                                continue;
                        }

                        hash.Add(name);

                        if (string.IsNullOrEmpty(kid))
                            kid = id;

                        string link = host + $"lite/kodik?rjson={rjson}&imdb_id={imdb_id}&kinopoisk_id={kinopoisk_id}&title={enc_title}&original_title={enc_original_title}&clarification={clarification}&pick={enc_pick}&s={s}&kid={id}";

                        vtpl.Append(name, kid == id, link);
                    }
```

**File:** Online/Controllers/Lumex.cs (L64-76)
```csharp
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

**File:** Online/Controllers/CDNmovies.cs (L19-21)
```csharp
            var rch = new RchClient(HttpContext, host, init, requestInfo);
            if (rch.IsNotSupport("web", out string rch_error))
                return ShowError(rch_error);
```
