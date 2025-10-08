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
        cachedItems: null,
        metadata: null,
        metadataTtl: 5 * 60 * 1000,
        autoCloseTimer: null,
        interactionHandler: null,
        lastProgressState: null,
        progressReady: false,
        originalOpen: null,
        originalSend: null,
        activeModal: null,
        modalKeyHandler: null,
        modalBackHandler: null,
        previousController: null,
        folderClickHandler: null,

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
            this.bindProviderFolders();
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
                    top: 50%;
                    left: 50%;
                    transform: translate(-50%, -50%);
                    width: min(480px, 94vw);
                    max-width: 540px;
                    padding: 26px 28px;
                    border-radius: 20px;
                    background: radial-gradient(circle at top, rgba(60, 255, 180, 0.15), rgba(17, 17, 17, 0.95));
                    backdrop-filter: blur(14px) saturate(140%);
                    color: #fff;
                    font-size: 13px;
                    line-height: 1.45;
                    display: flex;
                    flex-direction: column;
                    gap: 16px;
                    pointer-events: auto;
                    z-index: 9999;
                    box-shadow: 0 18px 40px rgba(0, 0, 0, 0.55);
                    opacity: 0;
                    transform-origin: center;
                    animation: smartfilter-fade-in 0.35s ease forwards;
                }

                .smartfilter-progress--closing {
                    animation: smartfilter-fade-out 0.25s ease forwards;
                }

                .smartfilter-progress__header {
                    display: flex;
                    align-items: center;
                    gap: 14px;
                }

                .smartfilter-progress__loader {
                    width: 32px;
                    height: 32px;
                    display: inline-flex;
                    align-items: center;
                    justify-content: center;
                    border-radius: 50%;
                    border: 3px solid rgba(255, 255, 255, 0.2);
                    border-top-color: #5ce0a5;
                    animation: smartfilter-spin 0.85s linear infinite;
                    position: relative;
                    box-shadow: 0 0 12px rgba(92, 224, 165, 0.35);
                }

                .smartfilter-progress__loader--success {
                    border-color: rgba(124, 252, 202, 0.18);
                    background: rgba(92, 224, 165, 0.12);
                    animation: smartfilter-pulse 1.5s ease-out infinite;
                }

                .smartfilter-progress__loader--success::before {
                    content: '\\2713';
                    font-size: 18px;
                    color: #7bffb0;
                    animation: smartfilter-pop 0.35s ease forwards;
                }

                .smartfilter-progress--ready .smartfilter-progress__loader {
                    animation: none;
                }

                .smartfilter-progress__titles {
                    flex: 1;
                    min-width: 0;
                }

                .smartfilter-progress__title {
                    font-size: 16px;
                    font-weight: 600;
                    letter-spacing: 0.02em;
                }

                .smartfilter-progress__subtitle {
                    margin-top: 2px;
                    font-size: 12px;
                    color: rgba(255, 255, 255, 0.65);
                }

                .smartfilter-progress__stats {
                    display: flex;
                    justify-content: space-between;
                    gap: 8px;
                    margin: 6px 0 4px;
                    font-size: 12px;
                    color: rgba(255, 255, 255, 0.75);
                }

                .smartfilter-progress__bar {
                    position: relative;
                    height: 10px;
                    border-radius: 999px;
                    background: rgba(255, 255, 255, 0.12);
                    overflow: hidden;
                    box-shadow: inset 0 0 0 1px rgba(255, 255, 255, 0.08);
                }

                .smartfilter-progress__bar-inner {
                    height: 100%;
                    width: 0;
                    background: linear-gradient(90deg, #4caf50 0%, #7b61ff 100%);
                    background-size: 200% 100%;
                    transition: width 0.35s ease;
                    animation: smartfilter-progress-stripes 1.8s linear infinite;
                }

                .smartfilter-progress--ready .smartfilter-progress__bar-inner {
                    animation: none;
                }

                .smartfilter-progress__providers {
                    max-height: 220px;
                    overflow-y: auto;
                    margin-top: 16px;
                    padding-right: 4px;
                    display: flex;
                    flex-direction: column;
                    gap: 8px;
                    scrollbar-width: thin;
                }

                .smartfilter-progress__provider {
                    display: flex;
                    align-items: center;
                    justify-content: space-between;
                    gap: 12px;
                    padding: 10px 12px;
                    border-radius: 12px;
                    background: rgba(255, 255, 255, 0.04);
                    border: 1px solid rgba(255, 255, 255, 0.08);
                    box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.04);
                    transition: transform 0.25s ease, border 0.25s ease, background 0.25s ease;
                    position: relative;
                }

                .smartfilter-progress__provider::after {
                    content: '';
                    position: absolute;
                    inset: 0;
                    border-radius: 12px;
                    opacity: 0;
                    pointer-events: none;
                    transition: opacity 0.25s ease;
                    background: linear-gradient(120deg, rgba(255, 255, 255, 0.07), rgba(255, 255, 255, 0));
                    transform: translateX(-30%);
                }

                .smartfilter-progress__provider--running::after {
                    opacity: 1;
                    animation: smartfilter-sheen 1.8s linear infinite;
                }

                .smartfilter-progress__provider--pending {
                    border-left: 3px solid #b0bec5;
                }

                .smartfilter-progress__provider--running {
                    border-left: 3px solid #64b5f6;
                }

                .smartfilter-progress__provider--completed {
                    border-left: 3px solid #66bb6a;
                }

                .smartfilter-progress__provider--empty {
                    border-left: 3px solid #ffca28;
                }

                .smartfilter-progress__provider--error {
                    border-left: 3px solid #ef5350;
                }

                .smartfilter-progress__provider:hover {
                    transform: translateX(4px);
                }

                .smartfilter-progress__provider-meta {
                    flex: 1;
                    min-width: 0;
                }

                .smartfilter-progress__provider-name {
                    display: block;
                    font-weight: 500;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                }

                .smartfilter-progress__provider-note {
                    display: block;
                    margin-top: 2px;
                    font-size: 11px;
                    color: rgba(255, 255, 255, 0.55);
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                }

                .smartfilter-progress__provider-status {
                    padding: 4px 10px;
                    border-radius: 999px;
                    font-size: 11px;
                    font-weight: 600;
                    text-transform: uppercase;
                    letter-spacing: 0.05em;
                    position: relative;
                    padding-left: 20px;
                }

                .smartfilter-progress__provider-status::before {
                    content: '';
                    position: absolute;
                    width: 8px;
                    height: 8px;
                    left: 8px;
                    top: 50%;
                    transform: translate(-50%, -50%);
                    border-radius: 50%;
                    background: currentColor;
                    box-shadow: 0 0 0 0 currentColor;
                    animation: smartfilter-status-pulse 2s ease infinite;
                }

                .smartfilter-progress__provider-status--pending {
                    background: rgba(158, 158, 158, 0.25);
                    color: #f5f5f5;
                }

                .smartfilter-progress__provider-status--running {
                    background: rgba(33, 150, 243, 0.22);
                    color: #90caf9;
                }

                .smartfilter-progress__provider-status--completed {
                    background: rgba(76, 175, 80, 0.22);
                    color: #a5d6a7;
                }

                .smartfilter-progress__provider-status--empty {
                    background: rgba(255, 193, 7, 0.22);
                    color: #ffe082;
                }

                .smartfilter-progress__provider-status--error {
                    background: rgba(244, 67, 54, 0.25);
                    color: #ef9a9a;
                }

                .smartfilter-progress__hint {
                    margin-top: 16px;
                    font-size: 11px;
                    text-align: center;
                    color: rgba(255, 255, 255, 0.5);
                    letter-spacing: 0.01em;
                }

                .smartfilter-progress--ready .smartfilter-progress__hint {
                    color: rgba(255, 255, 255, 0.68);
                }

                .smartfilter-progress__providers::-webkit-scrollbar {
                    width: 6px;
                }

                .smartfilter-progress__providers::-webkit-scrollbar-track {
                    background: transparent;
                }

                .smartfilter-progress__providers::-webkit-scrollbar-thumb {
                    background: rgba(255, 255, 255, 0.2);
                    border-radius: 4px;
                }

                .smartfilter-progress--ready {
                    box-shadow: 0 12px 28px rgba(0, 0, 0, 0.45);
                }

                .smartfilter-progress--ready .smartfilter-progress__title {
                    color: #7bffb0;
                }

                .smartfilter-progress--ready .smartfilter-progress__bar {
                    background: rgba(124, 252, 202, 0.18);
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

                .smartfilter-meta {
                    margin-top: 4px;
                    font-size: 0.85em;
                    color: rgba(255, 255, 255, 0.7);
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

                @keyframes smartfilter-spin {
                    to { transform: rotate(360deg); }
                }

                @keyframes smartfilter-progress-stripes {
                    0% { background-position: 0% 0; }
                    100% { background-position: -200% 0; }
                }

                @keyframes smartfilter-sheen {
                    0% { opacity: 0; transform: translateX(-50%); }
                    50% { opacity: 0.65; transform: translateX(0); }
                    100% { opacity: 0; transform: translateX(50%); }
                }

                @keyframes smartfilter-fade-in {
                    from { opacity: 0; transform: translate(-50%, -52%) scale(0.95); }
                    to { opacity: 1; transform: translate(-50%, -50%) scale(1); }
                }

                @keyframes smartfilter-fade-out {
                    from { opacity: 1; transform: translate(-50%, -50%) scale(1); }
                    to { opacity: 0; transform: translate(-50%, -52%) scale(0.95); }
                }

                @keyframes smartfilter-pulse {
                    0%, 100% { box-shadow: 0 0 0 0 rgba(124, 252, 202, 0.22); }
                    50% { box-shadow: 0 0 0 6px rgba(124, 252, 202, 0); }
                }

                @keyframes smartfilter-pop {
                    from { transform: scale(0.6); opacity: 0; }
                    to { transform: scale(1); opacity: 1; }
                }

                @keyframes smartfilter-status-pulse {
                    0%, 100% { box-shadow: 0 0 0 0 currentColor; }
                    50% { box-shadow: 0 0 0 4px rgba(255, 255, 255, 0); }
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
                let finalUrl = url;
                if (typeof url === 'string')
                    finalUrl = SmartFilter.prepareRequestUrl(url);

                const args = Array.from(arguments);
                args[1] = finalUrl;

                this.__smartfilter_url = typeof finalUrl === 'string' ? finalUrl : (finalUrl && finalUrl.toString()) || '';
                this.__smartfilter_isTarget = SmartFilter.isSmartFilterRequest(this.__smartfilter_url);
                this.__smartfilter_method = method;

                return SmartFilter.originalOpen.apply(this, args);
            };

            XMLHttpRequest.prototype.send = function () {
                if (this.__smartfilter_isTarget)
                    SmartFilter.handleRequestStart(this);

                const onReadyStateChange = function () {
                    if (this.readyState !== 4)
                        return;

                    SmartFilter.captureMetadata(this);

                    if (this.__smartfilter_isTarget)
                        SmartFilter.handleRequestComplete(this);
                };

                this.addEventListener('readystatechange', onReadyStateChange);

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
            this.cachedItems = null;
            this.lastProgressState = null;
            this.progressReady = false;
            this.cancelAutoClose();
            this.hideProgress(true);
            this.updateFilterButtonState(false);
            this.startProgress();
        },

        handleRequestComplete(xhr) {
            this.stopProgress();

            if (!this.lastProgressState || !this.lastProgressState.ready)
                this.hideProgress(true);

            try {
                if (!xhr.responseText)
                    return;

                const data = JSON.parse(xhr.responseText);
                if (!data)
                    return;

                const flattened = this.flattenItems(data);
                this.cachedData = data;
                this.cachedItems = flattened;
                this.updateFilterButtonState(flattened.length > 0);
            } catch (err) {
                this.cachedData = null;
                this.cachedItems = null;
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

        prepareRequestUrl(url) {
            if (typeof url !== 'string' || !this.isSmartFilterRequest(url))
                return url;

            const metadata = this.getFreshMetadata();
            if (!metadata)
                return url;

            try {
                const targetUrl = new URL(url, window.location.origin);
                const search = targetUrl.searchParams;

                if ((!search.has('kinopoisk_id') || parseInt(search.get('kinopoisk_id'), 10) <= 0) && metadata.kinopoisk_id)
                    search.set('kinopoisk_id', metadata.kinopoisk_id);

                if (!search.get('imdb_id') && metadata.imdb_id)
                    search.set('imdb_id', metadata.imdb_id);

                if ((!search.has('title') || !search.get('title')) && metadata.title)
                    search.set('title', metadata.title);

                if ((!search.has('original_title') || !search.get('original_title')) && metadata.original_title)
                    search.set('original_title', metadata.original_title);

                if ((!search.has('year') || search.get('year') === '0') && metadata.year)
                    search.set('year', String(metadata.year));

                if (typeof metadata.serial === 'number' && (!search.has('serial') || search.get('serial') === '-1'))
                    search.set('serial', String(metadata.serial));

                return targetUrl.toString();
            } catch (err) {
                return url;
            }
        },

        flattenItems(response) {
            if (!response)
                return [];

            if (Array.isArray(response))
                return response.filter((item) => item && typeof item === 'object');

            if (typeof response !== 'object')
                return [];

            if (Array.isArray(response.data) || typeof response.data === 'object')
                return this.flattenItems(response.data);

            if (Array.isArray(response.results) || typeof response.results === 'object')
                return this.flattenItems(response.results);

            const aggregated = [];
            Object.values(response).forEach((value) => {
                if (Array.isArray(value))
                    aggregated.push(...value.filter((item) => item && typeof item === 'object'));
            });

            return aggregated;
        },

        getFreshMetadata() {
            if (!this.metadata)
                return null;

            if (!this.metadata.timestamp || (Date.now() - this.metadata.timestamp) > this.metadataTtl) {
                this.metadata = null;
                return null;
            }

            return this.metadata;
        },

        captureMetadata(xhr) {
            if (!xhr)
                return;

            const responseType = xhr.responseType;
            if (responseType && responseType !== '' && responseType !== 'text' && responseType !== 'json')
                return;

            let payload = null;

            try {
                if (responseType === 'json' && xhr.response) {
                    payload = xhr.response;
                } else {
                    const text = xhr.responseText;
                    if (!text)
                        return;

                    const trimmed = text.trim();
                    if (!trimmed || (trimmed[0] !== '{' && trimmed[0] !== '['))
                        return;

                    payload = JSON.parse(trimmed);
                }
            } catch (err) {
                return;
            }

            const meta = this.extractMetadata(payload);
            if (meta)
                this.updateMetadata(meta);
        },

        extractMetadata(payload) {
            if (!payload || typeof payload !== 'object')
                return null;

            const meta = {};
            const stack = [payload];
            const seen = new WeakSet();

            while (stack.length) {
                const current = stack.pop();
                if (!current || typeof current !== 'object')
                    continue;

                if (seen.has(current))
                    continue;

                seen.add(current);

                if (Array.isArray(current)) {
                    for (const item of current)
                        stack.push(item);
                    continue;
                }

                for (const [key, value] of Object.entries(current)) {
                    if (value === null || value === undefined)
                        continue;

                    const lower = key.toLowerCase();

                    if (lower === 'kinopoisk_id' || lower === 'kp_id' || lower === 'kinopoiskid') {
                        const num = this.parsePositiveInt(value);
                        if (num)
                            meta.kinopoisk_id = num;
                    } else if (lower === 'imdb_id' || lower === 'imdb') {
                        const imdb = this.normalizeImdbId(value);
                        if (imdb)
                            meta.imdb_id = imdb;
                    } else if (lower === 'title' || lower === 'name' || lower === 'ru_title') {
                        const textValue = this.normalizeText(value);
                        if (textValue)
                            meta.title ??= textValue;
                    } else if (lower === 'original_title' || lower === 'originalname' || lower === 'original_name' || lower === 'orig_title') {
                        const original = this.normalizeText(value);
                        if (original)
                            meta.original_title ??= original;
                    } else if (lower === 'year') {
                        const year = this.parsePositiveInt(value, 4);
                        if (year)
                            meta.year ??= year;
                    } else if (lower === 'release_date' || lower === 'first_air_date' || lower === 'air_date' || lower === 'premiere_ru' || lower === 'premiere_world') {
                        const parsedYear = this.parseYearFromDate(value);
                        if (parsedYear)
                            meta.year ??= parsedYear;
                    } else if (lower === 'is_serial' || lower === 'serial' || lower === 'season_count' || lower === 'seasons') {
                        const serial = this.parseSerialFlag(lower, value);
                        if (serial !== null)
                            meta.serial = serial;
                    } else if (lower === 'type' || lower === 'content_type' || lower === 'category') {
                        const serial = this.parseSerialFromType(value);
                        if (serial !== null)
                            meta.serial = serial;
                    }

                    if (value && typeof value === 'object')
                        stack.push(value);
                }
            }

            if (!Object.keys(meta).length)
                return null;

            return meta;
        },

        updateMetadata(meta) {
            if (!meta || !Object.keys(meta).length)
                return;

            const existing = this.getFreshMetadata() || {};

            const next = {
                timestamp: Date.now(),
                kinopoisk_id: meta.kinopoisk_id || existing.kinopoisk_id,
                imdb_id: meta.imdb_id || existing.imdb_id,
                title: meta.title || existing.title,
                original_title: meta.original_title || existing.original_title,
                year: meta.year || existing.year,
                serial: meta.serial !== undefined ? meta.serial : existing.serial
            };

            this.metadata = next;
        },

        parsePositiveInt(value, digits = 0) {
            const num = Number.parseInt(value, 10);
            if (!Number.isFinite(num) || num <= 0)
                return null;

            if (digits && num.toString().length < digits)
                return null;

            return num;
        },

        parseYearFromDate(value) {
            if (!value)
                return null;

            const match = value.toString().match(/\d{4}/);
            if (!match)
                return null;

            return this.parsePositiveInt(match[0], 4);
        },

        parseSerialFlag(key, value) {
            if (value === null || value === undefined)
                return null;

            if (typeof value === 'boolean')
                return value ? 1 : 0;

            if (typeof value === 'number')
                return value > 0 ? 1 : 0;

            const str = value.toString().toLowerCase();

            if (key === 'season_count' || key === 'seasons') {
                const num = Number.parseInt(str, 10);
                if (Number.isFinite(num))
                    return num > 0 ? 1 : 0;
            }

            if (str === 'true' || str === 'serial' || str === 'tv' || str === 'show' || str === 'yes')
                return 1;
            if (str === 'false' || str === 'movie' || str === 'no')
                return 0;

            return null;
        },

        parseSerialFromType(value) {
            if (!value)
                return null;

            const str = value.toString().toLowerCase();

            if (/serial|series|tv|show|episode|anime/.test(str))
                return 1;
            if (/movie|film|documovie/.test(str))
                return 0;

            return null;
        },

        normalizeText(value) {
            if (value === null || value === undefined)
                return '';

            return value.toString().trim();
        },

        normalizeImdbId(value) {
            if (value === null || value === undefined)
                return '';

            const str = value.toString().trim();
            if (!str)
                return '';

            const match = str.match(/tt\d+/i);
            return match ? match[0].toLowerCase() : '';
        },

        startProgress() {
            if (!this.progressKey || !this.progressHost)
                return;

            this.stopProgress();
            this.progressReady = false;
            this.lastProgressState = { ready: false };
            this.cancelAutoClose();
            this.renderProgress({ ready: false, progress: 0, providers: [] });

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
            if (!data) {
                this.hideProgress(true);
                return;
            }

            let container = document.querySelector('.smartfilter-progress');
            if (!container) {
                container = document.createElement('div');
                container.className = 'smartfilter-progress';
                document.body.appendChild(container);
            }

            container.classList.remove('smartfilter-progress--closing');

            const total = data.total || data.Total || 0;
            const completed = data.completed || data.Completed || 0;
            const progress = data.progress || data.ProgressPercentage || data.Progress || 0;
            const progressValue = Math.max(0, Math.min(100, progress));
            const progressDisplay = Math.round(progressValue);
            const items = data.items || data.Items || 0;
            const providersRaw = data.providers || data.Providers || [];
            const providers = Array.isArray(providersRaw) ? providersRaw : [];
            const ready = Boolean(data.ready || data.Ready);

            const providerRows = providers.map((provider) => {
                const status = provider.status || provider.Status || 'pending';
                const name = provider.name || provider.Name || 'Провайдер';
                const itemsCount = provider.items ?? provider.Items ?? 0;
                const responseTime = provider.responseTime ?? provider.ResponseTime ?? 0;
                const error = provider.error || provider.Error || '';
                const info = this.describeStatus(status, itemsCount, error, responseTime);
                const statusClass = `smartfilter-progress__provider-status--${info.className}`;
                const providerClass = `smartfilter-progress__provider smartfilter-progress__provider--${info.className}`;
                const note = info.note ? `<span class="smartfilter-progress__provider-note">${this.escapeHtml(info.note)}</span>` : '';

                return `<div class="${providerClass}">
                    <div class="smartfilter-progress__provider-meta">
                        <span class="smartfilter-progress__provider-name">${this.escapeHtml(name)}</span>
                        ${note}
                    </div>
                    <span class="smartfilter-progress__provider-status ${statusClass}">${this.escapeHtml(info.label)}</span>
                </div>`;
            }).join('');

            const summarySubtitle = ready
                ? 'Загрузка завершена'
                : (total > 0 ? `Обработано ${completed}/${total}` : 'Собираем источники...');

            const hint = ready
                ? 'Нажмите любую кнопку или подождите 5 секунд, чтобы закрыть'
                : 'Можно продолжать пользоваться приложением — сбор идёт в фоне';

            const providersHtml = providerRows || `<div class="smartfilter-progress__provider smartfilter-progress__provider--pending">
                    <div class="smartfilter-progress__provider-meta">
                        <span class="smartfilter-progress__provider-name">Подключаем источники…</span>
                    </div>
                    <span class="smartfilter-progress__provider-status smartfilter-progress__provider-status--pending">Ожидание</span>
                </div>`;

            const loader = ready
                ? '<div class="smartfilter-progress__loader smartfilter-progress__loader--success"></div>'
                : '<div class="smartfilter-progress__loader"></div>';

            container.innerHTML = `
                <div class="smartfilter-progress__header">
                    ${loader}
                    <div class="smartfilter-progress__titles">
                        <div class="smartfilter-progress__title">SmartFilter</div>
                        <div class="smartfilter-progress__subtitle">${this.escapeHtml(summarySubtitle)}</div>
                    </div>
                </div>
                <div class="smartfilter-progress__stats">
                    <span>Источников: ${total}</span>
                    <span>Готовность: ${progressDisplay}%</span>
                    <span>Ссылок: ${items}</span>
                </div>
                <div class="smartfilter-progress__bar"><div class="smartfilter-progress__bar-inner" style="width:${progressValue}%"></div></div>
                <div class="smartfilter-progress__providers">${providersHtml}</div>
                <div class="smartfilter-progress__hint">${this.escapeHtml(hint)}</div>
            `;

            container.classList.toggle('smartfilter-progress--ready', ready);

            if (ready) {
                this.progressReady = true;
                this.scheduleAutoClose(true);
            } else {
                this.progressReady = false;
                this.cancelAutoClose();
            }

            this.lastProgressState = { ready, total, completed, items };
        },

        scheduleAutoClose(forceRestart = false) {
            if (this.autoCloseTimer && !forceRestart)
                return;

            if (this.autoCloseTimer)
                clearTimeout(this.autoCloseTimer);

            this.attachInteractionClose();
            this.autoCloseTimer = setTimeout(() => this.hideProgress(), 5000);
        },

        cancelAutoClose() {
            if (this.autoCloseTimer) {
                clearTimeout(this.autoCloseTimer);
                this.autoCloseTimer = null;
            }

            this.clearInteractionClose();
        },

        attachInteractionClose() {
            if (this.interactionHandler)
                return;

            const handler = () => {
                this.hideProgress();
            };

            this.interactionHandler = handler;
            ['click', 'wheel', 'keydown', 'touchstart'].forEach((eventName) => {
                window.addEventListener(eventName, handler, { passive: true });
            });
        },

        clearInteractionClose() {
            if (!this.interactionHandler)
                return;

            const handler = this.interactionHandler;
            ['click', 'wheel', 'keydown', 'touchstart'].forEach((eventName) => {
                window.removeEventListener(eventName, handler);
            });

            this.interactionHandler = null;
        },

        hideProgress(immediate = false) {
            const container = document.querySelector('.smartfilter-progress');
            this.cancelAutoClose();
            this.progressReady = false;
            this.lastProgressState = null;

            if (!container)
                return;

            if (immediate) {
                container.remove();
            } else {
                container.classList.add('smartfilter-progress--closing');
                setTimeout(() => {
                    if (container.parentElement)
                        container.remove();
                }, 250);
            }
        },

        escapeHtml(value) {
            if (value === null || value === undefined)
                return '';

            return String(value).replace(/[&<>"']/g, (char) => ({
                '&': '&amp;',
                '<': '&lt;',
                '>': '&gt;',
                '"': '&quot;',
                "'": '&#39;'
            })[char]);
        },

        truncateText(value, maxLength = 120) {
            if (value === null || value === undefined)
                return '';

            const text = String(value).trim();
            if (text.length <= maxLength)
                return text;

            return text.slice(0, maxLength - 1) + '…';
        },

        pluralize(number, forms) {
            if (!Array.isArray(forms) || forms.length < 3)
                return forms && forms.length ? forms[0] : '';

            const n = Math.abs(Number(number)) % 100;
            const n1 = n % 10;

            if (n > 10 && n < 20)
                return forms[2];
            if (n1 > 1 && n1 < 5)
                return forms[1];
            if (n1 === 1)
                return forms[0];
            return forms[2];
        },

        describeStatus(status, items, error, responseTime) {
            const normalized = (status || '').toString().toLowerCase();
            const map = {
                pending: { label: 'Ожидание', className: 'pending', note: 'В очереди' },
                running: { label: 'Загрузка', className: 'running', note: responseTime > 0 ? `${responseTime} мс` : 'Ожидаем ответ' },
                completed: { label: 'Готово', className: 'completed' },
                empty: { label: 'Пусто', className: 'empty', note: 'Результатов нет' },
                error: { label: 'Ошибка', className: 'error' }
            };

            const info = map[normalized] || map.pending;
            let note = info.note || '';

            if (normalized === 'completed') {
                if (items > 0)
                    note = `${items} ${this.pluralize(items, ['ссылка', 'ссылки', 'ссылок'])}`;
                else
                    note = 'Новых ссылок нет';
            } else if (normalized === 'error') {
                note = error ? this.truncateText(error, 80) : 'Ошибка загрузки';
            }

            return { label: info.label, className: info.className, note };
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

        bindProviderFolders() {
            if (this.folderClickHandler)
                return;

            this.folderClickHandler = (event) => {
                const target = event.target && event.target.closest
                    ? event.target.closest('.videos__item[data-folder="true"][data-provider][data-json]')
                    : null;
                if (!target)
                    return;

                let payload = null;
                try {
                    const json = target.getAttribute('data-json');
                    payload = json ? JSON.parse(json) : null;
                } catch (err) {
                    payload = null;
                }

                if (!payload || (payload.method || '').toString().toLowerCase() !== 'folder')
                    return;

                event.preventDefault();
                event.stopPropagation();
                if (typeof event.stopImmediatePropagation === 'function')
                    event.stopImmediatePropagation();

                const provider = target.dataset ? target.dataset.provider : null;
                if (!provider)
                    return;

                const container = target.closest ? target.closest('[data-smartfilter="true"]') : null;
                const expand = target.dataset.expanded !== 'true';
                target.dataset.expanded = expand ? 'true' : 'false';
                target.classList.toggle('smartfilter-expanded', expand);

                const scope = container || document;
                this.syncProviderVisibility(provider, scope);

                if (expand && container && container.querySelector) {
                    const selector = `.videos__item[data-provider="${this.escapeCssAttr(provider)}"][data-folder="false"]`;
                    const next = container.querySelector(selector);
                    if (next && window.Lampa && Lampa.Controller && typeof Lampa.Controller.collectionFocus === 'function')
                        Lampa.Controller.collectionFocus(next, container);
                }
            };

            document.addEventListener('click', this.folderClickHandler, true);
        },

        syncProviderVisibility(provider, root = document) {
            if (!provider || !root || typeof root.querySelectorAll !== 'function')
                return;

            const selector = `[data-provider="${this.escapeCssAttr(provider)}"]`;
            const items = root.querySelectorAll(selector);
            if (!items.length)
                return;

            let folder = null;
            items.forEach((item) => {
                if (item.dataset && item.dataset.folder === 'true')
                    folder = item;
            });

            const expanded = folder ? folder.dataset.expanded === 'true' : true;

            items.forEach((item) => {
                if (item === folder)
                    return;

                const dataset = item.dataset || {};
                const hiddenByFilter = dataset.hiddenByFilter === 'true';

                if (hiddenByFilter || !expanded)
                    item.style.display = 'none';
                else
                    item.style.display = '';
            });
        },

        syncAllProviderVisibility(root = document) {
            if (!root || typeof root.querySelectorAll !== 'function')
                return;

            const providers = new Set();
            root.querySelectorAll('[data-folder="true"][data-provider]').forEach((folder) => {
                const provider = folder.dataset ? folder.dataset.provider : null;
                if (provider)
                    providers.add(provider);
            });

            providers.forEach((provider) => this.syncProviderVisibility(provider, root));
        },

        escapeCssAttr(value) {
            if (typeof value !== 'string')
                return '';

            if (typeof CSS !== 'undefined' && typeof CSS.escape === 'function')
                return CSS.escape(value);

            return value
                .replace(/\\/g, '\\\\')
                .replace(/"/g, '\\"')
                .replace(/([\^$*+?.()|[\]{}])/g, '\\$1');
        },

        openFilterModal() {
            this.closeFilterModal();

            if (!Array.isArray(this.cachedItems) || !this.cachedItems.length) {
                if (window.Lampa && Lampa.Toast)
                    Lampa.Toast.show('Данные еще загружаются', 2500);
                return;
            }

            const voices = new Map();
            const qualities = new Map();

            this.cachedItems.forEach((item) => {
                if (!item || typeof item !== 'object')
                    return;

                const translateSource = item.translate || item.voice || 'Оригинал';
                const translate = translateSource !== null && translateSource !== undefined
                    ? translateSource.toString()
                    : 'Оригинал';
                voices.set(translate, true);

                const qualitySource = item.maxquality || item.quality;
                if (qualitySource !== null && qualitySource !== undefined)
                    qualities.set(qualitySource.toString(), true);
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

            const closeModal = () => this.closeFilterModal();

            modal.querySelector('#smartfilter-modal-close').addEventListener('click', closeModal);
            modal.querySelector('#smartfilter-reset').addEventListener('click', () => {
                modal.querySelectorAll('.smartfilter-chip').forEach((chip) => chip.classList.remove('active'));
            });
            modal.querySelector('#smartfilter-apply').addEventListener('click', () => {
                const selectedVoices = Array.from(modal.querySelectorAll('.smartfilter-chip[data-type="voice"].active')).map((chip) => chip.dataset.value);
                const selectedQuality = Array.from(modal.querySelectorAll('.smartfilter-chip[data-type="quality"].active')).map((chip) => chip.dataset.value);
                this.applyFilters(selectedVoices, selectedQuality);
                closeModal();
            });

            modal.querySelectorAll('.smartfilter-chip').forEach((chip) => {
                chip.addEventListener('click', () => chip.classList.toggle('active'));
            });

            document.body.appendChild(modal);
            this.activeModal = modal;

            const keyHandler = (event) => {
                if (!this.isBackNavigation(event))
                    return;

                event.preventDefault();
                event.stopPropagation();
                closeModal();
            };

            this.modalKeyHandler = keyHandler;
            window.addEventListener('keydown', keyHandler, true);

            const backButtonHandler = () => closeModal();
            this.modalBackHandler = backButtonHandler;
            document.addEventListener('backbutton', backButtonHandler, true);

            if (window.Lampa && Lampa.Controller && typeof Lampa.Controller.add === 'function') {
                const controllerName = 'smartfilter-modal';
                const enabled = typeof Lampa.Controller.enabled === 'function' ? Lampa.Controller.enabled() : null;
                this.previousController = enabled && enabled.name ? enabled.name : null;

                Lampa.Controller.add(controllerName, {
                    toggle: () => {
                        if (typeof Lampa.Controller.collectionSet === 'function')
                            Lampa.Controller.collectionSet(modal);

                        const target = modal.querySelector('.smartfilter-chip.active')
                            || modal.querySelector('#smartfilter-apply')
                            || modal.querySelector('#smartfilter-modal-close');

                        if (target && typeof Lampa.Controller.collectionFocus === 'function')
                            Lampa.Controller.collectionFocus(target, modal);
                    },
                    back: closeModal
                });

                if (typeof Lampa.Controller.toggle === 'function')
                    Lampa.Controller.toggle(controllerName);
            }
        },

        closeFilterModal() {
            const modal = this.activeModal;
            if (!modal)
                return;

            if (this.modalKeyHandler) {
                window.removeEventListener('keydown', this.modalKeyHandler, true);
                this.modalKeyHandler = null;
            }

            if (this.modalBackHandler) {
                document.removeEventListener('backbutton', this.modalBackHandler, true);
                this.modalBackHandler = null;
            }

            if (window.Lampa && Lampa.Controller) {
                if (typeof Lampa.Controller.remove === 'function')
                    Lampa.Controller.remove('smartfilter-modal');

                if (this.previousController && typeof Lampa.Controller.toggle === 'function')
                    Lampa.Controller.toggle(this.previousController);

                this.previousController = null;
            }

            if (modal.parentElement)
                modal.remove();

            this.activeModal = null;
        },

        isBackNavigation(event) {
            if (!event)
                return false;

            const key = typeof event.key === 'string' ? event.key.toLowerCase() : '';
            if (['back', 'backspace', 'escape', 'esc', 'browserback'].includes(key))
                return true;

            const keyCode = event.keyCode || event.which || event.detail;
            return [8, 27, 461, 10009, 166].includes(keyCode);
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

            const hasFolders = !!container.querySelector('[data-folder="true"][data-provider]');
            const voiceList = Array.isArray(voices)
                ? voices.map((voice) => voice !== null && voice !== undefined ? voice.toString() : '').filter(Boolean)
                : [];
            const qualityList = Array.isArray(qualities)
                ? qualities.map((quality) => quality !== null && quality !== undefined ? quality.toString() : '').filter(Boolean)
                : [];

            if (!hasFolders) {
                const items = container.querySelectorAll('.videos__item');
                items.forEach((item) => {
                    item.style.display = '';
                    const dataJson = item.getAttribute('data-json');
                    if (!dataJson)
                        return;

                    try {
                        const payload = JSON.parse(dataJson);
                        if (payload && (payload.method || '').toString().toLowerCase() === 'folder')
                            return;

                        const translateSource = payload.translate || payload.voice || 'Оригинал';
                        const translate = translateSource !== null && translateSource !== undefined
                            ? translateSource.toString()
                            : 'Оригинал';
                        const qualitySource = payload.maxquality || payload.quality;
                        const maxquality = qualitySource !== null && qualitySource !== undefined
                            ? qualitySource.toString()
                            : '';

                        const voiceMatch = !voiceList.length || voiceList.includes(translate);
                        const qualityMatch = !qualityList.length || !maxquality || qualityList.includes(maxquality);

                        if (!voiceMatch || !qualityMatch)
                            item.style.display = 'none';
                    } catch (err) {
                        /* ignore */
                    }
                });

                return;
            }

            const items = container.querySelectorAll('.videos__item');
            items.forEach((item) => {
                if (!item.dataset)
                    return;

                if (item.dataset.folder === 'true')
                    return;

                const dataJson = item.getAttribute('data-json');
                if (!dataJson) {
                    delete item.dataset.hiddenByFilter;
                    return;
                }

                let payload = null;
                try {
                    payload = JSON.parse(dataJson);
                } catch (err) {
                    delete item.dataset.hiddenByFilter;
                    return;
                }

                if (!payload || (payload.method || '').toString().toLowerCase() === 'folder') {
                    delete item.dataset.hiddenByFilter;
                    return;
                }

                const translateSource = payload.translate || payload.voice || 'Оригинал';
                const translate = translateSource !== null && translateSource !== undefined
                    ? translateSource.toString()
                    : 'Оригинал';
                const qualitySource = payload.maxquality || payload.quality;
                const maxquality = qualitySource !== null && qualitySource !== undefined
                    ? qualitySource.toString()
                    : '';

                const voiceMatch = !voiceList.length || voiceList.includes(translate);
                const qualityMatch = !qualityList.length || !maxquality || qualityList.includes(maxquality);

                if (!voiceMatch || !qualityMatch)
                    item.dataset.hiddenByFilter = 'true';
                else
                    delete item.dataset.hiddenByFilter;
            });

            this.syncAllProviderVisibility(container);
        }
    };

    SmartFilter.init();
})();
