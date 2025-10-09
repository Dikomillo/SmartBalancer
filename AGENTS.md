Первым делом Изучи Документацию
https://github.com/immisterio/Lampac
https://github.com/immisterio/Lampac/blob/main/Shared/Models/Templates/SeasonTpl.cs
https://github.com/immisterio/Lampac/blob/main/Shared/Models/Templates/

SmartBalancer/example_only/ - только как примера!!

Основной язык русский.


PROMT:
Отлично, посмотрел твою ветку **codexTEST** в `SmartBalancer` и актуальный расклад в Lampac. Ниже — короткий и чёткий план внедрения именно под твою структуру репо.

---

# План (под актуальные файлы репо)

## 0) Где правим (по дереву твоего репо)

* Бэкенд (агрегатор/модуль): `module/SmartFilter/...`  ([GitHub][1])
* Фронтенд-плагин: `wwwroot/plugins/smartfilter.js`  ([GitHub][2])
* Lampac (ядро): `immisterio/Lampac` — без правок в ядре, но используем стандартный **/lite/**-паттерн. ([GitHub][3])

---

## 1) Бэкенд SmartFilter (C#) — агрегатор и прогресс

### 1.1 Маршруты

Сделай контроллер (или дополни существующий) по паттерну:

* `GET /lite/smartfilter` — отдаёт HTML (по умолчанию) **или** JSON при `rjson=true`.
* `GET /lite/smartfilter/progress?key=...` — опрос состояния/прогресса.

> Эти URL нужны фронту: он перехватывает именно `/lite/smartfilter` и опрашивает `/progress`.

### 1.2 Контракт ответа

* **HTML-режим:** оберни карточки в

  ```html
  <div data-smartfilter="true"> … .videos__item … </div>
  ```
* **JSON-режим (при `rjson=true`):**

  ```json
  { "results": [ /* playable-объекты */ ] }
  ```

### 1.3 Формат playable-элементов

Каждый **реально проигрываемый** элемент рендерим как `.videos__item` с `data-json`, где есть поля озвучки и качества (хватит одного из синонимов):

* Озвучка: `translate | voice | voice_name | voiceName | translation | dub`
* Качество: `maxquality | maxQuality | quality | quality_label | qualityName | video_quality | source_quality | hd`

**Фильм:**

```html
<div class="videos__item"
     data-provider="VoKino"
     data-folder="false"
     data-json='{"method":"play","translate":"Jaskier","quality":"1080p","url":"..."}'></div>
```

**Сериал (эпизод):**

```html
<div class="videos__item"
     data-provider="Rezka"
     data-folder="false"
     data-json='{"method":"play","type":"episode","season":1,"episode":3,"translate":"LostFilm","quality":"720p","url":"..."}'></div>
```

**Шапка провайдера (для сворачивания):**

```html
<div class="videos__item"
     data-provider="VoKino"
     data-folder="true"
     data-json='{"method":"folder","title":"VoKino"}'></div>
