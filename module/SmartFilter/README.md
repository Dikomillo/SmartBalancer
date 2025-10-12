README (минимальный, чтобы не ломать дальше)
Обзор

Модуль SmartFilter — агрегирует результаты из /lite/events провайдеров, нормализует в MovieTpl/VoiceTpl/SeasonTpl/EpisodeTpl, отдаёт HTML (для стандартного рендера Лампы) и JSON по rjson=true (для плагина). Эндпоинт: /lite/smartfilter. 

SmartFilterController

Плагин smartfilter.js — визуализирует статусы провайдеров (из JSON providers) и предоставляет фронтовую фильтрацию уже отрисованных карточек по озвучке и качеству (без вмешательства в работу бэкенда/модуля). 

smartfilter

Поток

Лампа запрашивает /lite/events и видит провайдера SmartFilter Aggregator (первым). 

OnlineApi

При выборе SmartFilter Лампа грузит /lite/smartfilter → получает HTML (карточки) → рендерит. 

SmartFilterController

Плагин при открытии модалки отправляет отдельный запрос на тот же URL с rjson=true (и теми же параметрами запроса) → получает JSON (type, data[], providers[]). 

SmartFilterController

Плагин строит UI с чекбоксами «Озвучка/Качество», применяет фильтрацию скрытием .videos__item и при необходимости скрывает пустые «папки».

Установка

Подключить модуль (как обычный Online-модуль) — manifest.json, ModInit.cs, OnlineApi.cs, контроллер/движок/парсеры на месте. 

manifest

 

ModInit

 

OnlineApi

Подключить плагин фронта: либо вставить <script src="/smartfilter.js"> через конфиг инициализации (lampainit/liteinit) или appReplace (см. общую механику генерации /lite.js). Важно: не заменять штатную кнопку «Фильтр», лишь добавить кнопку SFilter рядом. (Сама разметка должна использовать классы темы Лампы.)

SmartFilter.conf — правим лимиты, ретраи, списки excludeProviders/includeOnlyProviders. Горячая перезагрузка по mtime. 

SmartFilter

API контракты

GET /lite/smartfilter → HTML (рендер карточек).

GET /lite/smartfilter?rjson=true → JSON:

{
  "type": "movie|season|episode",
  "data": [ ... ],        // элементы с полями: url, method, stream, translate/voice_name, maxquality/quality, quality{}
  "providers": [          // статусы провайдеров
    {"name":"...", "status":"completed|error", "responseTime":1234}
  ]
}


Плагин использует translate|voice|voice_name и maxquality|quality. 

SmartFilterController

 

GetCinema

 

GetSerials

Правила для плагина

Никаких глобальных перехватов XHR/fetch (monkey-patch) — свой fetch только для rjson=true.

Кнопка SFilter активна, когда выбран источник SmartFilter.

Модалка — классы стандартной темы Лампы; simple-button selector; навигация пультом через Lampa.Controller.

Фильтрация — скрытие DOM-элементов .videos__item (и пустых «папок», если они есть); не трогать player.source().

Тест-чеклист (быстро)

Выбрать SmartFilter в списке источников → карточки нарисовались.

Нажать SFilter → модалка открылась «как штатная», показались чекбоксы озвучек/качеств, вверху — статусы провайдеров (по providers).

Применить фильтр → часть карточек скрылась, пустые группы скрыты, кнопки переводов не конфликтуют.

Сменить сезон/озвучку в сериале → повторно открыть SFilter → данные актуализировались (по rjson=true).