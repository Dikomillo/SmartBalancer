# Как работают источники с сериалами в Lampac

Я изучил кодовую базу Lampac и готов объяснить, как работают источники с сериалами. Это поможет вам реализовать агрегатор Smartfilter для сериалов и аниме.

## Структура навигации для сериалов

Все источники в Lampac используют **трехуровневую навигацию** для сериалов:

1. **Выбор озвучки/перевода** (если несколько доступно)
2. **Выбор сезона** (когда `s = -1`)
3. **Выбор серии** (когда `s >= 1`)

### Ключевые параметры

- **`s`** - номер сезона (`-1` = показать список сезонов, `>= 1` = конкретный сезон)
- **`e`** - номер серии
- **`t`** - ID озвучки/перевода
- **`rjson`** - возвращать JSON вместо HTML

## Основные Template классы

### 1. VoiceTpl - Выбор озвучки [1](#0-0) 

Этот класс создает список доступных переводов/озвучек. Метод `Append()` добавляет каждую озвучку с параметром `active` для текущей выбранной.

### 2. SeasonTpl - Выбор сезона [2](#0-1) 

Класс для отображения списка сезонов. Может содержать дополнительное поле `quality` для отображения максимального качества.

### 3. EpisodeTpl - Список серий [3](#0-2) 

Самый важный класс - содержит все данные о серии включая ссылки на стримы, субтитры, качество и т.д.

## Примеры реализации в источниках

### Пример 1: Rezka (полная реализация)

