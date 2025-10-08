(function () {
    'use strict';

    const SmartFilter = {
        initAttempts: 0,
        maxInitAttempts: 80,
        retryDelay: 300,
        progressTimer: null,
        progressKey: null,
        progressHost: null,
        cachedData: null,
        originalOpen: null,
        originalSend: null,

        init() {
            if (!window.Lampa || !Lampa.Template || !document.body) {
                this.scheduleInit();
                return;
            }

            if (this.initialized)
                return;

            this.initialized = true;
            this.ensureStyles();
            this.observeSourceList();
            this.ensureFilterButton();
            this.hookXHR();
        },

        scheduleInit() {
            if (this.initAttempts >= this.maxInitAttempts)
                return;

            this.initAttempts += 1;
            setTimeout(this.init.bind(this), this.retryDelay * Math.pow(1.3, this.initAttempts));
        },

        ensureStyles() {
            if (document.getElementById('smartfilter-style'))
                return;

            const style = document.createElement('style');
            style.id = 'smartfilter-style';
            style.textContent = `
                .smartfilter-progress {
                    position: fixed;
                    left: 20px;
                    bottom: 20px;
                    width: 280px;
                    padding: 12px;
                    border-radius: 8px;
                    background: rgba(18, 18, 18, 0.92);
                    color: #fff;
                    font-size: 13px;
                    z-index: 9999;
                    box-shadow: 0 6px 16px rgba(0, 0, 0, 0.35);
                }

                .smartfilter-progress__bar {
                    height: 6px;
                    border-radius: 4px;
                    background: rgba(255, 255, 255, 0.15);
                    overflow: hidden;
                    margin-top: 8px;
                }

                .smartfilter-progress__bar-inner {
                    height: 100%;
                    width: 0;
                    background: linear-gradient(90deg, #4CAF50 0%, #81C784 100%);
                    transition: width 0.25s ease;
                }

                .smartfilter-progress__providers {
                    max-height: 180px;
                    overflow-y: auto;
                    margin-top: 8px;
                }

                .smartfilter-progress__provider {
                    display: flex;
                    justify-content: space-between;
                    align-items: center;
                    margin-top: 4px;
                }

                .smartfilter-progress__provider-name {
                    flex: 1;
                    margin-right: 6px;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                }

                .smartfilter-progress__provider-status {
                    font-size: 11px;
                    opacity: 0.75;
                }

                .smartfilter-sfilter-button {
                    border: 2px solid #4CAF50 !important;
                    margin-left: 10px;
                    opacity: 0.5;
                    cursor: not-allowed;
                }

                .smartfilter-sfilter-button.enabled {
                    opacity: 1;
                    cursor: pointer;
                }

                .smartfilter-modal {
                    position: fixed;
                    top: 0;
                    left: 0;
                    right: 0;
                    bottom: 0;
                    background: rgba(0, 0, 0, 0.85);
                    z-index: 10000;
                    display: flex;
                    justify-content: center;
                    align-items: center;
                }

                .smartfilter-modal__content {
                    width: 90%;
                    max-width: 520px;
                    max-height: 80%;
                    overflow-y: auto;
                    background: #1f1f1f;
                    border-radius: 10px;
                    padding: 20px;
                    color: #fff;
                }

                .smartfilter-modal__section {
                    margin-top: 16px;
                }

                .smartfilter-modal__section h3 {
                    margin: 0 0 8px 0;
                    font-size: 16px;
                }

                .smartfilter-chip {
                    display: inline-flex;
                    align-items: center;
                    padding: 6px 10px;
                    border-radius: 20px;
                    background: rgba(255, 255, 255, 0.08);
                    margin: 4px;
                    cursor: pointer;
                    transition: background 0.2s ease;
                }

                .smartfilter-chip.active {
                    background: #4CAF50;
                }

                .smartfilter-chip input {
                    margin-right: 6px;
                }

                .smartfilter-source-highlight {
                    background: linear-gradient(90deg, #2E7D32 0%, #4CAF50 100%) !important;
                    color: #fff !important;
                }
            `;
            document.head.appendChild(style);
        },

        observeSourceList() {
            const observer = new MutationObserver(() => this.decorateSource());
            observer.observe(document.body, { childList: true, subtree: true });
            this.decorateSource();
        },

        decorateSource() {
            const items = document.querySelectorAll('.selectbox-item');
            items.forEach((item) => {
                if (item.classList.contains('smartfilter-processed'))
                    return;

                const title = item.querySelector('.selectbox-item__title');
                if (!title || !title.textContent)
                    return;

                if (title.textContent.toLowerCase().indexOf('smartfilter') === -1)
                    return;

                item.classList.add('smartfilter-processed', 'smartfilter-source-highlight');
                const parent = item.parentElement;
                if (parent)
                    parent.insertBefore(item, parent.firstChild);

                const badge = document.createElement('div');
                badge.className = 'smartfilter-source-container';
                badge.innerHTML = `
                    <div style="display:flex;align-items:center;justify-content:space-between;width:100%">
                        <span style="font-weight:bold;font-size:1.05em">SmartFilter</span>
                        <span style="font-size:0.85em;background:rgba(0,0,0,0.3);padding:2px 8px;border-radius:12px;">Агрегатор</span>
                    </div>`;
                title.innerHTML = '';
                title.appendChild(badge);
            });
        },

        ensureFilterButton() {
            const filterBlock = document.querySelector('.filter--filter');
            if (!filterBlock)
                return;

            if (filterBlock.parentElement.querySelector('.smartfilter-sfilter-button'))
                return;

            const button = document.createElement('div');
            button.className = 'simple-button simple-button--filter selector smartfilter-sfilter-button';
            button.innerHTML = '<span>SFilter</span>';
            button.addEventListener('click', () => this.openFilterModal());
            filterBlock.parentElement.insertBefore(button, filterBlock.nextSibling);
        },

        hookXHR() {
            if (this.originalOpen)
                return;

            this.originalOpen = XMLHttpRequest.prototype.open;
            this.originalSend = XMLHttpRequest.prototype.send;

            XMLHttpRequest.prototype.open = function (method, url) {
                this.__smartfilter_url = typeof url === 'string' ? url : (url && url.toString()) || '';
                this.__smartfilter_isTarget = SmartFilter.isSmartFilterRequest(this.__smartfilter_url);
                this.__smartfilter_method = method;
                return SmartFilter.originalOpen.apply(this, arguments);
            };

            XMLHttpRequest.prototype.send = function () {
                if (this.__smartfilter_isTarget) {
                    SmartFilter.handleRequestStart(this);
                    this.addEventListener('readystatechange', function () {
                        if (this.readyState === 4) {
                            SmartFilter.handleRequestComplete(this);
                        }
                    });
                }

                return SmartFilter.originalSend.apply(this, arguments);
            };
        },

        isSmartFilterRequest(url) {
            return typeof url === 'string' && url.indexOf('/lite/smartfilter') !== -1;
        },

        handleRequestStart(xhr) {
            const info = this.parseRequestUrl(xhr.__smartfilter_url);
            this.progressHost = info.origin;
            this.progressKey = info.progressKey;
            this.cachedData = null;
            this.updateFilterButtonState(false);
            this.startProgress();
        },

        handleRequestComplete(xhr) {
            this.stopProgress();
            this.renderProgress(null);

            try {
                if (!xhr.responseText)
                    return;

                const data = JSON.parse(xhr.responseText);
                if (!data || !data.data)
                    return;

                this.cachedData = data;
                this.updateFilterButtonState(true);
            } catch (err) {
                this.cachedData = null;
                this.updateFilterButtonState(false);
            }
        },

        parseRequestUrl(url) {
            try {
                const targetUrl = new URL(url, window.location.origin);
                const params = [];
                targetUrl.searchParams.forEach((value, key) => {
                    if (key.toLowerCase() === 'rjson')
                        return;
                    params.push([key, value]);
                });

                params.sort((a, b) => a[0].localeCompare(b[0]));
                const key = 'smartfilter:' + params.map(([k, v]) => `${k}=${v}`).join('&');
                return {
                    origin: `${targetUrl.protocol}//${targetUrl.host}`,
                    progressKey: key ? `${key}:progress` : null
                };
            } catch (err) {
                return { origin: window.location.origin, progressKey: null };
            }
        },

        startProgress() {
            if (!this.progressKey || !this.progressHost)
                return;

            this.renderProgress({ ready: false, progress: 0, providers: [] });
            this.stopProgress();

            const pull = () => {
                const url = `${this.progressHost}/lite/smartfilter/progress?key=${encodeURIComponent(this.progressKey)}`;
                fetch(url, { credentials: 'include' })
                    .then((response) => response.ok ? response.json() : null)
                    .then((data) => {
                        if (!data)
                            return;

                        this.renderProgress(data);
                        if (data.ready)
                            this.stopProgress();
                    })
                    .catch(() => { });
            };

            pull();
            this.progressTimer = setInterval(pull, 600);
        },

        stopProgress() {
            if (this.progressTimer) {
                clearInterval(this.progressTimer);
                this.progressTimer = null;
            }
        },

        renderProgress(data) {
            let container = document.querySelector('.smartfilter-progress');
            if (!data) {
                if (container)
                    container.remove();
                return;
            }

            if (!container) {
                container = document.createElement('div');
                container.className = 'smartfilter-progress';
                document.body.appendChild(container);
            }

            const total = data.total || data.Total || 0;
            const progress = data.progress || data.ProgressPercentage || data.Progress || 0;
            const items = data.items || data.Items || 0;
            const providersRaw = data.providers || data.Providers || [];
            const providers = Array.isArray(providersRaw) ? providersRaw : [];

            const providerRows = providers.map((provider) => {
                const status = provider.status || provider.Status || 'pending';
                const name = provider.name || provider.Name || 'Провайдер';
                return `<div class="smartfilter-progress__provider">
                    <span class="smartfilter-progress__provider-name">${name}</span>
                    <span class="smartfilter-progress__provider-status">${status}</span>
                </div>`;
            }).join('');

            container.innerHTML = `
                <div><strong>SmartFilter</strong></div>
                <div>Источников: ${total} • Найдено ссылок: ${items}</div>
                <div class="smartfilter-progress__bar"><div class="smartfilter-progress__bar-inner" style="width:${progress}%"></div></div>
                <div class="smartfilter-progress__providers">${providerRows}</div>
            `;

            if (data.ready || data.Ready) {
                setTimeout(() => container.remove(), 2500);
            }
        },

        updateFilterButtonState(enabled) {
            const button = document.querySelector('.smartfilter-sfilter-button');
            if (!button)
                return;

            if (enabled) {
                button.classList.add('enabled');
            } else {
                button.classList.remove('enabled');
            }
        },

        openFilterModal() {
            if (!this.cachedData || !this.cachedData.data || !this.cachedData.data.length) {
                if (window.Lampa && Lampa.Toast)
                    Lampa.Toast.show('Данные еще загружаются', 2500);
                return;
            }

            const voices = new Map();
            const qualities = new Map();

            this.cachedData.data.forEach((item) => {
                const translate = item.translate || item.voice || 'Оригинал';
                voices.set(translate, true);

                if (item.maxquality)
                    qualities.set(item.maxquality, true);
            });

            const modal = document.createElement('div');
            modal.className = 'smartfilter-modal';
            modal.innerHTML = `
                <div class="smartfilter-modal__content">
                    <div style="display:flex;justify-content:space-between;align-items:center;">
                        <h2 style="margin:0">SmartFilter</h2>
                        <button class="simple-button selector" id="smartfilter-modal-close">Закрыть</button>
                    </div>
                    <div class="smartfilter-modal__section">
                        <h3>Озвучки</h3>
                        <div>${Array.from(voices.keys()).map((voice) => this.createChip('voice', voice)).join('')}</div>
                    </div>
                    <div class="smartfilter-modal__section">
                        <h3>Качество</h3>
                        <div>${Array.from(qualities.keys()).map((quality) => this.createChip('quality', quality)).join('')}</div>
                    </div>
                    <div style="margin-top:20px;display:flex;justify-content:flex-end;gap:10px;">
                        <button class="simple-button selector" id="smartfilter-reset">Сбросить</button>
                        <button class="simple-button selector" id="smartfilter-apply">Применить</button>
                    </div>
                </div>`;

            modal.querySelector('#smartfilter-modal-close').addEventListener('click', () => modal.remove());
            modal.querySelector('#smartfilter-reset').addEventListener('click', () => {
                modal.querySelectorAll('.smartfilter-chip').forEach((chip) => chip.classList.remove('active'));
            });
            modal.querySelector('#smartfilter-apply').addEventListener('click', () => {
                const selectedVoices = Array.from(modal.querySelectorAll('.smartfilter-chip[data-type="voice"].active')).map((chip) => chip.dataset.value);
                const selectedQuality = Array.from(modal.querySelectorAll('.smartfilter-chip[data-type="quality"].active')).map((chip) => chip.dataset.value);
                this.applyFilters(selectedVoices, selectedQuality);
                modal.remove();
            });

            modal.querySelectorAll('.smartfilter-chip').forEach((chip) => {
                chip.addEventListener('click', () => chip.classList.toggle('active'));
            });

            document.body.appendChild(modal);
        },

        createChip(type, value) {
            return `<label class="smartfilter-chip" data-type="${type}" data-value="${value}">
                <input type="checkbox" />
                <span>${value}</span>
            </label>`;
        },

        applyFilters(voices, qualities) {
            const container = document.querySelector('[data-smartfilter="true"]');
            if (!container)
                return;

            const items = container.querySelectorAll('.videos__item');
            items.forEach((item, index) => {
                item.style.display = '';
                const dataJson = item.getAttribute('data-json');
                if (!dataJson)
                    return;

                try {
                    const payload = JSON.parse(dataJson);
                    const translate = payload.translate || payload.voice || 'Оригинал';
                    const maxquality = payload.maxquality || payload.quality;

                    const voiceMatch = !voices.length || voices.includes(translate);
                    const qualityMatch = !qualities.length || qualities.includes(maxquality);

                    if (!voiceMatch || !qualityMatch)
                        item.style.display = 'none';
                } catch (err) {
                    /* ignore */
                }
            });
        }
    };

    SmartFilter.init();
})();
