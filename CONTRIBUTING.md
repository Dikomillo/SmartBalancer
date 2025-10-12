ниже — обещанный «джун-friendly» план: что оставить, что выкинуть, где именно поправить код, чтобы `/lite/smartfilter` отдавал ГОВТОВЫЕ данные (без «вечного pending»), а фронт рендерился стандартным интерфейсом Lampa (шаблоны Lampac). Всё привязал к твоим файлам.

---

# Что оставить, а что удалить

## Оставить (ядро модуля)

* `SmartFilterController.cs` — HTTP-endpoint `/lite/smartfilter` + `/lite/smartfilter/progress`  
* `SmartFilterEngine.cs` — опрос источников, нормализация, агрегация  
* `SmartFilterProgress.cs` — хранение прогресса/partial/финала  
* `AggregationModels.cs` — DTO для прогресса/результатов  
* `LampacResponseBuilder.cs` — собирает **правильный JSON для Lampa** из агрегированных данных (Movie/Season/Episode/Similar)  
* `SeriesDataHelper.cs` — вычленяет голоса/сезоны/эпизоды/макс.качество из разнородного JSON источников  
* `NormalizationStore.cs` + `normalization.json` — унификация ярлыков качества/озвучек
* `SmartFilter.conf` — конфиг модуля (таймауты, параллелизм, ретраи, exclude/include)
* `OnlineApi.cs` (модульный) — регистрирует провайдера «SmartFilter Aggregator» в `/lite/events`
* `ModInit.cs`, `SmartFilter.csproj`, `manifest.json` — служебные

## Удалить / сделать опциональным

* **`smartfilter.js` — УДАЛИТЬ.** Он сейчас перехватывает XHR/fetch и рисует «окно прогресса». Ты просишь использовать **стандартный интерфейс Lampa** и «SFilter просто фильтрует вкл/выкл качество и озвучку». Это можно (и лучше) делать без JS-перехватов: Lampa уже рендерит шаблоны, а простейшая фильтрация — это скрытие DOM-элементов по атрибутам/классам (если вообще нужно). Текущий файл — избыточный и ломает UX. 
* `ResponseRenderer.cs` — **опционально**. Нужен только если хочешь поддержать `rjson=false` (HTML-ответы). Если ваш клиент всегда шлёт `rjson=true`, файл можно убрать, а в контроллере ветку HTML — возвращать 404/ошибку. Сейчас он аккуратно рендерит HTML на основе агрегированных данных; можно оставить для совместимости. 
* `BaseOnlineController.cs` (из модуля) — **удалить, если в ядре Lampac уже есть одноимённая база**. Чтобы избежать конфликтов типов/неймспейсов, лучше наследоваться от базового контроллера ядра, а модульный дубликат не держать.

---

# Исправления, без которых всё «залипает» в pending

## 1) `/lite/events` парсится неправильно (жёстко `JArray.Parse`)

В Lampac `/lite/events` очень часто отдаёт **словарь** `Dictionary<string,string/obj>` (а не массив). У тебя в `SmartFilterEngine.GetActiveProvidersAsync` — **жёсткий** `JArray.Parse(response)`, из-за чего список провайдеров пустеет, `total=0`, а фронт видит «вечный pending». Исправь на универсальный парсер `JToken`:

**Где:** `SmartFilterEngine.cs`, метод `GetActiveProvidersAsync` (поиск `JArray.Parse(response)`)  

**Что сделать (по сути):**

* Парсить `JToken root = JToken.Parse(response)`.
* Если `root` — `JObject`: пройтись по `Properties()`, где `prop.Name` — это `name`, а `prop.Value` — либо строка-`url`, либо объект `{ url|uri, balanser|plugin, index }`.
* Если `root` — `JArray`: оставить твою текущую логику.
* Всегда делать `url.Replace("{localhost}", host)`, отфильтровывать самого себя (`"SmartFilter Aggregator"`), `excludeProviders`, `includeOnlyProviders`, исключать аниме-источники при `serial=0` (у тебя уже есть этот фильтр).  

> Это единственная причина «провайдеры: []» → «total:0» → «pending» на первом ответе.