**Логика обработки сериала в движке:** [4](#0-3) 

Эта логика показывает:
- Когда `s == -1` → создается `SeasonTpl` для списка сезонов (строки 388-399)
- Когда `s >= 1` → создается `EpisodeTpl` для списка серий (строки 401-426)
- VoiceTpl создается для всех случаев (строки 346-377)
- Возврат: `vtpl.ToHtml() + tpl.ToHtml()` для сезонов или `vtpl.ToHtml() + etpl.ToHtml()` для серий

**Контроллер с отдельным методом Serial:** [5](#0-4) 

### Пример 2: HDVB (упрощенная структура) [6](#0-5) 

Здесь видна та же логика:
- `s == -1` → список сезонов через `SeasonTpl` (строки 63-84)
- `s >= 1` → сначала `VoiceTpl` для переводов (строки 87-101), потом `EpisodeTpl` для серий (строки 103-120)

### Пример 3: CDNmovies (структура с вложенными данными) [7](#0-6) 

## Методы вывода (HTML vs JSON)

Все Template классы поддерживают два формата вывода:

**HTML формат:** [8](#0-7) 

**JSON формат:** [9](#0-8) 

То же самое для EpisodeTpl: [10](#0-9) 

## Как агрегировать источники

Для вашего Smartfilter посмотрите, как работает агрегация в `OnlineApi.cs`: [11](#0-10) 

Метод `send()` проверяет все условия и добавляет источник в общий список.

Финальная агрегация происходит здесь: [12](#0-11) 

## Рекомендации для Smartfilter

Для реализации агрегатора сериалов в Smartfilter:

1. **Вызывайте каждый источник с нужными параметрами:**
   - Для получения сезонов: `/lite/{provider}?...&s=-1&rjson=true`
   - Для получения серий: `/lite/{provider}?...&s={season}&rjson=true`

2. **Парсите JSON ответы** - используйте параметр `rjson=true` для получения структурированных данных

3. **Объединяйте результаты:**
   - Для сезонов: объедините данные из `data` массива всех источников
   - Для серий: то же самое, можно добавить поле источника для каждой серии
   - Для озвучек: объедините массивы `voice` из разных источников

4. **Используйте те же Template классы** для вывода:
   ```
   var vtpl = new VoiceTpl();
   var tpl = new SeasonTpl();  // или EpisodeTpl для серий
   
   // добавляйте данные из всех источников
   foreach(var source in sources) {
       // парсите и добавляйте через Append()
   }
   
   return rjson ? tpl.ToJson(vtpl) : (vtpl.ToHtml() + tpl.ToHtml());
   ```

5. **Соблюдайте последовательность вызовов:**
   - Index/Search → Serial (с s=-1) → Serial (с s=X) → Movie (финальная ссылка)

## Notes

Ключевое отличие сериалов от фильмов - **многоуровневая навигация**. Вместо одного вызова как у фильмов, для сериалов нужно:
1. Получить список сезонов от каждого источника
2. Объединить их в один `SeasonTpl`
3. При выборе сезона - получить серии от всех источников для этого сезона
4. Объединить в один `EpisodeTpl`

Все источники следуют этой схеме, различаясь только деталями API и способами получения данных от провайдеров. Главное - понять, что `s=-1` всегда означает "показать сезоны", а `s>=1` означает "показать серии сезона X".

Для отладки рекомендую изучить ответы существующих источников с параметром `rjson=true` - это покажет точную структуру данных, которую нужно воспроизвести.

### Citations

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

**File:** Shared/Models/Templates/SeasonTpl.cs (L8-34)
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

**File:** Shared/Models/Templates/EpisodeTpl.cs (L9-24)
```csharp
    public struct EpisodeTpl
    {
        public List<(string name, string title, string s, string e, string link, string method, StreamQualityTpl? streamquality, SubtitleTpl? subtitles, string streamlink, string voice_name, VastConf vast, List<HeadersModel> headers, int? hls_manifest_timeout, SegmentTpl? segments, string subtitles_call)> data { get; set; }

        public EpisodeTpl() : this(20) { }

        public EpisodeTpl(int capacity) 
        {
            data = new List<(string, string, string, string, string, string, StreamQualityTpl?, SubtitleTpl?, string, string, VastConf, List<HeadersModel>, int?, SegmentTpl?, string)>(capacity);
        }

        public void Append(string name, string title, string s, string e, string link, string method = "play", in StreamQualityTpl? streamquality = null, in SubtitleTpl? subtitles = null, string streamlink = null, string voice_name = null, VastConf vast = null, List<HeadersModel> headers = null, int? hls_manifest_timeout = null, SegmentTpl? segments = null, string subtitles_call = null)
        {
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(link))
                data.Add((name, $"{title} ({e} серия)", s, e, link, method, streamquality, subtitles, streamlink, voice_name, vast, headers, hls_manifest_timeout, segments, subtitles_call));
        }
```

**File:** Shared/Models/Templates/EpisodeTpl.cs (L63-91)
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
                    quality = i.streamquality?.ToObject(emptyToNull: true),
                    subtitles = i.subtitles?.ToObject(emptyToNull: true),
                    i.subtitles_call,
                    s = int.TryParse(i.s, out int _s) ? _s : 0,
                    e = int.TryParse(i.e, out int _e) ? _e : 0,
                    details = i.voice_name,
                    i.name,
                    i.title,
                    i.hls_manifest_timeout,
                    vast = (i.vast ?? AppInit.conf.vast)?.url != null ? (i.vast ?? AppInit.conf.vast) : null,
                    i.segments
                })
            }, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        }
```

**File:** Shared/Engine/Online/Rezka.cs (L340-436)
```csharp
                #region Сериал
                string trs = new Regex("\\.initCDNSeriesEvents\\([0-9]+, ([0-9]+),").Match(result.content).Groups[1].Value;
                if (string.IsNullOrWhiteSpace(trs))
                    return string.Empty;

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

                var tpl = new SeasonTpl();
                var etpl = new EpisodeTpl();
                HashSet<string> eshash = new HashSet<string>();

                string sArhc = s.ToString();

                var m = Regex.Match(result.content, "data-cdn_url=\"(?<cdn>[^\"]+)\" [^>]+ data-season_id=\"(?<season>[0-9]+)\" data-episode_id=\"(?<episode>[0-9]+)\"([^>]+)?>(?<name>[^>]+)</[a-z]+>");
                while (m.Success)
                {
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
                    }
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

                    m = m.NextMatch();
                }

                if (rjson)
                    return s == -1 ? tpl.ToJson(vtpl) : etpl.ToJson(vtpl);

                if (s == -1)
                    return vtpl.ToHtml() + tpl.ToHtml();

                return vtpl.ToHtml() + etpl.ToHtml();
                #endregion
            }
```

**File:** Online/Controllers/Rezka.cs (L165-194)
```csharp
        #region Serial
        [HttpGet]
        [Route("lite/rezka/serial")]
        async public ValueTask<ActionResult> Serial(string title, string original_title, string href, long id, int t, int s = -1, bool rjson = false)
        {
            var init = await Initialization();
            if (await IsBadInitialization(init, rch: true))
                return badInitMsg;

            if (string.IsNullOrEmpty(href))
                return OnError();

            var oninvk = await InitRezkaInvoke(init);
            var proxyManager = new ProxyManager(init);

            var rch = new RchClient(HttpContext, host, init, requestInfo, keepalive: -1);
            if (rch.IsNotConnected())
                return ContentTo(rch.connectionMsg);

            Episodes root = await InvokeCache($"rezka:view:serial:{id}:{t}", cacheTime(20, init: init), () => oninvk.SerialEmbed(id, t));
            if (root == null)
                return OnError(null, gbcache: !rch.enable);

            var content = await InvokeCache($"rezka:{href}", cacheTime(20, init: init), () => oninvk.Embed(href, null));
            if (content == null)
                return OnError(null, gbcache: !rch.enable);

            return ContentTo(oninvk.Serial(root, content, accsArgs(string.Empty), title, original_title, href, id, t, s, true, rjson));
        }
        #endregion
```

**File:** Online/Controllers/HDVB.cs (L62-123)
```csharp
                #region Сериал
                if (s == -1)
                {
                    var tpl = new SeasonTpl();
                    var tmp_season = new HashSet<string>();

                    foreach (var voice in data)
                    {
                        foreach (var season in voice.Value<JArray>("serial_episodes"))
                        {
                            string season_name = $"{season.Value<int>("season_number")} сезон";
                            if (tmp_season.Contains(season_name))
                                continue;

                            tmp_season.Add(season_name);

                            string link = $"{host}/lite/hdvb?rjson={rjson}&serial=1&kinopoisk_id={kinopoisk_id}&title={HttpUtility.UrlEncode(title)}&original_title={HttpUtility.UrlEncode(original_title)}&s={season.Value<int>("season_number")}";
                            tpl.Append(season_name, link, season.Value<int>("season_number"));
                        }
                    }

                    return ContentTo(rjson ? tpl.ToJson() : tpl.ToHtml());
                }
                else
                {
                    #region Перевод
                    var vtpl = new VoiceTpl();

                    for (int i = 0; i < data.Count; i++)
                    {
                        if (data[i].Value<JArray>("serial_episodes").FirstOrDefault(i => i.Value<int>("season_number") == s) == null)
                            continue;

                        if (t == -1)
                            t = i;

                        string link = $"{host}/lite/hdvb?rjson={rjson}&serial=1&kinopoisk_id={kinopoisk_id}&title={HttpUtility.UrlEncode(title)}&original_title={HttpUtility.UrlEncode(original_title)}&s={s}&t={i}";
                        vtpl.Append(data[i].Value<string>("translator"), t == i, link);
                    }
                    #endregion

                    var etpl = new EpisodeTpl();
                    string iframe = HttpUtility.UrlEncode(data[t].Value<string>("iframe_url"));
                    string translator = HttpUtility.UrlEncode(data[t].Value<string>("translator"));

                    string sArhc = s.ToString();

                    foreach (int episode in data[t].Value<JArray>("serial_episodes").FirstOrDefault(i => i.Value<int>("season_number") == s).Value<JArray>("episodes").ToObject<List<int>>())
                    {
                        string link = $"{host}/lite/hdvb/serial?title={HttpUtility.UrlEncode(title)}&original_title={HttpUtility.UrlEncode(original_title)}&iframe={iframe}&t={translator}&s={s}&e={episode}";
                        string streamlink = accsArgs($"{link.Replace("/serial", "/serial.m3u8")}&play=true");

                        etpl.Append($"{episode} серия", title ?? original_title, sArhc, episode.ToString(), link, "call", streamlink: streamlink);
                    }

                    if (rjson)
                        return ContentTo(etpl.ToJson(vtpl));

                    return ContentTo(vtpl.ToHtml() + etpl.ToHtml());
                }
                #endregion
            }
