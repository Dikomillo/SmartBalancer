Структура проекта

Сервер: SmartFilterController получает запросы, дергает SmartFilterEngine, кэширует результат и публикует прогресс через SmartFilterProgress; ResponseRenderer строит HTML.
Движок: SmartFilterEngine собирает провайдеров (GetActiveProvidersAsync), параллельно тянет данные (FetchProviderTemplateAsync с retry), нормализует тип (DetermineContentType) и строит итоговые payload’ы (BuildSeason/Episode/...).
Клиент: smartfilter.js перехватывает XHR, кеширует данные/ID, рисует прогресс, фильтры и управляет папками в выдаче.