## 2) Контроллер отдаёт ответ слишком рано

Сейчас `SmartFilterController.Index` при `rjson=true` отвечает **моментально**, если `aggregationTask` ещё не готов, не дав движку даже инициализировать прогресс. Поэтому первый `snapshot` почти всегда пустой.  

**Где:** `SmartFilterController.cs`, метод `Index`

**Что сделать (микроправка):**

* После запуска `aggregationTask` вставь «мягкое ожидание» и попробуй вернуть финал/частичный:

  ```csharp
  await Task.WhenAny(aggregationTask, Task.Delay(200)); // 200–300 мс достаточно
  if (aggregationTask.IsCompleted) aggregation = await SafeAwait(aggregationTask);
  ```
* Если таск не успел — верни **snapshot**, но он уже будет с непустым `total` (после правки п.1 движок успеет вызвать `Initialize(...)`).  
* Если провайдеров действительно нет (total==0) — сразу отдай **финал** с `ready:true` и `data: []` (а не `pending`). Тип можно вычислить `ResolveAggregationType(serial, provider, s)` (у тебя это уже есть).  

## 3) Частичный ответ = тоже валидный Lampa JSON

У тебя уже есть `LampacResponseBuilder.Build(...)`, который умеет собрать **корректный** JSON под шаблоны Lampac (movie/season/episode/similar) из агрегированных данных/partial. Убедись, что **в прогресс-ответе** при наличии `snapshot.Partial` вызывается `LampacResponseBuilder.Build(...)`, и в JSON попадают `type` и `data` — тогда Lampa сразу отрисует, а не будет «ждать окна прогресса».

---

# Пошаговый «как джуну»

## Шаг 0. Почистить репо

* Удалить: `plugins/smartfilter.js` (и ссылку на него из манифеста/шаблонов, если есть). Он не нужен, стандартный UI Lampa всё нарисует. 
* (Опционально) удалить `ResponseRenderer.cs`, если фронт всегда запрашивает `rjson=true`. Иначе оставить.
* (Если дублируется с ядром) удалить модульный `BaseOnlineController.cs`, использовать базовый из Lampac.

## Шаг 1. Исправить парсинг `/lite/events`

* Открыть `SmartFilterEngine.cs`.
* Найти `GetActiveProvidersAsync(...)`.
* Заменить `JArray.Parse(response)` на разбор `JToken` и поддержать **оба** формата (словарь/массив). См. правило выше.  
* Не забудь `url.Replace("{localhost}", host)`; отфильтровать `"SmartFilter Aggregator"`.  

## Шаг 2. Добавить «мягкое ожидание» в контроллер

* Открыть `SmartFilterController.cs`, метод `Index`.
* Сразу после получения `aggregationTask` (через `InvokeCache(...)`) добавь:

  ```csharp
  await Task.WhenAny(aggregationTask, Task.Delay(200));
  if (aggregationTask.IsCompleted) aggregation = await SafeAwait(aggregationTask);
  ```


* Если `aggregation == null`:

  * Получи `snapshot = SmartFilterProgress.Snapshot(...)`.
  * Если `snapshot.Total == 0` → **верни финал с пустым `data` и `ready:true`**, а не `pending`. Тип вычисли `ResolveAggregationType(...)`.  
  * Если есть `snapshot.Partial` → **через `LampacResponseBuilder.Build(...)` собери `type+data`** и верни это (можно `ready:false`, но `data` — непустой).  

## Шаг 3. Нормализация и метаданные (у тебя уже ок)

* `NormalizationStore` + `normalization.json` — оставить: они приводят «1080 / FHD / FullHD» к одному коду/лейблу и суммируют счётчики в `AggregationMetadata`.
* `SeriesDataHelper` — оставляем: вытаскивает `VoiceTpl/SeasonTpl/EpisodeTpl`-совместимые поля из разнородных ответов провайдеров (в т.ч. вложенные `data/results/playlist/...`).  
* `LampacResponseBuilder` — оставляем: строит правильный JSON для Lampa (`MovieTpl/SeasonTpl/EpisodeTpl/Similar`).  

## Шаг 4. Конфиг

