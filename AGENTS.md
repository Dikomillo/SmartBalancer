Первым делом Изучи Документацию и всю что нужно в этом репозитории - https://github.com/immisterio/Lampac

# План реализации агрегатора сериалов

Я изучил кодовую базу Lampac и нашел, как реализована работа с сериалами. Вот подробная логика, которую вы можете использовать:

## Основная архитектура

В Lampac используется трёхуровневая структура для сериалов:

1. **Сезоны** → 2. **Переводы/Озвучки** (опционально) → 3. **Серии**

Для этого используются три шаблонных класса: [1](#0-0) [2](#0-1) [3](#0-2) 

## Логика работы по параметру `s`

Ключевая логика построена на параметре `s` (season):

**Когда `s == -1`** → отображаем список сезонов: [4](#0-3) 

**Когда `s >= 1`** → отображаем серии выбранного сезона: [5](#0-4) 

## Детальный пример из Rezka

Более сложный пример с поддержкой переводов показан в Rezka:

**1. Проверка на тип контента (фильм или сериал):** [6](#0-5) 

**2. Если сериал - собираем переводы:** [7](#0-6) 

**3. Создаём шаблоны для сезонов и серий:** [8](#0-7) 

**4. Логика рендеринга сезонов (s == -1):** [9](#0-8) 

**5. Логика рендеринга серий (s >= 1):** [10](#0-9) 

**6. Возврат результата:** [11](#0-10) 

## Агрегация нескольких источников

Для агрегации всех источников используется endpoint `/lite/events`: [12](#0-11) 

Каждый источник возвращает свою ссылку в формате:

```
{host}/lite/{provider}?title={title}&s=-1  // для сезонов
{host}/lite/{provider}?title={title}&s=1   // для серий 1 сезона
```

## Алгоритм реализации

Вот пошаговый план для вашего агрегатора:

```mermaid
graph TD
    A[Получить запрос с параметрами] --> B{Проверить s параметр}
    B -->|s == -1| C[Создать SeasonTpl]
    B -->|s >= 1| D[Создать EpisodeTpl]
    
    C --> E[Опросить все источники]
    E --> F[Для каждого источника собрать сезоны]
    F --> G{Есть переводы?}
    G -->|Да| H[Создать VoiceTpl]
    G -->|Нет| I[Добавить сезоны в SeasonTpl]
    H --> I
    I --> J[Вернуть SeasonTpl.ToJson или ToHtml]
    
    D --> K[Опросить все источники для выбранного сезона]
    K --> L[Собрать серии из всех источников]
    L --> M{Есть переводы?}
    M -->|Да| N[Добавить VoiceTpl для каждой озвучки]
    M -->|Нет| O[Добавить серии в EpisodeTpl]
    N --> O
    O --> P[Вернуть EpisodeTpl.ToJson или ToHtml]
```

## Практическая реализация

**Шаг 1:** Создайте метод контроллера с параметром `s`:

```csharp
public async Task<ActionResult> Index(string title, int s = -1, bool rjson = false)
```

**Шаг 2:** Разделите логику по значению `s`:

- `s == -1` → собирайте сезоны через `SeasonTpl.Append(название_сезона, ссылка, номер_сезона)`
- `s >= 1` → собирайте серии через `EpisodeTpl.Append(название_серии, title, сезон, серия, ссылка, "call")`

**Шаг 3:** Если есть несколько озвучек, используйте `VoiceTpl.Append(название_озвучки, активна, ссылка)`

**Шаг 4:** Возвращайте результат через `ToJson()` или `ToHtml()` в зависимости от параметра `rjson`

## Notes

Ключевые моменты:
- Параметр `s` управляет всей навигацией: `-1` = сезоны, `>= 1` = серии конкретного сезона
- Все шаблоны имеют методы `ToJson()` и `ToHtml()` для унифицированного вывода
- VoiceTpl можно передать в `ToJson(vtpl)` или `ToHtml(vtpl)` для включения в финальный результат
- Каждый источник возвращает свои ссылки, агрегация происходит на уровне списка источников в `/lite/events`
- Для агрегации достаточно собрать URL всех источников и вернуть их списком, а переключение между сезонами/сериями происходит через параметр `s` в URL

### Citations

**File:** Shared/Models/Templates/SeasonTpl.cs (L8-35)
```csharp
    public struct SeasonTpl
    {
        public List<(string name, string link, int? id)> data { get; set; }

        public string quality = null;

        public SeasonTpl() : this(10) { }

        public SeasonTpl(int capacity) { data = new List<(string, string, int?)>(capacity); }

        public SeasonTpl(string quality, int capacity = 10) 
        {
            data = new List<(string, string, int?)>(capacity);
            this.quality = quality; 
        }

        public void Append(string name, string link, string id)
        {
            int.TryParse(id, out int sid);
            Append(name, link, sid);
        }

        public void Append(string name, string link, int id)
        {
            if (!string.IsNullOrEmpty(name))
                data.Add((name, link, id));
        }

```

**File:** Shared/Models/Templates/VoiceTpl.cs (L7-19)
```csharp
    public struct VoiceTpl
    {
        public List<(string name, bool active, string link)> data { get; set; }

        public VoiceTpl() : this(15) { }

        public VoiceTpl(int capacity) { data = new List<(string, bool, string)>(capacity); }

        public void Append(string name, bool active, string link)
        {
            if (!string.IsNullOrEmpty(name))
                data.Add((name, active, link));
        }
```

**File:** Shared/Models/Templates/EpisodeTpl.cs (L9-24)
```csharp
    public struct EpisodeTpl
    {
        public List<(string name, string title, string s, string e, string link, string method, StreamQualityTpl? streamquality, SubtitleTpl? subtitles, string streamlink, string voice_name, VastConf vast, List<HeadersModel> headers, int? hls_manifest_timeout)> data { get; set; }

        public EpisodeTpl() : this(20) { }

        public EpisodeTpl(int capacity) 
        {
            data = new List<(string, string, string, string, string, string, StreamQualityTpl?, SubtitleTpl?, string, string, VastConf, List<HeadersModel>, int?)>(capacity);
        }

        public void Append(string name, string title, string s, string e, string link, string method = "play", in StreamQualityTpl? streamquality = null, in SubtitleTpl? subtitles = null, string streamlink = null, string voice_name = null, VastConf vast = null, List<HeadersModel> headers = null, int? hls_manifest_timeout = null)
        {
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(link))
                data.Add((name, $"{title} ({e} серия)", s, e, link, method, streamquality, subtitles, streamlink, voice_name, vast, headers, hls_manifest_timeout));
        }
```

**File:** Online/Controllers/Videoseed.cs (L94-105)
```csharp
                    if (s == -1)
                    {
                        var tpl = new SeasonTpl(cache.seasons.Count);

                        foreach (var season in cache.seasons)
                        {
                            string link = $"{host}/lite/videoseed?rjson={rjson}&kinopoisk_id={kinopoisk_id}&imdb_id={imdb_id}&title={enc_title}&original_title={enc_original_title}&s={season.Key}";
                            tpl.Append($"{season.Key} сезон", link, season.Key);
                        }

                        return ContentTo(rjson ? tpl.ToJson() : tpl.ToHtml());
                    }
```

**File:** Online/Controllers/Videoseed.cs (L106-121)
```csharp
                    else
                    {
                        string sArhc = s.ToString();
                        var videos = cache.seasons.First(i => i.Key == sArhc).Value["videos"].ToObject<Dictionary<string, JObject>>();

                        var etpl = new EpisodeTpl(videos.Count);

                        foreach (var video in videos)
                        {
                            string iframe = video.Value.Value<string>("iframe");
                            etpl.Append($"{video.Key} серия", title ?? original_title, sArhc, video.Key, accsArgs($"{host}/lite/videoseed/video/{AesTo.Encrypt(iframe)}"), "call", vast: init.vast);
                        }

                        return ContentTo(rjson ? etpl.ToJson() : etpl.ToHtml());
                    }
                    #endregion
```

**File:** Shared/Engine/Online/Rezka.cs (L271-272)
```csharp
            if (!result.content.Contains("data-season_id="))
            {
```

**File:** Shared/Engine/Online/Rezka.cs (L345-377)
```csharp
                #region Перевод
                var vtpl = new VoiceTpl();

                if (result.content.Contains("data-translator_id="))
                {
                    var match = new Regex("<[a-z]+ [^>]+ data-translator_id=\"(?<translator>[0-9]+)\"([^>]+)?>(?<name>[^<]+)(<img title=\"(?<imgname>[^\"]+)\" [^>]+/>)?").Match(result.content);
                    while (match.Success)
                    {
                        if (!userprem && match.Groups[0].Value.Contains("prem_translator"))
                        {
                            match = match.NextMatch();
                            continue;
                        }

                        string name = match.Groups["name"].Value.Trim();
                        if (!string.IsNullOrEmpty(match.Groups["imgname"].Value) && !name.ToLower().Contains(match.Groups["imgname"].Value.ToLower().Trim()))
                            name += $" ({match.Groups["imgname"].Value})";

                        string link = host + $"lite/rezka/serial?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&href={enc_href}&id={result.id}&t={match.Groups["translator"].Value}";

                        string voice_href = Regex.Match(match.Groups[0].Value, "href=\"(https?://[^/]+)?/([^\"]+)\"").Groups[2].Value;
                        if (!string.IsNullOrEmpty(voice_href) && init.ajax != null && init.ajax.Value == false)
                        {
                            string voice = HttpUtility.UrlEncode(voice_href);
                            link = host + $"lite/rezka?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&href={voice}&id={result.id}&t={match.Groups["translator"].Value}";
                        }

                        vtpl.Append(name, match.Groups["translator"].Value == trs, link);

                        match = match.NextMatch();
                    }
                }
                #endregion
```

**File:** Shared/Engine/Online/Rezka.cs (L379-381)
```csharp
                var tpl = new SeasonTpl();
                var etpl = new EpisodeTpl();
                HashSet<string> eshash = new HashSet<string>();
```

**File:** Shared/Engine/Online/Rezka.cs (L388-399)
```csharp
                    if (s == -1)
                    {
                        #region Сезоны
                        string sname = $"{m.Groups["season"].Value} сезон";
                        if (!string.IsNullOrEmpty(m.Groups["season"].Value) && !eshash.Contains(sname))
                        {
                            eshash.Add(sname);
                            string link = host + $"lite/rezka?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&href={enc_href}&t={trs}&s={m.Groups["season"].Value}";

                            tpl.Append(sname, link, m.Groups["season"].Value);
                        }
                        #endregion
```

**File:** Shared/Engine/Online/Rezka.cs (L401-423)
```csharp
                    else
                    {
                        #region Серии
                        if (m.Groups["season"].Value == s.ToString() && !eshash.Contains(m.Groups["name"].Value))
                        {
                            eshash.Add(m.Groups["name"].Value);
                            string link = host + $"lite/rezka/movie?title={enc_title}&original_title={enc_original_title}&id={result.id}&t={trs}&s={s}&e={m.Groups["episode"].Value}";

                            string voice_href = Regex.Match(m.Groups[0].Value, "href=\"(https?://[^/]+)?/([^\"]+)\"").Groups[2].Value;
                            if (!string.IsNullOrEmpty(voice_href))
                                link += $"&voice={HttpUtility.UrlEncode(voice_href)}";

                            string stream = null;
                            if (showstream)
                            {
                                stream = usehls ? $"{link.Replace("/movie", "/movie.m3u8")}&play=true" : $"{link}&play=true";
                                stream += args;
                            }

                            etpl.Append(m.Groups["name"].Value, title ?? original_title, sArhc, m.Groups["episode"].Value, link, "call", streamlink: stream);
                        }
                        #endregion
                    }
```

**File:** Shared/Engine/Online/Rezka.cs (L428-434)
```csharp
                if (rjson)
                    return s == -1 ? tpl.ToJson(vtpl) : etpl.ToJson(vtpl);

                if (s == -1)
                    return vtpl.ToHtml() + tpl.ToHtml();

                return vtpl.ToHtml() + etpl.ToHtml();
```

**File:** Online/OnlineApi.cs (L594-598)
```csharp
        [Route("lite/events")]
        async public ValueTask<ActionResult> Events(long id, string imdb_id, long kinopoisk_id, string title, string original_title, string original_language, int year, string source, string rchtype, int serial = -1, bool life = false, bool islite = false, string account_email = null, string uid = null, string token = null)
        {
            var online = new List<(dynamic init, string name, string url, string plugin, int index)>(50);
            bool isanime = original_language is "ja" or "zh";
```
