# Codex Agent Rules — Lampac Project (DeepWiki + Context7)

## 📘 Общие правила
- Источник архитектуры и практик — DeepWiki репозитория `immisterio/Lampac`.
- Для любых внешних библиотек/API — сначала проверка через Context7 (актуальные версии, сигнатуры, примеры).
- Любое решение подтверждается ссылками на DeepWiki/Context7.
- Если уверенность < 99% — не писать код, продолжать уточнение.

---

## 🧭 Этап 1 — Анализ (DeepWiki)
1) `deepwiki.read_wiki_structure` — получить архитектуру проекта.  
2) Прочитать минимум:
   - `README.md`
   - `docs/architecture.md` (если есть)
   - `CONTRIBUTING.md`
   - релевантные гайды/модули
3) Сформулировать ≥5 вопросов через `deepwiki.ask_question`:
   - точки расширения, конфигурация, тесты, кодстайл, интеграции.

---

## 🧠 Этап 2 — Подтверждение понимания
- Краткое резюме структуры.
- Список интерфейсов/extension points.
- План реализации (по шагам).
- Все вопросы закрыты или сведены к чётким допущениям.

> Если не уверен — вернуться к Этапу 1.

---

## 🧪 Этап 2.5 — Верификация API (Context7)
Перед любым кодом, затрагивающим внешние библиотеки:
1) `context7.resolve-library-id("lib@version")` — закрепить точную версию.  
2) `context7.get-library-docs("lib", "symbol")` — проверить сигнатуры, ошибки, примеры.  
3) Зафиксировать в плане: используемая версия, ключевые методы, edge-cases.  
4) При отсутствии библиотеки — предложить ближайший поддерживаемый аналог (через Context7).

---

## 🛠 Этап 3 — Кодинг
- Каждому блоку кода предшествует цитата источников:
  - из DeepWiki — путь к файлу/разделу,
  - из Context7 — `lib@version` + запрошенные символы.
- Не выдумывать API; использовать только подтверждённые сигнатуры.
- Следовать соглашениям репозитория (имена, структура, формат ответов для Lampa).
- По окончании — финальная сверка с архитектурными требованиями.

---

## 🧪 Этап 4 — Тесты и интеграция
- Совместимость с текущими тестами/пайплайнами.
- Чек-лист:
  - изменённые файлы,
  - конфиги,
  - точки интеграции,
  - потенциальные регрессы.
- Всё соответствует правилам репозитория.

---

## 📚 Вспомогательные команды

### DeepWiki
- `deepwiki.read_wiki_structure { "repo": "immisterio/Lampac" }`
- `deepwiki.read_wiki_contents { "repo": "immisterio/Lampac", "path": "README.md" }`
- `deepwiki.ask_question { "repo": "immisterio/Lampac", "question": "..." }`
- `deepwiki.search { "repo": "immisterio/Lampac", "q": "provider search" }`
- `deepwiki.read { "repo": "immisterio/Lampac", "path": "src/modules/search.js" }`

### Context7 (MCP)
- `context7.resolve-library-id("axios@1")`
- `context7.get-library-docs("axios", "interceptors")`
- `context7.resolve-library-id("cheerio@1.0.0-rc.12")`
- `context7.get-library-docs("cheerio", "load")`
- `context7.resolve-library-id("ioredis@5")`
- `context7.get-library-docs("ioredis", "set")`

> Пример рабочего цикла:  
> 1) `deepwiki.search` → нашли нужный модуль → `deepwiki.read`  
> 2) `context7.resolve-library-id("cheerio@1.0.0-rc.12")`  
> 3) `context7.get-library-docs("cheerio", "CheerioAPI")`  
> 4) пишем код, ссылаясь на найденные источники.

---

## 🤖 Поведение агента
- Никогда не писать код без подтверждённого контекста (DeepWiki) и сигнатур (Context7).
- Всегда ссылаться на источники в комментариях кода.
- Всегда проходить Этапы 1 → 2 → 2.5 → 3 → 4.
- При сомнениях — **сначала вопрос/уточнение, а не код**.