* `SmartFilter.conf`:

  * `maxParallelRequests: 8` — норм.
  * `requestTimeoutSeconds: 25–30` — норм.
  * `enableRetry: true` (опционально), `maxRetryAttempts: 2–3`, `retryDelayMs: 300–500`.
  * Если нужно стабилизировать — используй `excludeProviders`/`includeOnlyProviders`.
* `ModInit.conf.cacheTimeMinutes` — 5–15 мин (кэш задачи агрегации).
* `OnlineApi.cs` — «SmartFilter Aggregator» остаётся.

## Шаг 5. Тесты руками

1. Фильм:
   `/lite/smartfilter?title=Орудия&year=2025&serial=0&rjson=true`
   Должен прийти:

   ```json
   { "type":"movie", "data":[ { "method":"play", "url":"...", "quality":{...}, "translate":"...", "maxquality":"...", "title":"..." }, ... ] }
   ```

   — формат как в твоём эталоне (методы и поля совпадают).
2. Сериал (список сезонов):
   `/lite/smartfilter?title=...&serial=-1&rjson=true` → `type:"season"` + элементы с `link` на конкретный сезон.
3. Сезон (эпизоды/голоса):
   `/lite/smartfilter?...&serial=1&s=1&rjson=true` → `type:"episode"` (`EpisodeTpl`) + `voice` (если провайдер даёт).
4. Пусто/ошибка:
   `/lite/smartfilter?...(что-то экзотическое)...&rjson=true` → **не pending**, а финал `ready:true, data:[]`.

---

# Что делать с `smartfilter.js`

**Удалить целиком.** Он:

* вмешивается в сетевые вызовы (hook XHR/fetch),
* рисует собственное окно прогресса,
* пытается «склеить» провайдеры/папки на клиенте.

Ты просишь «использовать стандартный интерфейс Lampa» и «SFilter только фильтрует качество/озвучку». Это означает: **никаких модальных окон/перехватов**. Lampa сама отрендерит из `Tpl.ToJson()` всё что нужно (фильмы/сезоны/эпизоды/голоса). Если хочется «тумблеры скрытия по качеству/озвучке» — это можно сделать минимальным пользовательским скриптом (10–20 строк), который **после рендера** пробегает DOM и скрывает `.videos__item`, если не подходит условие (по `data-provider`, `data-json` → `quality`/`voice_name`). Но это уже «сахар», не обязательная часть.

> Короче: **предпочтительно жить без `smartfilter.js` вовсе**. Если нужен — оставить только минимальные «hide/show» функции, без перехватов сетевых запросов.

---

# Почему это решает проблему сразу

* Правильный парсинг `/lite/events` → появляются провайдеры → `total>0`.  
* Мягкая задержка в контроллере → первый ответ уже **с partial**, а часто и финальный.  
* `LampacResponseBuilder` делает **валидный JSON** под шаблоны Lampa → UI рендерится стандартно, без кастомных окон.  
* Прогресс остаётся (для желающих), но не блокирует показ данных: `partial` уже содержит `type+data`, Lampa их отрисует.  

---

# Короткий чек-лист «что править»

1. `SmartFilterEngine.GetActiveProvidersAsync` — **поддержать Dictionary-формат** `/lite/events` (сегодня у тебя `JArray.Parse` → пусто).  
2. `SmartFilterController.Index` — добавить **200–300 мс** «мягкого ожидания», и если `total==0` → **сразу финал с `data:[]`**, а если есть `partial` → вернуть **валидный Lampa JSON** (через `LampacResponseBuilder.Build`).
3. Удалить `plugins/smartfilter.js`.  
4. (Опционально) убрать HTML-рендер (`ResponseRenderer.cs`) и ветку `rjson=false`, если не нужна.  
5. Проверить `SmartFilter.conf` (таймауты/параллелизм/ретраи) — оставить дефолты, при необходимости исключить токсичные источники.

---

если хочешь — могу выдать точные «патч-фрагменты» (готовые вставки) для двух мест:
а) парсер `/lite/events` в `SmartFilterEngine`,
б) «мягкое ожидание» + `partial→type/data` в `SmartFilterController`.