```

**File:** Shared/Engine/Online/CDNmovies.cs (L59-124)
```csharp
        public string Html(Voice[] voices, long kinopoisk_id, string title, string original_title, int t, int s, int sid, VastConf vast = null, bool rjson = false)
        {
            if (voices == null || voices.Length == 0)
                return string.Empty;

            string enc_title = HttpUtility.UrlEncode(title);
            string enc_original_title = HttpUtility.UrlEncode(original_title);

            #region Перевод html
            var vtpl = new VoiceTpl(voices.Length);

            for (int i = 0; i < voices.Length; i++)
            {
                string link = host + $"lite/cdnmovies?rjson={rjson}&kinopoisk_id={kinopoisk_id}&title={enc_title}&original_title={enc_original_title}&t={i}";
                vtpl.Append(voices[i].title, t == i, link);
            }
            #endregion

            if (s == -1)
            {
                #region Сезоны
                var tpl = new SeasonTpl(voices[t].folder.Length);

                for (int i = 0; i < voices[t].folder.Length; i++)
                {
                    string season = Regex.Match(voices[t].folder[i].title, "([0-9]+)$").Groups[1].Value;
                    if (string.IsNullOrEmpty(season))
                        continue;

                    string link = host + $"lite/cdnmovies?rjson={rjson}&kinopoisk_id={kinopoisk_id}&title={enc_title}&original_title={enc_original_title}&t={t}&s={season}&sid={i}";
                    tpl.Append($"{season} сезон", link, season);
                }

                return rjson ? tpl.ToJson(vtpl) : (vtpl.ToHtml() + tpl.ToHtml());
                #endregion
            }
            else
            {
                #region Серии
                var etpl = new EpisodeTpl();
                string sArhc = s.ToString();

                foreach (var item in voices[t].folder[sid].folder)
                {
                    var streamquality = new StreamQualityTpl();

                    foreach (Match m in Regex.Matches(item.file, "\\[(360|240)p?\\]([^\\[\\|,\n\r\t ]+\\.(mp4|m3u8))"))
                    {
                        string link = m.Groups[2].Value;
                        if (string.IsNullOrEmpty(link))
                            continue;

                        streamquality.Insert(onstreamfile.Invoke(link), $"{m.Groups[1].Value}p");
                    }

                    string episode = Regex.Match(item.title, "([0-9]+)$").Groups[1].Value;
                    etpl.Append($"{episode} cерия", title ?? original_title, sArhc, episode, streamquality.Firts().link, streamquality: streamquality, vast: vast);
                }

                if (rjson)
                    return etpl.ToJson(vtpl);

                return vtpl.ToHtml() + etpl.ToHtml();
                #endregion
            }
        }
