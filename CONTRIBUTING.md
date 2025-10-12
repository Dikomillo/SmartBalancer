
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