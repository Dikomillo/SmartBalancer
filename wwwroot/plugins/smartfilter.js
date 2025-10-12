(function () {
  const PLUGIN_NS = 'smartfilter';
  const PROVIDER_NAME = 'SmartFilter Aggregator';

  // ===== Utils =====
  const qs = (sel, root = document) => root.querySelector(sel);
  const qsa = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  const esc = encodeURIComponent;

  function parseQuery(qsStr) {
    const q = {};
    (qsStr || '').replace(/^\?/, '').split('&').forEach(p => {
      if (!p) return;
      const [k, v] = p.split('=');
      q[decodeURIComponent(k)] = decodeURIComponent(v || '');
    });
    return q;
  }

  function buildUrlWithParams(base, params) {
    const u = new URL(base, location.origin);
    Object.entries(params || {}).forEach(([k, v]) => {
      if (v != null && v !== '') u.searchParams.set(k, v);
    });
    return u.toString();
  }

  function isSmartFilterActive() {
    // 1) когда уже внутри провайдера:
    if (location.pathname.startsWith('/lite/smartfilter')) return true;
    // 2) или выбран в селекторе провайдеров:
    const activeBtn = qsa('.videos__button.selector.active, .selectbox-item.active, .selector.active')
      .find(el => /SmartFilter Aggregator/i.test(el.textContent || el.getAttribute('title') || ''));
    return !!activeBtn;
  }

  // Находим контейнер с карточками
  function findCardsContainer() {
    // мягкая привязка: сначала ищем пометку data-smartfilter
    let root = qs('[data-smartfilter="true"]');
    if (root && qsa('.videos__item', root).length) return root;

    // фолбэк: любой контейнер с .videos__item
    const candidates = qsa('.videos__line, .videos__items, .videos, .content, body');
    for (const c of candidates) {
      if (qsa('.videos__item', c).length) return c;
    }
    return document.body;
  }

  // Нормализация текста
  const norm = (s) => (s || '').toString().trim().toLowerCase();
  const normalizeVoice = (s) => {
    const n = norm(s);
    if (['оригинал', 'original', 'orig', 'sub', 'субтитры'].includes(n)) return 'оригинал';
    return n;
  };
  const normalizeQuality = (s) => {
    const m = (s || '').toString().match(/\b(\d{3,4})p\b/i);
    return m ? `${m[1]}p` : (s || '').toString();
  };

  // собираем из JSON (от бэка) возможные значения озвучек/качеств
  function collectOptionsFromJson(json) {
    const voices = new Set();
    const quals = new Set();
    (json?.data || []).forEach(item => {
      const v = item.translate || item.voice || item.voice_name || item.details;
      const q = item.maxquality || item.quality || (item.stream && item.stream.maxquality);

      if (v) voices.add(normalizeVoice(v));
      if (q) quals.add(normalizeQuality(typeof q === 'string' ? q : (q.value || q)));
      // если item.quality — объект уровней, добавим ключи
      if (item.quality && typeof item.quality === 'object') {
        Object.keys(item.quality).forEach(k => quals.add(normalizeQuality(k)));
      }
    });
    return {
      voices: Array.from(voices).filter(Boolean).sort(),
      qualities: Array.from(quals).filter(Boolean).sort((a,b)=>{
        const na = parseInt(a), nb = parseInt(b);
        if (!isFinite(na) || !isFinite(nb)) return String(a).localeCompare(String(b));
        return nb - na; // от большего к меньшему
      })
    };
  }

  // Проверяем карточку на совпадение фильтра
  function cardMatches(el, sel) {
    try {
      const jsonStr = el.getAttribute('data-json'); // стандарт Lampa
      if (!jsonStr) return true;
      const data = JSON.parse(jsonStr);

      // Вытащим голос и качество из data
      const voice = normalizeVoice(data.translate || data.voice || data.voice_name || data.details);
      let maxq = data.maxquality || data.quality || (data.stream && data.stream.maxquality);
      if (typeof maxq === 'object' && maxq !== null) {
        // иногда объект с {value: "1080p"} или структура качества — проигнорируем
        maxq = maxq.value || null;
      }
      const quality = normalizeQuality(maxq || '');

      // Проверка по выбранному фильтру
      if (sel.voices.size && !sel.voices.has(voice)) return false;
      if (sel.qualities.size && !sel.qualities.has(quality)) return false;

      return true;
    } catch (e) {
      return true; // не ломаемся на странных карточках
    }
  }

  // Скрыть пустые группы ("папки") эвристикой: контейнер, у которого все .videos__item скрыты
  function hideEmptyGroups(root) {
    const groups = qsa('.videos__line, .videos__items', root);
    groups.forEach(g => {
      const items = qsa('.videos__item', g);
      if (!items.length) return;
      const visible = items.some(i => i.style.display !== 'none');
      g.style.display = visible ? '' : 'none';
    });
  }

  // ===== UI =====

  function ensureSFilterButton() {
    if (qs('.sf-btn')) return;

    // Ищем место рядом со стандартной "Фильтр"
    const filterBtn = qs('.filter--filter, .button--filter, [data-action="filter"]');
    const host = filterBtn?.parentElement || qs('.filter, .head, .subhead') || qs('.selector') || qs('body');

    const btn = document.createElement('div');
    btn.className = 'sf-btn simple-button selector';
    btn.textContent = 'SFilter';
    btn.title = 'Фильтр по озвучке и качеству (SmartFilter)';

    btn.addEventListener('click', openModal);
    btn.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' || e.key === ' ') {
        e.preventDefault(); openModal();
      }
    });
    btn.addEventListener('hover:enter', openModal);
    btn.addEventListener('focus:enter', openModal);

    if (filterBtn && filterBtn.nextSibling) {
      filterBtn.parentElement.insertBefore(btn, filterBtn.nextSibling);
    } else {
      host.appendChild(btn);
    }
  }

  function openModal() {
    // Строим модалку в стилях Lampa (минимально совместимые классы)
    let modal = qs('.sf-modal');
    if (!modal) {
      modal = document.createElement('div');
      modal.className = 'sf-modal modal';
      modal.innerHTML = `
        <div class="modal__content">
          <div class="modal__head">
            <div class="modal__title">SFilter — фильтр озвучки/качества</div>
          </div>
          <div class="modal__body">
            <div class="sf-status"></div>
            <div class="sf-filters">
              <div class="sf-col">
                <div class="sf-caption">Озвучка</div>
                <div class="sf-voices"></div>
              </div>
              <div class="sf-col">
                <div class="sf-caption">Качество</div>
                <div class="sf-qualities"></div>
              </div>
            </div>
          </div>
          <div class="modal__buttons">
            <div class="sf-apply simple-button selector">Применить</div>
            <div class="sf-reset simple-button selector">Сброс</div>
            <div class="sf-close simple-button selector">Закрыть</div>
          </div>
        </div>
      `;
      document.body.appendChild(modal);

      qs('.sf-close', modal).addEventListener('click', closeModal);
      qs('.sf-close', modal).addEventListener('hover:enter', closeModal);
      qs('.sf-close', modal).addEventListener('focus:enter', closeModal);

      qs('.sf-reset', modal).addEventListener('click', () => {
        qsa('.sf-voices input[type=checkbox], .sf-qualities input[type=checkbox]', modal)
          .forEach(i => i.checked = false);
        applyFilter();
      });
      qs('.sf-reset', modal).addEventListener('hover:enter', () => qs('.sf-reset', modal).click());
      qs('.sf-reset', modal).addEventListener('focus:enter', () => qs('.sf-reset', modal).click());

      qs('.sf-apply', modal).addEventListener('click', applyFilter);
      qs('.sf-apply', modal).addEventListener('hover:enter', applyFilter);
      qs('.sf-apply', modal).addEventListener('focus:enter', applyFilter);
    }

    modal.style.display = 'block';
    fetchJsonAndBuildFilters(modal).catch(err => {
      qs('.sf-status', modal).textContent = 'Ошибка загрузки данных SmartFilter';
      console.error(err);
    });
  }

  function closeModal() {
    const modal = qs('.sf-modal');
    if (modal) modal.style.display = 'none';
  }

  function fetchJsonAndBuildFilters(modal) {
    qs('.sf-status', modal).textContent = 'Загрузка…';

    // Соберём исходные параметры с текущей страницы (id, imdb_id, kp, title, year, serial…)
    const params = parseQuery(location.search);
    const base = location.pathname.startsWith('/lite/smartfilter')
      ? location.pathname + location.search
      : '/lite/smartfilter';
    // Убедимся, что rjson=true есть
    params.rjson = 'true';

    const url = buildUrlWithParams(base, params);
    return fetch(url, { credentials: 'include' })
      .then(r => r.json())
      .then(json => {
        // Рендер статусов провайдеров
        const st = qs('.sf-status', modal);
        const providers = Array.isArray(json.providers) ? json.providers : [];
        st.innerHTML = providers.length
          ? providers.map(p => `<div>• ${p.name}: ${p.status}${p.responseTime ? ` (${p.responseTime}ms)` : ''}</div>`).join('')
          : 'Провайдеры: нет данных';

        // Построим список чекбоксов
        const { voices, qualities } = collectOptionsFromJson(json);

        const vbox = qs('.sf-voices', modal); vbox.innerHTML = '';
        const qbox = qs('.sf-qualities', modal); qbox.innerHTML = '';

        if (!voices.length && !qualities.length) {
          // fallback: попробуем собрать по DOM, если JSON скуден
          const cards = qsa('.videos__item', findCardsContainer());
          const vSet = new Set(), qSet = new Set();
          cards.forEach(card => {
            try{
              const d = JSON.parse(card.getAttribute('data-json')||'{}');
              const v = d.translate || d.voice || d.voice_name || d.details;
              const mq = d.maxquality || d.quality || (d.stream && d.stream.maxquality);
              if (v) vSet.add(normalizeVoice(v));
              if (mq) qSet.add(normalizeQuality(typeof mq==='string'?mq:(mq?.value||mq)));
              if (d.quality && typeof d.quality==='object') {
                Object.keys(d.quality).forEach(k => qSet.add(normalizeQuality(k)));
              }
            }catch(_){}
          });
          vSet.forEach(v => voices.push(v));
          qSet.forEach(q => qualities.push(q));
        }

        const mk = (name, val) => {
          const id = `${name}-${val}`;
          return `<label class="selector"><input type="checkbox" data-type="${name}" value="${val}" /> <span>${val}</span></label>`;
        };
        vbox.innerHTML = voices.map(v => mk('voice', v)).join('') || '<div>Озвучки не найдены</div>';
        qbox.innerHTML = qualities.map(q => mk('quality', q)).join('') || '<div>Качества не найдены</div>';

        qs('.sf-status', modal).textContent = providers.length ? 'Готово' : 'Готово (без статусов)';
        // Поставим фокус на первую кнопку
        const firstBtn = qs('.sf-apply', modal) || qs('.sf-close', modal);
        if (firstBtn) firstBtn.focus();
      });
  }

  function applyFilter() {
    const modal = qs('.sf-modal');
    const sel = {
      voices: new Set(qsa('.sf-voices input[type=checkbox]:checked', modal).map(i => i.value)),
      qualities: new Set(qsa('.sf-qualities input[type=checkbox]:checked', modal).map(i => i.value))
    };

    const root = findCardsContainer();
    const cards = qsa('.videos__item', root);
    cards.forEach(card => {
      card.style.display = cardMatches(card, sel) ? '' : 'none';
    });

    hideEmptyGroups(root);
  }

  // ===== init =====
  function tick() {
    if (!isSmartFilterActive()) return;
    ensureSFilterButton();
  }

  const observer = new MutationObserver(() => {
    try { tick(); } catch(e){ console.error(e); }
  });
  observer.observe(document.documentElement, { childList: true, subtree: true });
  // начальный проход
  tick();

})();