```

**File:** Online/OnlineApi.cs (L705-784)
```csharp
            #region send
            void send(BaseSettings _init, string plugin = null, string name = null, string arg_title = null, string arg_url = null, string rch_access = null, BaseSettings myinit = null)
            {
                var init = myinit != null ? _init : loadKit(_init, kitconf);
                bool enable = init.enable && !init.rip;
                if (!enable)
                    return;

                if (init.rhub && !init.rhub_fallback)
                {
                    if (rch_access != null && rchtype != null) 
                    {
                        enable = rch_access.Contains(rchtype);
                        if (enable && init.rhub_geo_disable != null)
                        {
                            if (requestInfo.Country != null && init.rhub_geo_disable.Contains(requestInfo.Country))
                                enable = false;
                        }
                    }
                }

                if (enable && init.client_type != null && rchtype != null)
                    enable = init.client_type.Contains(rchtype);

                if (init.geo_hide != null)
                {
                    if (requestInfo.Country != null && init.geo_hide.Contains(requestInfo.Country))
                        enable = false;
                }

                if (enable)
                {
                    if (init.group_hide)
                    {
                        if (init.group > 0)
                        {
                            if (user == null || init.group > user.group)
                                return;
                        }
                        else if (AppInit.conf.accsdb.enable)
                        {
                            if (user == null && string.IsNullOrEmpty(AppInit.conf.accsdb.premium_pattern))
                                return;
                        }
                    }

                    string url = string.Empty;

                    if (string.IsNullOrEmpty(init.overridepasswd))
                    {
                        url = init.overridehost;
                        if (string.IsNullOrEmpty(url) && init.overridehosts != null && init.overridehosts.Length > 0)
                            url = init.overridehosts[Random.Shared.Next(0, init.overridehosts.Length)];
                    }

                    string displayname = init.displayname ?? name ?? init.plugin;

                    if (!string.IsNullOrEmpty(url))
                    {
                        if (plugin == "collaps-dash")
                        {
                            displayname = displayname.Replace("- 720p", "- 1080p");
                            url = url.Replace("/collaps", "/collaps-dash");
                        }
                    }
                    else {
                        url = "{localhost}/lite/" + (plugin ?? (init.plugin ?? name).ToLower()) + arg_url;
                    }

                    if (original_language != null && original_language.Split("|")[0] is "ru" or "ja" or "ko" or "zh" or "cn")
                    {
                        string _p = (plugin ?? (init.plugin ?? name).ToLower());
                        if (_p is "filmix" or "filmixtv" or "fxapi" or "kinoukr" or "rezka" or "rhsprem" or "redheadsound" or "kinopub" or "alloha" or "lumex" or "vcdn" or "videocdn" or "fancdn" or "redheadsound" or "kinotochka" or "remux") // || (_p == "kodik" && kinopoisk_id == 0 && string.IsNullOrEmpty(imdb_id))
                            url += (url.Contains("?") ? "&" : "?") + "clarification=1";
                    }

                    online.Add((myinit, $"{displayname}{arg_title}", url, (plugin ?? init.plugin ?? name).ToLower(), init.displayindex > 0 ? init.displayindex : online.Count));
                }
            }
            #endregion
```

**File:** Online/OnlineApi.cs (L1108-1109)
```csharp
            string online_result = string.Join(",", online.OrderBy(i => i.index).Select(i => "{\"name\":\"" + i.name + "\",\"url\":\"" + i.url + "\",\"balanser\":\"" + i.plugin + "\"}"));
            return ContentTo($"[{online_result.Replace("{localhost}", host)}]");