```

> Критично: эпизоды сериалов **не** `folder`, а `method:"play"` — иначе не будет ни фильтрации, ни воспроизведения.

### 1.4 Прогресс

В `Progress(key)` верни:

```json
{
  "ready": true|false,
  "progress": 0..100,
  "total": 7, "completed": 5, "items": 123,
  "providers": [
    {"name":"VoKino","status":"completed|running|pending|empty|error","items":32,"responseTime":120}
  ]
}
```

Фронт это уже умеет рисовать.

---

## 2) Фронтенд `wwwroot/plugins/smartfilter.js`

### 2.1 Кнопка SFilter — сделать визуально активной

Добавь в `ensureStyles()`:

```css
.smartfilter-sfilter-button.enabled{
  opacity: 1 !important;
  cursor: pointer !important;
}
```

### 2.2 Гарантировано насытим `sfilterItems`

* Перехватываем **XHR** на `/lite/smartfilter` (у тебя уже сделано).
* Добавь перехват **fetch** (если где-то вызывают `fetch` вместо XHR):

```js
if (typeof window.fetch === 'function') {
  const _fetch = window.fetch, self = window.__SmartFilterPlugin__ || this;
  window.fetch = function(url, opts){
    const p = _fetch.apply(this, arguments);
    const u = typeof url === 'string' ? url : (url && url.url);
    if (u && u.indexOf('/lite/smartfilter') !== -1) {
      p.then(r=>{
        if (r && r.ok) r.clone().json().then(d=>{
          const items = (d && (d.results || d.items)) || [];
          if (items.length) self.notifySFilterModule(items, {cachedData:d, cachedItems:items});
        }).catch(()=>{ /* если HTML — это ок */ });
      });
    }
    return p;
  };
}
```

### 2.3 Fallback-загрузка по клику

Если пользователь нажал SFilter слишком рано (массив пуст), плагин сам дёргает JSON и открывает модалку:

```js
activateSFilterButton(e){
  e && e.preventDefault && e.preventDefault();
  if (!this.sfilterItems || !this.sfilterItems.length){
    const q = location.search.replace(/^\?/,'');
    const api = '/lite/smartfilter?' + q + (q.includes('rjson=')?'':'&rjson=true');
    fetch(api).then(r=>r.json()).then(d=>{
      const items = (d && (d.results || d.items)) || [];
      if (!items.length) return Lampa?.Toast?.show?.('Данные ещё не готовы', 2500);
      this.notifySFilterModule(items, {cachedData:d, cachedItems:items});
      this.openSFilterModal();
    }).catch(()=> Lampa?.Toast?.show?.('Ошибка загрузки данных', 2500));
    return;
  }
  this.openSFilterModal();
}
```

### 2.4 Контейнер

На странице должен быть один корневой контейнер с результатами:

```html
<div data-smartfilter="true"> … </div>
```

Иначе фильтр не поймёт, какие `.videos__item` скрывать/показывать.

---

## 3) Интеграционные шаги (короткая последовательность)

1. **Бэкенд**:
   — Верни HTML в `<div data-smartfilter="true">…</div>` и JSON `{results:[]}` при `rjson=true`.
   — Убедись, что **каждый** playable-элемент содержит одно из полей озвучки и одно из полей качества.
   — Эпизоды сериалов → `method:"play"` + `type/season/episode`.

2. **Фронтенд**:
   — Добавь CSS для `.smartfilter-sfilter-button.enabled`.
   — Оставь перехват XHR **и** добавь перехват `fetch`.
   — В `activateSFilterButton()` добавь fallback-загрузку JSON.

3. **Проверка**:
   — В DevTools → Network видел `/lite/smartfilter?...` и (при `rjson=true`) в ответе есть `results[]`.
   — После первого получения данных SFilter визуально активна.
   — Модалка показывает уникальные озвучки/качества, фильтр скрывает карточки по выбору.
   — Сериалы: эпизоды видны как отдельные `.videos__item`, играют и фильтруются.

---

## 4) Что менять конкретно у тебя в репо

* `module/SmartFilter/…` — добавить/допилить контроллеры `Index` (HTML/JSON) и `Progress` под `/lite/smartfilter`.  ([GitHub][1])
* `wwwroot/plugins/smartfilter.js` — добавить CSS-правило, перехват `fetch`, fallback-загрузку в обработчик SFilter.  ([GitHub][2])

Lampac трогать не нужно — мы вписываемся в стандартные `/lite/*`-маршруты. ([GitHub][3])

---

Хочешь — пришлю дифф-патч для `wwwroot/plugins/smartfilter.js` (готовый блок вставок) и минимальный каркас C#-контроллера под твою структуру `module/SmartFilter`.

[1]: https://github.com/Dikomillo/SmartBalancer/tree/codexTEST/module/SmartFilter "SmartBalancer/module/SmartFilter at codexTEST · Dikomillo/SmartBalancer · GitHub"
[2]: https://github.com/Dikomillo/SmartBalancer/tree/codexTEST/wwwroot/plugins "SmartBalancer/wwwroot/plugins at codexTEST · Dikomillo/SmartBalancer · GitHub"
[3]: https://github.com/immisterio/Lampac?utm_source=chatgpt.com "immisterio/Lampac"
