
Этап 1 — Анализ (DeepWiki)

Что подтвердил через DeepWiki:

Шаблоны расположены в Shared/Models/Templates: MovieTpl, SeasonTpl, EpisodeTpl, VoiceTpl. Они умеют Append(...), ToJson(...), ToHtml(...) и именно так формируют правильный ответ для Lampa (это и нужно тебе).

В типовых контроллерах (например, Alloha, VeoVeo, GetsTV) возвращается rjson ? tpl.ToJson() : tpl.ToHtml(); для сериалов: SeasonTpl при s=-1, EpisodeTpl для эпизодов, VoiceTpl для озвучек.

/lite/events в Lampac — стандартный «реестр» активных провайдеров, его используют для параллельного обхода источников. (В ядре возвращается словарь name→uri; в некоторых кастомных сборках — массив объектов.)

В твоём модуле (по предоставленным файлам):

SmartFilterController — HTTP-эндпоинт /lite/smartfilter, возвращает progress JSON пока идёт агрегация, а затем финальные данные.

SmartFilterEngine — собирает список провайдеров по /lite/events, параллельно опрашивает, парсит ответы (MovieTpl/SeasonTpl/EpisodeTpl/VoiceTpl) и агрегирует.

SmartFilterProgress — хранит прогресс в IMemoryCache по ключу smartfilter:{query}:progress, выставляет ready=true через PublishFinal(...).

SmartFilter.conf — конфиг модуля (таймауты, число параллельных запросов, ретраи, whitelist/blacklist провайдеров).

OnlineApi модуля — добавляет в список провайдеров пункт «SmartFilter Aggregator» с URL .../lite/smartfilter.

Этап 2 — Подтверждение понимания
Короткое резюме структуры

Клиент дергает: /lite/smartfilter?title=...&year=...&serial=...&rjson=true.

Контроллер стартует агрегацию (кэшируется по cacheKey = smartfilter:{sorted_query}), сразу возвращает снимок прогресса {status:"pending", ...} и ключ прогресса.

Движок:

тянет список провайдеров через /lite/events (ядро Lampac);

параллельно ходит к каждому .../lite/{provider} с теми же query + rjson=true;

аккумулирует payload (JSON из Tpl.ToJson()), помечает прогресс;

публикует финальный PublishFinal(...) → ready=true, собирает сводный data + type.

Повторные запросы клиента по rjson=true начинают получать уже готовый { type, data[] } (без pending).

Extension points / интерфейсы

Вход: SmartFilterController.Index(...).

Список источников: SmartFilterEngine.GetActiveProvidersAsync(...) (обращается к /lite/events).

Исполнение провайдера: SmartFilterEngine.FetchProviderTemplateAsync(...).

Прогресс: SmartFilterProgress.*.

Конфиг: ModInit.conf.

Почему у тебя «вечный pending»

GetActiveProvidersAsync жёстко парсит /lite/events как JArray, а у ядра Lampac — часто JObject-словарь name -> url. В этом случае парсинг падает или возвращается пустой список провайдеров — прогресс не инициализируется корректно и первый ответ остаётся «pending / total:0».

Контроллер отдаёт статусы сразу, не дожидаясь даже инициализации прогресса, поэтому при первом хите ты видишь providers: [], ready: false.

Если events отдал пусто/ошибку — движок ускоренно делает PublishFinal(...) с пустым составом; но из-за гонки на первой выдаче пользователь успевает увидеть нулевой снимок и может не перепросить.

Этап 2.5 — Верификация API (Context7)

Newtonsoft.Json закрепил как /jamesnk/newtonsoft.json и освежил docs по JObject/JArray/JToken и JsonConvert.SerializeObject/DeserializeObject — тобой это активно используется в движке и контроллере при сборке JSON-ответов и разборе провайдеров. Всё ок, используешь корректно.

Этап 3 — Кодинг (что конкретно исправить)

Ниже минимальные правки, чтобы:

уйти от «вечного pending»;

корректно распознавать /lite/events как JObject или JArray;

возвращать готовый результат сразу, если агрегация завершилась мгновенно или провайдеров нет;

гарантировать валидный формат (для фильмов как в твоём примере; для сериалов — через SeasonTpl/EpisodeTpl/VoiceTpl из провайдеров).

3.1 SmartFilterEngine.GetActiveProvidersAsync — поддержать оба формата /lite/events

Проблема: сейчас ты делаешь JArray.Parse(response) и ждёшь объекты с полями name/url/balanser/index.
Исправление: парсить корень как JToken, затем:

если JObject → пройтись по Properties():

name = prop.Name

value:

если string → это url

если object → попытаться взять url/balanser/index

если JArray → как сейчас.

Также неплохо пробить fallback на ключи uri вместо url, если попадается.

Это устранит пустой набор провайдеров и «ноль total».

3.2 SmartFilterController — «мягкое ожидание» и быстрый финал

Чтобы не отдавать пустой pending на самом первом запросе:

Если rjson=true и aggregationTask ещё не завершён, попробуй краткий await «первого такта» (например, await Task.WhenAny(aggregationTask, Task.Delay(200))).

Если за эти ~200 мс движок уже инициализировал прогресс (или успел понять, что провайдеров нет), ты отдашь уже не пустой снимок или даже финал.

Если после краткого ожидания aggregationTask завершился — отдай финал сразу.

Если нет — отдай BuildProgressResponse(...), но УБЕДИСЬ, что в нём есть total > 0 (потому что Initialize(...) уже вызван до старта обхода), а не «нулевой» снимок.

3.3 SmartFilterEngine.TryParseProviderResponse — толерантность к провайдерам

Провайдеры Lampac отдают корректный JSON в Tpl.ToJson(). Здесь убедись, что:

принимаешь и type: "movie"|"season"|"episode"|"similar", и наличие voice/maxquality/data (не падай, если чего-то нет);

itemsCount считаешь по data.Length только когда data — массив;

HTML-ответы (если rjson=false у провайдера по багу) игнорируешь (items=0, status empty) — но не валишь весь прогон.

3.4 SmartFilterEngine — «одноисточниковый passthrough»

В конфиге у тебя уже есть "preferSingleProviderPassthrough": true. В движке это учтено — если контент пришёл от единственного провайдера (и тип «movie/season/episode/similar»), возвращай его напрямую (без смешивания). Оставь так — это правильно.

3.5 Исключения/ретраи

У тебя в конфиге enableRetry/maxRetryAttempts/retryDelayMs — проверь, что в FetchProviderTemplateAsync(...) ретраи действительно делаются до выставления окончательного статуса провайдера. Если ретраи включены — логируй только на последней попытке (чтобы не засорять лог).

3.6 Совместимость UI / фильтра SFilter

Ты просишь «использовать стандартный интерфейс lampa». Это означает:

Не собирать «кастомный» HTML: двигайся строго через Tpl.ToJson() провайдеров и возвращай агрегированный JSON. Lampa сама отрисует обычный интерфейс (голоса/качества/сезоны).

SFilter-окно «просто фильтрует» — то есть твой smartfilter не вмешивается в структуру data[], не переименовывает поля, не меняет method/url/quality/translate и т.д.