```
# Как устроено кеширование в Lampac

Система кеширования в Lampac построена на двух уровнях: **HybridCache** (память + диск) и **MemoryCache** (только память), с использованием семафоров для предотвращения дублирующих запросов. [1](#1-0) 

## Основной метод InvokeCache

Метод `InvokeCache<T>()` является центральным компонентом кеширования [2](#1-1) :

1. **Семафор для дедупликации** - использует `_semaphoreLocks` для предотвращения параллельных запросов с одинаковым ключом [3](#1-2) 
2. **Проверка кеша** - сначала проверяет `hybridCache.TryGetValue()`, если данные есть - возвращает их с заголовком `X-Invoke-Cache: HIT` [4](#1-3) 
3. **Выполнение запроса** - если данных нет, выполняет `onget.Invoke()` и сохраняет результат [5](#1-4) 
4. **Освобождение семафора** - в блоке `finally` освобождает семафор и удаляет его из словаря, если больше нет ожидающих [6](#1-5) 

## Структура ключей кеша

Каждый провайдер использует свою схему ключей:

**Примеры из контроллеров:**
- Rezka: `rezka:view:serial:{id}:{t}` для сериалов [7](#1-6) 
- Rezka: `rezka:movie:{voice}:{realip}:{cookie}` для фильмов [8](#1-7) 
- VideoDB: `videodb:view:{kinopoisk_id}` для контента<cite />
- VDBmovies: `vdbmovies:{orid}:{kinopoisk_id}` с учетом IP через `rch.ipkey()` [9](#1-8) 

## Время жизни кеша

Метод `cacheTime()` определяет длительность кеширования с учетом различных факторов<cite />:

**Типичные значения:**
- Поиск: 40 минут [10](#1-9) 
- Контент сериалов: 20 минут [7](#1-6) 
- Видео ссылки: 10-20 минут<cite />
- Ошибки: 20 секунд (только в multiaccess режиме)<cite />

## Интеграция с RCH

При использовании Remote Client Hub ключи кеша включают IP удаленного клиента через метод `rch.ipkey()` [9](#1-8) . Это важно, так как разные удаленные клиенты могут иметь разный доступ к контенту.

**Пример с RCH:** [11](#1-10) 

## Автоматическая очистка кеша

Система `CacheCron` периодически очищает устаревшие файлы кеша [12](#1-11) :

- Запускается каждые 4 минуты [13](#1-12) 
- Очищает различные типы кеша: tmdb, cub, img, torrent, html, hls [14](#1-13) 
- Удаляет файлы старше указанного времени [15](#1-14) 

## Кеширование ошибок

В режиме `multiaccess` ошибки кешируются на 20 секунд для предотвращения перегрузки<cite />. Проверка происходит через `IsCacheError()` в методе `IsBadInitialization()`<cite />.

## Notes

Система кеширования в Lampac оптимизирована для работы с множественными источниками контента. Ключевые особенности:
- Семафоры предотвращают дублирующие запросы к одному и тому же контенту
- HybridCache позволяет использовать как память, так и диск
- RCH-aware кеширование учитывает IP удаленных клиентов
- Автоматическая очистка предотвращает переполнение диска

Для вашего Smartfilter агрегатора важно учитывать, что каждый источник уже кеширует свои данные, поэтому вы можете просто вызывать их API и полагаться на их кеш, либо добавить свой слой кеширования для агрегированных результатов.

Wiki pages you might want to explore:
- [Content Provider Architecture (immisterio/Lampac)](/wiki/immisterio/Lampac#6.1)
- [Remote Client Hub (RCH) System (immisterio/Lampac)](/wiki/immisterio/Lampac#8.3)

### Citations

**File:** Shared/BaseController.cs (L324-373)
```csharp
        #region InvokeCache
        public ValueTask<CacheResult<T>> InvokeCache<T>(string key, TimeSpan time, Func<CacheResult<T>, ValueTask<dynamic>> onget) => InvokeCache(key, time, null, onget);

        async public ValueTask<CacheResult<T>> InvokeCache<T>(string key, TimeSpan time, ProxyManager? proxyManager, Func<CacheResult<T>, ValueTask<dynamic>> onget, bool? memory = null)
        {
            var semaphore = _semaphoreLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

            try
            {
                await semaphore.WaitAsync(TimeSpan.FromSeconds(40));

                if (hybridCache.TryGetValue(key, out T _val, memory))
                {
                    HttpContext.Response.Headers.TryAdd("X-Invoke-Cache", "HIT");
                    return new CacheResult<T>() { IsSuccess = true, Value = _val };
                }

                HttpContext.Response.Headers.TryAdd("X-Invoke-Cache", "MISS");

                var val = await onget.Invoke(new CacheResult<T>());

                if (val == null)
                    return new CacheResult<T>() { IsSuccess = false, ErrorMsg = "null" };

                if (val.GetType() == typeof(CacheResult<T>))
                    return (CacheResult<T>)val;

                if (val.Equals(default(T)))
                    return new CacheResult<T>() { IsSuccess = false, ErrorMsg = "default" };

                if (typeof(T) == typeof(string) && string.IsNullOrEmpty(val.ToString()))
                    return new CacheResult<T>() { IsSuccess = false, ErrorMsg = "empty" };

                proxyManager?.Success();
                hybridCache.Set(key, val, time, memory);
                return new CacheResult<T>() { IsSuccess = true, Value = val };
            }
            finally
            {
                try
                {
                    semaphore.Release();
                }
                finally
                {
                    if (semaphore.CurrentCount == 1)
                        _semaphoreLocks.TryRemove(key, out _);
                }
            }
        }
```

**File:** Online/Controllers/Rezka.cs (L184-184)
```csharp
            Episodes root = await InvokeCache($"rezka:view:serial:{id}:{t}", cacheTime(20, init: init), () => oninvk.SerialEmbed(id, t));
```

**File:** Online/Controllers/Rezka.cs (L223-227)
```csharp
                md = await InvokeCache(rch.ipkey($"rezka:movie:{voice}:{realip}:{init.cookie}", proxyManager), cacheTime(20, mikrotik: 1, init: init), () => oninvk.Movie(voice), proxyManager);
            }

            if (md == null && init.ajax != null)
                md = await InvokeCache(rch.ipkey($"rezka:view:get_cdn_series:{id}:{t}:{director}:{s}:{e}:{realip}:{init.cookie}", proxyManager), cacheTime(20, mikrotik: 1, init: init), () => oninvk.Movie(id, t, director, s, e, favs), proxyManager);
```

**File:** Online/Controllers/VDBmovies.cs (L117-117)
```csharp
            var cache = await InvokeCache<EmbedModel>(rch.ipkey($"vdbmovies:{orid}:{kinopoisk_id}", proxyManager), cacheTime(20, rhub: 2, init: init), proxyManager, async res =>
```

**File:** Online/Controllers/Mirage.cs (L445-445)
```csharp
            var cache = await InvokeCache<JArray>($"mirage:search:{title}", cacheTime(40, init: init), proxyManager, async res =>
```

**File:** Lampac/Engine/CRON/CacheCron.cs (L12-60)
```csharp
        public static void Run()
        {
            _cronTimer = new Timer(cron, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4));
        }

        static Timer _cronTimer;

        static bool _cronWork = false;

        static void cron(object state)
        {
            if (_cronWork)
                return;

            _cronWork = true;

            try
            {
                var files = new Dictionary<string, FileInfo>();
                long freeDiskSpace = getFreeDiskSpace();

                foreach (var conf in new List<(string path, int minute)> {
                    ("tmdb", AppInit.conf.tmdb.cache_img),
                    ("cub", AppInit.conf.cub.cache_img),
                    ("img", AppInit.conf.serverproxy.image.cache_time),
                    ("torrent", AppInit.conf.fileCacheInactive.torrent),
                    ("html", AppInit.conf.fileCacheInactive.html),
                    ("hls", AppInit.conf.fileCacheInactive.hls),
                    ("storage/temp", 10)
                })
                {
                    try
                    {
                        if (conf.minute == -1 || !Directory.Exists(Path.Combine("cache", conf.path)))
                            continue;

                        foreach (string infile in Directory.EnumerateFiles(Path.Combine("cache", conf.path), "*", SearchOption.AllDirectories))
                        {
                            try
                            {
                                if (conf.minute == 0)
                                    File.Delete(infile);
                                else
                                {
                                    var fileinfo = new FileInfo(infile);
                                    if (DateTime.Now > fileinfo.LastWriteTime.AddMinutes(conf.minute))
                                        fileinfo.Delete();
                                    else if (1073741824 > freeDiskSpace) // 1Gb
                                        files.TryAdd(infile, fileinfo);
```

Как устроены плагины и как их подключают

Lampac — это сервер, который раздаёт клиенту Lampa/Lampa Lite JS-плагины по URL (пример: /on.js, /online.js, /dlna.js, /sync.js, /ts.js). Твой плагин — это просто .js, который Lampac отдаёт с подстановками (host, token и т.п.).

Подключение в Lampa: в настройках клиента Lampa добавляешь URL плагина (например, http(s)://<твойдомен>/myplugin.js) в список расширений → перезапуск виджета.

Динамические замены: Lampac при отдаче файла может заменить плейсхолдеры ({localhost}, {token}, и т.п.) и «патчить» код (appReplace). Это же можно использовать, чтобы плавно вносить правки без ломания.

Структура плагина (.js)

Минимум — самодостаточный ES5-скрипт, обёрнутый в IIFE, который:

ничего не «затирает» в глобале;

ждёт, пока Lampa прогрузится;

добавляет UI (кнопку/пункт меню) и вешает обработчики через делегирование.

// myplugin.js — ES5, без const/let/arrow, без топ-левела.
(function (window) {
  'use strict';

  // 1) Защита от повторной инициализации
  if (window.__MY_SMARTFILTER_INIT__) return;
  window.__MY_SMARTFILTER_INIT__ = true;

  // 2) Поддержка старых сред (очень короткие полифилы)
  if (!Object.assign) {
    Object.assign = function (t) {
      for (var i = 1; i < arguments.length; i++) {
        var s = arguments[i] || {};
        for (var k in s) if (Object.prototype.hasOwnProperty.call(s, k)) t[k] = s[k];
      }
      return t;
    };
  }
  if (!Array.prototype.includes) {
    Array.prototype.includes = function (x) {
      return this.indexOf(x) !== -1;
    };
  }
  // Promise/fetch лучше грузить отдельным polyfill’ом (см. раздел ниже)

  // 3) Утилита безопасного добавления кнопки на стартовый экран
  function addStartButton() {
    var root = document.querySelector('.full-start__buttons');
    if (!root) return;

    // не дублируем
    if (root.querySelector('.my-smartfilter-btn')) return;

    var btn = document.createElement('div');
    btn.className = 'full-start__button selector my-smartfilter-btn';
    btn.setAttribute('tabindex', '0');
    btn.setAttribute('data-subtitle', 'Smartfilter');
    btn.innerHTML =
      '<svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true"><path d="M3 5h18v2H3zm4 6h10v2H7zm3 6h4v2h-4z"/></svg>' +
      '<div class="full-start__button-name">Smartfilter</div>';

    root.appendChild(btn);
  }

  // 4) Делегирование событий (работает и без jQuery)
  document.addEventListener('click', function (e) {
    var el = e.target.closest ? e.target.closest('.my-smartfilter-btn') : null;
    if (!el) return;
    e.preventDefault();

    // Открываем твой Smartfilter в Lampa
    // Либо вызываем глобальный метод, если есть, либо переходим на internal route
    if (window.Lampa && Lampa.Activity && Lampa.Activity.push) {
      Lampa.Activity.push({
        url: '{localhost}/lite/smartfilter', // Lampac подставит реальный хост
        title: 'Smartfilter',
        component: 'iframe', // или свой компонент, если есть
        pass: {}
      });
    } else {
      // Фолбэк — открыть отдельным окном/iframe
      window.open('{localhost}/lite/smartfilter', '_self');
    }
  }, false);

  // 5) Ждём DOM и появление контейнера (старые TV могут отрисовывать позже)
  function initWhenReady() {
    if (document.readyState === 'loading') {
      document.addEventListener('DOMContentLoaded', addStartButton);
    } else {
      addStartButton();
    }
    // перестраховка от позднего появления DOM-узлов
    var mo = new MutationObserver(function () { addStartButton(); });
    mo.observe(document.documentElement, { childList: true, subtree: true });
  }

  initWhenReady();
})(window);


Если хочешь jQuery-стиль — не инлайнь другую версию jQuery; используй то, что уже есть у клиента (если есть), и не включай его повторно, чтобы не конфликтовать. На случай коллизий — jQuery.noConflict().

Добавить свою кнопку/пункт меню

Есть 2 пути:

DOM-инъекция (как в примере выше)
— простая и совместимая: ищешь контейнер (.full-start__buttons) и добавляешь туда свой <div class="selector">.... Класс selector нужен для навигации пультом/клавишами. Обработчики — только делегированием (через document) и без тяжёлых мутаций.

Через API клиента Lampa (если доступно)
В ряде сборок есть глобалы вроде Lampa.Component.add(...) или Lampa.Listener.follow('app', ...). Если они доступны — регистрируй компонентом, но оставайся совместимым: держи DOM-фолбэк как выше.

Как раздавать плагин с Lampac

Положи файл, например, plugins/myplugin.js в репозиторий Lampac.

Добавь endpoint, по аналогии с существующими (/online.js и т.п.):

// Lampac/Controllers/... (тот же контроллер, где on.js/online.js)
[HttpGet]
[Route("myplugin.js")]
public ActionResult MyPlugin()
{
    // читаем шаблон, делаем подстановки
    var js = FileCache.ReadAllText("plugins/myplugin.js")
        .Replace("{localhost}", host) // host возьми так же, как в других методах
        // .Replace("{token}", myToken) // если нужно
        ;

    // Выдай корректный content-type и кэшируй разумно
    Response.Headers["Content-Type"] = "application/javascript; charset=utf-8";
    Response.Headers["Cache-Control"] = "public, max-age=300"; // 5 минут
    return Content(js, "application/javascript; charset=utf-8");
}


В Lampa в настройках расширений укажи https://<host>/myplugin.js.

Безопасные практики, чтобы «ничего не ломать»

ES5-код: старые Tizen/WebOS/Silk/Opera TV могут не понимать ES6. Пиши на ES5: var, function, без =>, без class, без for..of.

Не переопределять глобалы, не менять прототипы нативных объектов.

Namespace CSS: если добавляешь стили — используй уникальные префиксы классов (.my-smartfilter-*), старайся не трогать существующие стили.

Делегирование событий: не вешай обработчики на каждую кнопку — только один на документ, проверяй closest('.my-smartfilter-btn').

Проверяй наличие контейнеров (DOM может появляться поздно) и используй MutationObserver для безопасного «приклеивания» UI, как в примере.

Не блокируй поток: никаких тяжёлых синхронных XHR/долгих циклов; сетевые вызовы — через fetch/XHR с таймаутами; UI — через мелкие таски.

CSP/заголовки: отдавай плагин с Content-Type: application/javascript, избегай eval и инлайновых скриптов, если у клиента строгий CSP.

Кэширование: Lampac может кэшировать отданный .js в памяти/диске; на клиенте не полагайся на вечное кэширование — учитывай обновления (версионируй URL, например /myplugin.js?v=2).

jQuery и совместимость со старыми браузерами/TV

jQuery можно использовать, если он уже есть у клиента. Если тебе нужно гарантированно — проверь наличие:

var $ = window.jQuery || window.$; // если нет — работай без него


Если всё-таки инжектируешь свою копию (не рекомендуется) — обязательно:

var jq = jQuery.noConflict(true);
// и работай только через jq, чтобы не задеть глобальный $


ES5-совместимость: придерживайся ES5. Для API, которых нет на старых платформах, подгружай полифилы:

Promise (es6-promise),

fetch (whatwg-fetch или xhr-фолбэк),

URL, URLSearchParams,

Object.assign, Array.prototype.includes.

События/делегирование: jQuery-паттерны $(document).on('click', selector, handler) на ТВ работают устойчиво. Если без jQuery — document.addEventListener('click', fn) + e.target.closest(selector).

Lazy-load и кэш

Скрипт плагина — один небольшой файл. Всё тяжёлое (данные, картинки) — грузим лениво по действию пользователя (по клику на кнопку).

На стороне Lampac включай кэширование рендера (HybridCache + файловые кеши) — это ускорит твой UI без усложнений на клиенте.

Мини-пример «jQuery-версия» (если jQuery есть)
(function (window, jQuery) {
  'use strict';
  if (!jQuery) return; // тихо выходим, если нет jQuery
  if (window.__MY_SMARTFILTER_INIT__) return;
  window.__MY_SMARTFILTER_INIT__ = true;

  jQuery(function ($) {
    function addBtn() {
      var $root = $('.full-start__buttons');
      if (!$root.length || $root.find('.my-smartfilter-btn').length) return;
      $('<div class="full-start__button selector my-smartfilter-btn" tabindex="0" data-subtitle="Smartfilter">' +
          '<svg viewBox="0 0 24 24" width="24" height="24"><path d="M3 5h18v2H3zm4 6h10v2H7zm3 6h4v2h-4z"/></svg>' +
          '<div class="full-start__button-name">Smartfilter</div>' +
        '</div>').appendTo($root);
    }

    $(document).on('click', '.my-smartfilter-btn', function (e) {
      e.preventDefault();
      if (window.Lampa && Lampa.Activity && Lampa.Activity.push) {
        Lampa.Activity.push({
          url: '{localhost}/lite/smartfilter',
          title: 'Smartfilter',
          component: 'iframe',
          pass: {}
        });
      } else {
        window.open('{localhost}/lite/smartfilter', '_self');
      }
    });

    addBtn();
    // На случай поздней отрисовки
    var mo = new MutationObserver(addBtn);
    mo.observe(document.documentElement, { childList: true, subtree: true });
  });
})(window, window.jQuery || window.$);

Быстрый чек-лист «чтобы не ломать»

 ES5 only (никакого =>, const, class).

 Без глобальных перезаписей, имена классов/переменных — с префиксом my-.

 Делегирование событий через документ.

 Полифилы подгружать только при отсутствии фич (feature-detect).

 Не вставлять вторую копию jQuery; при необходимости — noConflict.

 DOM-инъекция в существующие контейнеры + MutationObserver.

 Лёгкий JS-файл, данные — лениво после клика.

 Отдавать с корректными заголовками и умеренным Cache-Control.

 ---

 