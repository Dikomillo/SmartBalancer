
(function (global) {
    'use strict';

    const window = global;
    const document = window && window.document ? window.document : null;

    const SmartFilter = {
        initAttempts: 0,
        maxInitAttempts: 80,
        retryDelay: 300,
        progressTimer: null,
        progressKey: null,
        progressHost: null,
        legacyObserverInterval: null,
        cachedData: null,
        cachedItems: null,
        cachedMetadata: null,
        seriesState: null,
        metadata: null,
        metadataTtl: 5 * 60 * 1000,
        metadataScanLimit: 2000,
        metadataArrayLimit: 200,
        autoCloseTimer: null,
        interactionHandler: null,
        lastProgressState: null,
        progressReady: false,
        originalOpen: null,
        originalSend: null,
        originalFetch: null,
        folderClickHandler: null,
        legacyMode: false,
        sfilterItems: [],
        sfilterButton: null,
        sfilterButtonHandler: null,
        sfilterButtonHoverHandler: null,
        sfilterButtonKeyHandler: null,
        sfilterContainer: null,
        sfilterModal: null,
        sfilterKeyHandler: null,
        sfilterBackHandler: null,
        sfilterPrevController: null,
        sfilterControllerName: 'smartfilter-modal',

        init() {
            this.ensurePolyfills();
            this.legacyMode = this.detectLegacyMode();

            if (!document || !document.body) {
                if (document && typeof setTimeout === 'function')
                    this.scheduleInit();
                return;
            }

            const lampa = window && window.Lampa ? window.Lampa : null;
            if (!lampa || !lampa.Template) {
                this.scheduleInit();
                return;
            }

            if (this.initialized)
                return;

            this.initialized = true;
            this.ensureStyles();
            this.observeSourceList();
            this.ensureSFilterIntegration();
            this.hookXHR();
            this.hookFetch();
            this.bindProviderFolders();
        },

        ensurePolyfills() {
            if (typeof Array.from !== 'function')
                Array.from = function (arrayLike) { return Array.prototype.slice.call(arrayLike); };

            if (typeof Object.values !== 'function')
                Object.values = function (obj) {
                    if (obj === null || typeof obj !== 'object')
                        return [];
                    const result = [];
                    for (const key in obj) {
                        if (Object.prototype.hasOwnProperty.call(obj, key))
                            result.push(obj[key]);
                    }
                    return result;
                };

            if (typeof Object.entries !== 'function')
                Object.entries = function (obj) {
                    if (obj === null || typeof obj !== 'object')
                        return [];
                    const result = [];
                    for (const key in obj) {
                        if (Object.prototype.hasOwnProperty.call(obj, key))
                            result.push([key, obj[key]]);
                    }
                    return result;
                };

            if (!Array.prototype.includes)
                Array.prototype.includes = function (searchElement) {
                    const fromIndex = arguments.length > 1 ? Number(arguments[1]) || 0 : 0;
                    for (let i = Math.max(fromIndex, 0); i < this.length; i += 1) {
                        if (this[i] === searchElement)
                            return true;
                    }
                    return false;
                };

            if (!String.prototype.includes)
                String.prototype.includes = function (search, start) {
                    return this.indexOf(search, start || 0) !== -1;
                };

            if (typeof Number.isFinite !== 'function')
                Number.isFinite = function (value) { return typeof value === 'number' && isFinite(value); };

            if (typeof Number.parseInt !== 'function')
                Number.parseInt = parseInt;

            if (typeof Number.parseFloat !== 'function')
                Number.parseFloat = parseFloat;

            if (typeof NodeList !== 'undefined' && !NodeList.prototype.forEach)
                NodeList.prototype.forEach = Array.prototype.forEach;

            if (typeof window.Map !== 'function') {
                const SimpleMap = function () {
                    this._keys = [];
                    this._values = [];
                };
                SimpleMap.prototype.set = function (key, value) {
                    const index = this._keys.indexOf(key);
                    if (index === -1) {
                        this._keys.push(key);
                        this._values.push(value);
                    } else {
                        this._values[index] = value;
                    }
                };
                SimpleMap.prototype.get = function (key) {
                    const index = this._keys.indexOf(key);
                    return index === -1 ? undefined : this._values[index];
                };
                SimpleMap.prototype.keys = function () {
                    return this._keys.slice();
                };
                SimpleMap.prototype.clear = function () {
                    this._keys.length = 0;
                    this._values.length = 0;
                };
                window.Map = SimpleMap;
            }

            if (typeof window.Set !== 'function') {
                const SimpleSet = function () { this._values = []; };
                SimpleSet.prototype.add = function (value) {
                    if (this._values.indexOf(value) === -1)
                        this._values.push(value);
                };
                SimpleSet.prototype.has = function (value) {
                    return this._values.indexOf(value) !== -1;
                };
                SimpleSet.prototype.forEach = function (callback, thisArg) {
                    const values = this._values.slice();
                    for (let i = 0; i < values.length; i += 1)
                        callback.call(thisArg, values[i], values[i], this);
                };
                window.Set = SimpleSet;
            }

            if (typeof window.WeakSet !== 'function') {
                const SimpleWeakSet = function () { this._values = []; };
                SimpleWeakSet.prototype.add = function (value) {
                    if (value && typeof value === 'object' && this._values.indexOf(value) === -1)
                        this._values.push(value);
                    return this;
                };
                SimpleWeakSet.prototype.has = function (value) {
                    return this._values.indexOf(value) !== -1;
                };
                window.WeakSet = SimpleWeakSet;
            }
        },

        detectLegacyMode() {
            if (!document || typeof document.createElement !== 'function')
                return false;

            const testEl = document.createElement('div');
            const lacksClassList = !testEl || !('classList' in testEl);
            const lacksFetch = !window || typeof window.fetch !== 'function';
            const lacksCssSupports = !window || !window.CSS || typeof window.CSS.supports !== 'function';
            const lacksPromise = !window || typeof window.Promise !== 'function';
            return lacksClassList || lacksFetch || lacksCssSupports || lacksPromise;
        },

        hasClass(element, className) {
            if (!element || !className)
                return false;

            if (element.classList && typeof element.classList.contains === 'function')
                return element.classList.contains(className);

            const current = element.className || '';
            return (` ${current} `).indexOf(` ${className} `) !== -1;
        },

        addClass(element, className) {
            if (!element || !className)
                return;

            const classes = Array.isArray(className) ? className : [className];
            classes.forEach((cls) => {
                if (!cls)
                    return;

                if (element.classList && typeof element.classList.add === 'function')
                    element.classList.add(cls);
                else if (!this.hasClass(element, cls))
                    element.className = `${element.className ? `${element.className} ` : ''}${cls}`;
            });
        },

        removeClass(element, className) {
            if (!element || !className)
                return;

            const classes = Array.isArray(className) ? className : [className];
            classes.forEach((cls) => {
                if (!cls)
                    return;

                if (element.classList && typeof element.classList.remove === 'function') {
                    element.classList.remove(cls);
                } else if (element.className) {
                    element.className = element.className
                        .split(' ')
                        .filter((item) => item && item !== cls)
                        .join(' ');
                }
            });
        },

        toggleClass(element, className, force) {
            if (!element || !className)
                return;

            const shouldAdd = force === undefined ? !this.hasClass(element, className) : Boolean(force);
            if (shouldAdd)
                this.addClass(element, className);
            else
                this.removeClass(element, className);
        },

        forEachNode(collection, callback) {
            if (!collection || typeof callback !== 'function')
                return;

            const items = typeof collection.length === 'number'
                ? Array.prototype.slice.call(collection)
                : [];

            for (let i = 0; i < items.length; i += 1)
                callback(items[i], i);
        },

        requestJson(url, onSuccess, onError) {
            if (!url)
                return;

            if (typeof fetch === 'function') {
                fetch(url, { credentials: 'include' })
                    .then((response) => (response && response.ok) ? response.json() : null)
                    .then((data) => {
                        if (typeof onSuccess === 'function' && data)
                            onSuccess.call(this, data);
                    })
                    .catch((err) => {
                        if (typeof onError === 'function')
                            onError.call(this, err);
                    });
                return;
            }

            try {
                const xhr = new XMLHttpRequest();
                xhr.open('GET', url, true);
                xhr.withCredentials = true;
                const self = this;
                xhr.onreadystatechange = function () {
                    if (this.readyState !== 4)
                        return;

                    if (this.status >= 200 && this.status < 300) {
                        try {
                            const payload = JSON.parse(this.responseText || 'null');
                            if (payload && typeof onSuccess === 'function')
                                onSuccess.call(self, payload);
                        } catch (error) {
                            if (typeof onError === 'function')
                                onError.call(self, error);
                        }
                    } else if (typeof onError === 'function') {
                        onError.call(self, new Error('HTTP ' + this.status));
                    }
                };
                xhr.send(null);
            } catch (error) {
                if (typeof onError === 'function')
                    onError.call(this, error);
            }
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
                    width: min(560px, 96vw);
                    max-width: 640px;
                    padding: 32px 34px;
                    border-radius: 20px;
                    background: radial-gradient(circle at top, rgba(60, 255, 180, 0.15), rgba(17, 17, 17, 0.95));
                    backdrop-filter: blur(14px) saturate(140%);
                    color: #fff;
                    font-size: 13px;
                    line-height: 1.5;
                    display: flex;
                    flex-direction: column;
                    gap: 18px;
                    pointer-events: auto;
                    z-index: 9999;
                    box-shadow: 0 18px 40px rgba(0, 0, 0, 0.55);
                    opacity: 0;
                    transform-origin: center;
                    animation: smartfilter-fade-in 0.35s ease forwards;
                }

                .smartfilter-progress--legacy {
                    width: min(520px, 96vw);
                    max-width: 560px;
                    padding: 24px 26px;
                    background: rgba(17, 17, 17, 0.95);
                    border: 1px solid rgba(255, 255, 255, 0.18);
                    box-shadow: 0 18px 34px rgba(0, 0, 0, 0.6);
                    backdrop-filter: none;
                    animation: none;
                    opacity: 1 !important;
                    line-height: 1.55;
                    font-size: 13px;
                    word-break: break-word;
                }

                .smartfilter-progress--closing {
                    animation: smartfilter-fade-out 0.25s ease forwards;
                }

                .smartfilter-progress__header {
                    display: flex;
                    align-items: center;
                    gap: 16px;
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

                .smartfilter-progress--legacy .smartfilter-progress__loader,
                .smartfilter-progress--legacy .smartfilter-progress__loader::before {
                    animation: none !important;
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
                    gap: 10px;
                    margin: 8px 0 4px;
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

                .smartfilter-progress--legacy .smartfilter-progress__bar-inner {
                    animation: none;
                }

                .smartfilter-progress--ready .smartfilter-progress__bar-inner {
                    animation: none;
                }

                .smartfilter-progress__providers {
                    max-height: 260px;
                    overflow-y: auto;
                    margin-top: 18px;
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

                .smartfilter-progress--legacy .smartfilter-progress__provider-name,
                .smartfilter-progress--legacy .smartfilter-progress__provider-note {
                    white-space: normal;
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
                    margin-top: 18px;
                    font-size: 11px;
                    text-align: center;
                    color: rgba(255, 255, 255, 0.5);
                    letter-spacing: 0.01em;
                    line-height: 1.45;
                }

                .smartfilter-progress--legacy .smartfilter-progress__hint {
                    color: rgba(255, 255, 255, 0.75);
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
                    opacity: 1 !important;
                    cursor: pointer !important;
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
                    padding: 6px 12px;
                    margin: 4px;
                    border-radius: 20px;
                    border: 0;
                    background: rgba(255, 255, 255, 0.08);
                    color: inherit;
                    font: inherit;
                    cursor: pointer;
                    transition: background 0.2s ease, transform 0.15s ease;
                }

                .smartfilter-chip:focus {
                    outline: none;
                }

                .smartfilter-chip:focus-visible {
                    outline: 2px solid rgba(76, 175, 80, 0.65);
                    outline-offset: 2px;
                }

                .smartfilter-chip.active {
                    background: #4CAF50;
                    transform: translateY(-1px);
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
            if (!document || !document.body)
                return;

            const refresh = () => {
                this.decorateSource();
                this.ensureSFilterIntegration();

                const container = document.querySelector('[data-smartfilter="true"]');
                if (container || (this.cachedItems && this.cachedItems.length))
                    this.notifySFilterModule(this.cachedItems || [], { ensureButton: true, container, metadata: this.cachedMetadata });
            };

            if (typeof MutationObserver === 'function') {
                const observer = new MutationObserver(refresh);
                observer.observe(document.body, { childList: true, subtree: true });
            } else if (!this.legacyObserverInterval && typeof setInterval === 'function') {
                this.legacyObserverInterval = setInterval(refresh, 800);
            }

            refresh();
        },

        decorateSource() {
            const items = document.querySelectorAll('.selectbox-item');
            items.forEach((item) => {
                if (this.hasClass(item, 'smartfilter-processed'))
                    return;

                const title = item.querySelector('.selectbox-item__title');
                if (!title || !title.textContent)
                    return;

                if (title.textContent.toLowerCase().indexOf('smartfilter') === -1)
                    return;

                this.addClass(item, ['smartfilter-processed', 'smartfilter-source-highlight']);
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

        ensureSFilterIntegration() {
            this.ensureSFilterContainer();
            this.ensureSFilterButton();
            this.updateSFilterButtonState();
        },

        ensureSFilterContainer() {
            if (this.sfilterContainer && document.contains(this.sfilterContainer))
                return this.sfilterContainer;

            const element = document.querySelector('[data-smartfilter="true"]');
            if (element)
                this.sfilterContainer = element;

            return this.sfilterContainer;
        },

        ensureSFilterButton() {
            const filterBlock = document.querySelector('.filter--filter');
            if (!filterBlock || !filterBlock.parentElement)
                return null;

            const parent = filterBlock.parentElement;
            let button = parent.querySelector('.smartfilter-sfilter-button');

            if (!button) {
                button = document.createElement('div');
                button.className = 'simple-button simple-button--filter selector smartfilter-sfilter-button';
                button.innerHTML = '<span>SFilter</span>';
                parent.insertBefore(button, filterBlock.nextSibling);
            }

            button.setAttribute('role', 'button');
            button.setAttribute('tabindex', '0');
            button.setAttribute('aria-label', 'Открыть фильтры SmartFilter');

            if (!this.sfilterButtonHandler)
                this.sfilterButtonHandler = this.onSFilterButtonClick.bind(this);

            if (!this.sfilterButtonHoverHandler)
                this.sfilterButtonHoverHandler = (event) => this.activateSFilterButton(event);

            if (!this.sfilterButtonKeyHandler)
                this.sfilterButtonKeyHandler = (event) => {
                    if (!event)
                        return;

                    const key = typeof event.key === 'string' ? event.key.toLowerCase() : '';
                    const keyCode = event.keyCode || event.which || event.detail;
                    if (key === 'enter' || key === ' ' || key === 'spacebar' || keyCode === 13 || keyCode === 32) {
                        event.preventDefault();
                        this.activateSFilterButton(event);
                    }
                };

            if (!button.__smartfilterSFilterBound) {
                button.addEventListener('click', this.sfilterButtonHandler);
                button.addEventListener('hover:enter', this.sfilterButtonHoverHandler);
                button.addEventListener('keydown', this.sfilterButtonKeyHandler);
                button.__smartfilterSFilterBound = true;
            }

            this.sfilterButton = button;
            return button;
        },

        onSFilterButtonClick(event) {
            this.activateSFilterButton(event);
        },

        activateSFilterButton(event) {
            if (event && typeof event.preventDefault === 'function')
                event.preventDefault();

            const ready = Array.isArray(this.sfilterItems) && this.sfilterItems.length > 0;

            if (ready) {
                this.openSFilterModal();
                return;
            }

            this.requestSmartFilterData().then((items) => {
                const hasItems = Array.isArray(items) && items.length > 0;
                const cached = Array.isArray(this.sfilterItems) && this.sfilterItems.length > 0;

                if (hasItems || cached) {
                    this.openSFilterModal();
                    return;
                }

                if (window.Lampa && Lampa.Toast && typeof Lampa.Toast.show === 'function')
                    Lampa.Toast.show('Данные ещё не готовы', 2500);
            }).catch(() => {
                if (window.Lampa && Lampa.Toast && typeof Lampa.Toast.show === 'function')
                    Lampa.Toast.show('Ошибка загрузки данных', 2500);
            });
        },

        buildSmartFilterRequestUrl(forceJson = true) {
            if (!window || !window.location)
                return null;

            const basePath = '/lite/smartfilter';
            let search = '';

            if (typeof window.location.search === 'string' && window.location.search)
                search = window.location.search;

            if (!search && typeof window.location.hash === 'string') {
                const hash = window.location.hash;
                const queryIndex = hash.indexOf('?');
                if (queryIndex !== -1)
                    search = hash.slice(queryIndex);
            }

            const params = [];
            if (search) {
                const query = search.charAt(0) === '?' ? search.slice(1) : search;
                query.split('&').forEach((part) => {
                    if (!part)
                        return;
                    const trimmed = part.trim();
                    if (!trimmed)
                        return;
                    if (forceJson && trimmed.toLowerCase().startsWith('rjson='))
                        return;
                    params.push(trimmed);
                });
            }

            if (forceJson)
                params.push('rjson=true');

            const queryString = params.length ? params.join('&') : '';
            let url = basePath;
            if (queryString)
                url += `?${queryString}`;
            else if (forceJson)
                url += '?rjson=true';

            const prepared = this.prepareRequestUrl(url);
            return typeof prepared === 'string' && prepared ? prepared : url;
        },

        requestSmartFilterData(forceJson = true) {
            const url = this.buildSmartFilterRequestUrl(forceJson);
            if (!url)
                return Promise.resolve([]);

            const handleData = (data) => {
                if (!data)
                    return [];

                const dataset = this.normalizeSeriesDataset(data);
                let items = [];

                if (dataset && Array.isArray(dataset.items))
                    items = dataset.items.slice();

                if (!items.length)
                    items = this.flattenItems(data);

                if (!Array.isArray(items))
                    items = [];

                const container = this.ensureSFilterContainer();
                this.notifySFilterModule(items, {
                    ensureButton: true,
                    container,
                    cachedData: data,
                    cachedItems: items,
                    series: dataset,
                    metadata: data && data.metadata ? data.metadata : this.cachedMetadata
                });

                return items;
            };

            if (typeof window.fetch === 'function') {
                return window.fetch(url, { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
                    .then((response) => {
                        if (!response || !response.ok)
                            throw new Error('Network error');
                        return response.clone().text();
                    })
                    .then((text) => {
                        if (!text)
                            return [];

                        let payload = null;
                        try {
                            payload = JSON.parse(text);
                        } catch (err) {
                            throw err;
                        }

                        return handleData(payload);
                    });
            }

            return new Promise((resolve, reject) => {
                if (typeof XMLHttpRequest !== 'function') {
                    reject(new Error('Transport not available'));
                    return;
                }

                const xhr = new XMLHttpRequest();
                xhr.open('GET', url, true);

                try {
                    xhr.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
                } catch (err) {
                    /* ignore */
                }

                xhr.onreadystatechange = () => {
                    if (xhr.readyState !== 4)
                        return;

                    if (xhr.status >= 200 && xhr.status < 300) {
                        try {
                            const payload = xhr.responseText ? JSON.parse(xhr.responseText) : [];
                            resolve(handleData(payload));
                        } catch (err) {
                            reject(err);
                        }
                    } else {
                        reject(new Error(`Status ${xhr.status}`));
                    }
                };

                xhr.onerror = () => reject(new Error('Network error'));
                xhr.send(null);
            });
        },

        updateSFilterButtonState() {
            const button = this.sfilterButton || this.ensureSFilterButton();
            if (!button)
                return;

            const options = this.collectSFilterOptions();
            const hasVoices = options && Array.isArray(options.voices) && options.voices.length > 0;
            const hasQualities = options && Array.isArray(options.qualities) && options.qualities.length > 0;
            const enabled = hasVoices || hasQualities;

            if (enabled) {
                this.addClass(button, 'enabled');
                button.setAttribute('aria-disabled', 'false');
            } else {
                this.removeClass(button, 'enabled');
                button.setAttribute('aria-disabled', 'true');
            }
        },

        notifySFilterModule(items, options = {}) {
            if (options.container)
                this.sfilterContainer = options.container;
            else
                this.ensureSFilterContainer();

            if (options.reset)
                this.resetSFilterState();

            if (options.cachedData && typeof options.cachedData === 'object')
                this.cachedData = options.cachedData;

            if (Array.isArray(options.cachedItems))
                this.cachedItems = options.cachedItems.slice();

            if (options.metadata && typeof options.metadata === 'object')
                this.cachedMetadata = options.metadata;
            else if (options.reset)
                this.cachedMetadata = null;

            if (options.series === null)
                this.seriesState = null;
            else if (options.series && typeof options.series === 'object')
                this.seriesState = options.series;

            if (Array.isArray(items)) {
                this.sfilterItems = items.filter((item) => item && typeof item === 'object');
                this.clearSFilterFilters();
            } else if (!options.reset) {
                this.sfilterItems = [];
            }

            this.ensureSFilterButton();
            this.updateSFilterButtonState();
        },

        resetSFilterState() {
            this.closeSFilterModal();
            this.sfilterItems = [];
            this.seriesState = null;
            this.clearSFilterFilters();
        },

        clearSFilterFilters() {
            const container = this.ensureSFilterContainer();
            if (!container)
                return;

            this.forEachNode(container.querySelectorAll('.videos__item'), (item) => {
                if (!item)
                    return;

                if (item.style)
                    item.style.display = '';

                if (item.dataset)
                    delete item.dataset.hiddenByFilter;
            });

            this.resetTranslationButtons(container);
            this.syncAllProviderVisibility(container);
        },

        collectSFilterOptions() {
            if (this.cachedMetadata && typeof this.cachedMetadata === 'object') {
                const metaOptions = this.extractOptionsFromMetadata(this.cachedMetadata);
                if (metaOptions)
                    return metaOptions;
            }

            if (this.seriesState && typeof this.seriesState === 'object') {
                const seriesVoices = Array.isArray(this.seriesState.voices) ? this.seriesState.voices.slice() : [];
                const seriesQualities = Array.isArray(this.seriesState.qualities) ? this.seriesState.qualities.slice() : [];

                if (seriesVoices.length || seriesQualities.length)
                    return { voices: seriesVoices, qualities: seriesQualities };
            }

            const options = this.extractOptionsFromMetadata({
                voices: this.buildFacetMapFromItems(this.sfilterItems, 'voice'),
                qualities: this.buildFacetMapFromItems(this.sfilterItems, 'quality')
            });

            return options || { voices: [], qualities: [] };
        },

        extractOptionsFromMetadata(metadata) {
            if (!metadata || typeof metadata !== 'object')
                return null;

            const voices = this.buildFacetArray(metadata.voices || metadata.Voices, 'voice');
            const qualities = this.buildFacetArray(metadata.qualities || metadata.Qualities, 'quality');

            if (!voices.length && !qualities.length)
                return null;

            return { voices, qualities };
        },

        buildFacetMapFromItems(items, kind) {
            const map = {};
            const source = Array.isArray(items) ? items : [];
            source.forEach((item) => {
                if (!item || typeof item !== 'object')
                    return;

                if (kind === 'voice') {
                    const label = this.normalizeVoiceLabel(item.voice_label || item.voice || item.translation || item.details);
                    if (!label)
                        return;
                    const key = this.normalizeFilterText(label) || label;
                    const entry = map[key] || { label, count: 0 };
                    entry.count += 1;
                    map[key] = entry;
                } else if (kind === 'quality') {
                    const label = this.normalizeQualityLabel(item.quality_label || item.quality || item.maxquality);
                    if (!label)
                        return;
                    const key = this.normalizeFilterText(label) || label;
                    const entry = map[key] || { label, count: 0 };
                    entry.count += 1;
                    map[key] = entry;
                }
            });
            return map;
        },

        buildFacetArray(map, type) {
            if (!map || typeof map !== 'object')
                return [];

            const entries = [];
            Object.keys(map).forEach((key) => {
                const facet = map[key];
                if (!facet || typeof facet !== 'object')
                    return;

                const label = this.normalizeText(facet.label || facet.Label);
                if (!label)
                    return;

                const code = this.normalizeText(facet.code || facet.Code);
                const countRaw = facet.count != null ? facet.count : facet.Count;
                const count = Number.isFinite(countRaw) ? Number(countRaw) : Number.parseInt(countRaw, 10);
                entries.push({ code, label, count: Number.isFinite(count) ? count : 0 });
            });

            if (!entries.length)
                return [];

            if (type === 'quality')
                entries.sort((a, b) => this.scoreQualityFacet(b.code || b.label) - this.scoreQualityFacet(a.code || a.label));
            else
                entries.sort((a, b) => a.label.localeCompare(b.label));

            return entries.map((entry) => entry.count > 0 ? `${entry.label} (${entry.count})` : entry.label);
        },

        scoreQualityFacet(value) {
            if (!value)
                return -1;

            const normalized = value.toString().toLowerCase();
            if (normalized.includes('2160'))
                return 6;
            if (normalized.includes('1440'))
                return 5;
            if (normalized.includes('1080'))
                return 4;
            if (normalized.includes('720'))
                return 3;
            if (normalized.includes('480'))
                return 2;
            if (normalized.includes('360'))
                return 1;
            if (normalized.includes('cam'))
                return 0;
            return 0;
        },

        normalizeFilterText(value) {
            if (value === null || value === undefined)
                return '';

            return String(value)
                .trim()
                .replace(/\s+/g, ' ')
                .toLowerCase();
        },

        normalizeQualityLabel(value) {
            if (value === null || value === undefined)
                return '';

            if (Array.isArray(value))
                return this.normalizeQualityLabel(value[0]);

            if (typeof value === 'object')
                return this.normalizeQualityLabel(value.label || value.name || value.title || value.quality || value.maxquality || value.maxQuality);

            const text = String(value).trim();
            if (!text)
                return '';

            const normalized = this.normalizeFilterText(text);
            if (!normalized)
                return '';

            const map = {
                'uhd': '2160p',
                '4k': '2160p',
                '2160p': '2160p',
                '1440p': '1440p',
                '1080p': '1080p',
                'fullhd': '1080p',
                'full hd': '1080p',
                'fhd': '1080p',
                '720p': '720p',
                'hd': '720p',
                'hdrip': 'HDRip',
                '480p': '480p',
                'sd': '480p',
                '360p': '360p',
                'camrip': 'CAMRip',
                'camrip hd': 'CAMRip',
                'cam-rip': 'CAMRip',
                'cam': 'CAMRip',
                'ts': 'TS',
                'telesync': 'TS',
                'dvdrip': 'DVDRip',
                'webrip': 'WEBRip',
                'web-rip': 'WEBRip',
                'webdl': 'WEB-DL',
                'web-dl': 'WEB-DL',
                'bdrip': 'BDRip',
                'bdr': 'BDRip'
            };

            if (Object.prototype.hasOwnProperty.call(map, normalized))
                return map[normalized];

            const match = normalized.match(/(\d{3,4}p)/);
            if (match && match[1])
                return match[1];

            return text;
        },

        normalizeVoiceLabel(value) {
            if (value === null || value === undefined)
                return 'Оригинал';

            if (Array.isArray(value))
                return this.normalizeVoiceLabel(value[0]);

            if (typeof value === 'object')
                return this.normalizeVoiceLabel(value.name || value.title || value.label || value.translate || value.voice || value.voice_name || value.voiceName);

            const text = String(value).trim();
            if (!text)
                return 'Оригинал';

            const normalized = this.normalizeFilterText(text);
            if (normalized === 'original')
                return 'Оригинал';

            return text;
        },

        matchesSFilterPayload(payload, voiceFilters, qualityFilters) {
            if (!payload || typeof payload !== 'object')
                return false;

            const normalizedVoices = Array.isArray(voiceFilters)
                ? voiceFilters.map((voice) => this.normalizeFilterText(voice)).filter(Boolean)
                : [];
            const normalizedQualities = Array.isArray(qualityFilters)
                ? qualityFilters.map((quality) => this.normalizeFilterText(quality)).filter(Boolean)
                : [];

            const voiceValue = payload.smartfilterVoice || payload.translate || payload.voice || payload.voice_name || payload.voiceName || payload.translation || payload.dub;
            const voiceLabel = this.normalizeVoiceLabel(voiceValue);
            const normalizedVoice = this.normalizeFilterText(voiceLabel);
            const hasVoiceFilters = normalizedVoices.length > 0;
            const voiceMatch = !hasVoiceFilters || normalizedVoices.includes(normalizedVoice);

            const qualityValue = payload.smartfilterQuality || payload.maxquality || payload.maxQuality || payload.quality || payload.quality_label || payload.qualityName || payload.video_quality || payload.source_quality || payload.hd;
            const qualityLabel = this.normalizeQualityLabel(qualityValue);
            const normalizedQuality = this.normalizeFilterText(qualityLabel);
            const hasQualityFilters = normalizedQualities.length > 0;
            const qualityMatch = !hasQualityFilters || !normalizedQuality || normalizedQualities.includes(normalizedQuality);

            return voiceMatch && qualityMatch;
        },

        resetTranslationButtons(root) {
            if (!root || typeof root.querySelectorAll !== 'function')
                return;

            this.forEachNode(this.getTranslationButtons(root), (element) => {
                if (!element)
                    return;

                if (element.style)
                    element.style.display = '';

                if (element.dataset)
                    delete element.dataset.hiddenByFilter;
            });
        },

        updateTranslationButtonsVisibility(root, normalizedVoices) {
            if (!root || typeof root.querySelectorAll !== 'function')
                return;

            const allowed = Array.isArray(normalizedVoices)
                ? normalizedVoices.filter(Boolean)
                : [];
            const allowedSet = new Set(allowed);
            const hasFilter = allowedSet.size > 0;

            this.forEachNode(this.getTranslationButtons(root), (element) => {
                if (!element)
                    return;

                const dataset = element.dataset || {};
                const labelSource = dataset.translate || dataset.voice || dataset.voiceName || dataset.voice_name || dataset.name || element.textContent;
                const voiceLabel = this.normalizeVoiceLabel(labelSource);
                const normalized = this.normalizeFilterText(voiceLabel);
                const alwaysVisible = normalized === 'все' || normalized === 'all';
                const visible = alwaysVisible || !hasFilter || allowedSet.has(normalized);

                if (element.style)
                    element.style.display = visible ? '' : 'none';

                if (element.dataset) {
                    if (visible)
                        delete element.dataset.hiddenByFilter;
                    else
                        element.dataset.hiddenByFilter = 'true';
                }
            });
        },

        getTranslationButtons(root) {
            if (!root || typeof root.querySelectorAll !== 'function')
                return [];

            const selectors = [
                '[data-translate]',
                '[data-voice]',
                '[data-voice-name]',
                '[data-voice_name]',
                '[data-voicename]',
                '[data-voiceName]',
                '.selector[data-name]',
                '.filter__item[data-name]',
                '.translations__item[data-name]'
            ];

            return root.querySelectorAll(selectors.join(','));
        },

        openSFilterModal() {
            const { voices, qualities } = this.collectSFilterOptions();
            if (!voices.length && !qualities.length)
                return;

            const modal = document.createElement('div');
            modal.className = 'smartfilter-modal';
            modal.innerHTML = `
                <div class="smartfilter-modal__content">
                    <div style="display:flex;justify-content:space-between;align-items:center;">
                        <h2 style="margin:0">SmartFilter</h2>
                        <button class="simple-button selector" id="smartfilter-modal-close">Закрыть окно</button>
                    </div>
                    <div class="smartfilter-modal__section">
                        <h3>Озвучки</h3>
                        <div>${voices.map((voice) => this.createSFilterChip('voice', voice)).join('')}</div>
                    </div>
                    <div class="smartfilter-modal__section">
                        <h3>Качество</h3>
                        <div>${qualities.map((quality) => this.createSFilterChip('quality', quality)).join('')}</div>
                    </div>
                    <div style="margin-top:20px;display:flex;justify-content:flex-end;gap:10px;">
                        <button class="simple-button selector" id="smartfilter-reset">Сбросить</button>
                        <button class="simple-button selector" id="smartfilter-apply">Применить</button>
                    </div>
                </div>`;

            document.body.appendChild(modal);
            this.sfilterModal = modal;

            const closeModal = () => this.closeSFilterModal();

            const closeButton = modal.querySelector('#smartfilter-modal-close');
            if (closeButton)
                closeButton.addEventListener('click', closeModal);

            const resetButton = modal.querySelector('#smartfilter-reset');
            if (resetButton)
                resetButton.addEventListener('click', () => {
                    this.forEachNode(modal.querySelectorAll('.smartfilter-chip'), (chip) => this.removeClass(chip, 'active'));
                });

            const applyButton = modal.querySelector('#smartfilter-apply');
            if (applyButton)
                applyButton.addEventListener('click', () => {
                    const selectedVoices = this.getSFilterSelectedValues(modal, 'voice');
                    const selectedQualities = this.getSFilterSelectedValues(modal, 'quality');
                    this.applySFilterFilters(selectedVoices, selectedQualities);
                    closeModal();
                });

            this.forEachNode(modal.querySelectorAll('.smartfilter-chip'), (chip) => {
                chip.addEventListener('click', () => this.toggleClass(chip, 'active'));
            });

            this.sfilterKeyHandler = (event) => {
                if (!this.isBackNavigation(event))
                    return;

                event.preventDefault();
                event.stopPropagation();
                closeModal();
            };

            window.addEventListener('keydown', this.sfilterKeyHandler, true);

            this.sfilterBackHandler = () => closeModal();
            document.addEventListener('backbutton', this.sfilterBackHandler, true);

            this.setupSFilterController(modal);
        },

        closeSFilterModal() {
            if (!this.sfilterModal)
                return;

            if (this.sfilterKeyHandler)
                window.removeEventListener('keydown', this.sfilterKeyHandler, true);

            if (this.sfilterBackHandler)
                document.removeEventListener('backbutton', this.sfilterBackHandler, true);

            this.sfilterKeyHandler = null;
            this.sfilterBackHandler = null;

            this.teardownSFilterController();

            if (this.sfilterModal.parentElement)
                this.sfilterModal.remove();

            this.sfilterModal = null;
        },

        setupSFilterController(modal) {
            if (!modal || !window.Lampa || !Lampa.Controller || typeof Lampa.Controller.add !== 'function')
                return;

            const enabled = typeof Lampa.Controller.enabled === 'function' ? Lampa.Controller.enabled() : null;
            this.sfilterPrevController = enabled && enabled.name ? enabled.name : null;

            Lampa.Controller.add(this.sfilterControllerName, {
                toggle: () => {
                    if (typeof Lampa.Controller.collectionSet === 'function')
                        Lampa.Controller.collectionSet(modal);

                    const target = modal.querySelector('.smartfilter-chip.active')
                        || modal.querySelector('#smartfilter-apply')
                        || modal.querySelector('#smartfilter-modal-close');

                    if (target && typeof Lampa.Controller.collectionFocus === 'function')
                        Lampa.Controller.collectionFocus(target, modal);
                },
                back: () => this.closeSFilterModal()
            });

            if (typeof Lampa.Controller.toggle === 'function')
                Lampa.Controller.toggle(this.sfilterControllerName);
        },

        teardownSFilterController() {
            if (!window.Lampa || !Lampa.Controller)
                return;

            if (typeof Lampa.Controller.remove === 'function')
                Lampa.Controller.remove(this.sfilterControllerName);

            if (this.sfilterPrevController && typeof Lampa.Controller.toggle === 'function')
                Lampa.Controller.toggle(this.sfilterPrevController);

            this.sfilterPrevController = null;
        },

        getSFilterSelectedValues(modal, type) {
            const selected = [];
            this.forEachNode(modal.querySelectorAll('.smartfilter-chip[data-type="' + type + '"].active'), (chip) => {
                const value = chip.getAttribute('data-value');
                if (value)
                    selected.push(value);
            });
            return selected;
        },

        applySFilterFilters(voices, qualities) {
            const container = this.ensureSFilterContainer();
            if (!container)
                return;

            const voiceList = Array.isArray(voices)
                ? voices.map((voice) => this.normalizeFilterText(voice)).filter(Boolean)
                : [];
            const qualityList = Array.isArray(qualities)
                ? qualities.map((quality) => this.normalizeFilterText(quality)).filter(Boolean)
                : [];

            const hasFolders = !!container.querySelector('[data-folder="true"][data-provider]');

            if (!hasFolders) {
                this.forEachNode(container.querySelectorAll('.videos__item'), (item) => {
                    if (!item)
                        return;

                    item.style.display = '';

                    const dataJson = item.getAttribute('data-json');
                    if (!dataJson)
                        return;

                    try {
                        const payload = JSON.parse(dataJson);
                        if (!payload)
                            return;

                        if ((payload.method || '').toString().toLowerCase() === 'folder')
                            return;

                        const matches = this.matchesSFilterPayload(payload, voiceList, qualityList);
                        if (!matches)
                            item.style.display = 'none';
                        else
                            item.style.display = '';
                    } catch (err) {
                        /* ignore */
                    }
                });
                this.updateTranslationButtonsVisibility(container, voiceList);
                return;
            }

            this.forEachNode(container.querySelectorAll('.videos__item'), (item) => {
                if (!item || !item.dataset)
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

                const matches = this.matchesSFilterPayload(payload, voiceList, qualityList);

                if (!matches)
                    item.dataset.hiddenByFilter = 'true';
                else
                    delete item.dataset.hiddenByFilter;
            });

            this.syncAllProviderVisibility(container);
            this.updateTranslationButtonsVisibility(container, voiceList);
        },

        createSFilterChip(type, value) {
            const safeValue = value !== null && value !== undefined ? String(value) : '';
            return `<button type="button" class="smartfilter-chip" data-type="${type}" data-value="${this.escapeHtml(safeValue)}">${this.escapeHtml(safeValue)}</button>`;
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

                const args = Array.prototype.slice.call(arguments);
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

        hookFetch() {
            if (!window || typeof window.fetch !== 'function' || this.originalFetch)
                return;

            this.originalFetch = window.fetch;

            window.fetch = function (input, init) {
                let request = input;
                let url = '';
                const hasRequest = typeof Request === 'function';

                if (typeof input === 'string')
                    url = input;
                else if (input && typeof input === 'object')
                    url = typeof input.url === 'string' ? input.url : (hasRequest && input instanceof Request ? input.url : '');

                let preparedUrl = typeof url === 'string' && url ? SmartFilter.prepareRequestUrl(url) : url;

                if (typeof input === 'string')
                    request = preparedUrl;
                else if (hasRequest && input instanceof Request && preparedUrl && preparedUrl !== url) {
                    try {
                        const requestInit = init ? Object.assign({}, init) : {};
                        if (!requestInit.method)
                            requestInit.method = input.method;
                        if (!requestInit.headers)
                            requestInit.headers = input.headers;
                        if (!Object.prototype.hasOwnProperty.call(requestInit, 'credentials'))
                            requestInit.credentials = input.credentials;
                        if (!Object.prototype.hasOwnProperty.call(requestInit, 'cache'))
                            requestInit.cache = input.cache;
                        if (!Object.prototype.hasOwnProperty.call(requestInit, 'mode'))
                            requestInit.mode = input.mode;
                        if (!Object.prototype.hasOwnProperty.call(requestInit, 'redirect'))
                            requestInit.redirect = input.redirect;
                        if (!Object.prototype.hasOwnProperty.call(requestInit, 'referrer'))
                            requestInit.referrer = input.referrer;
                        if (!Object.prototype.hasOwnProperty.call(requestInit, 'integrity'))
                            requestInit.integrity = input.integrity;
                        if (!Object.prototype.hasOwnProperty.call(requestInit, 'keepalive'))
                            requestInit.keepalive = input.keepalive;
                        if (!Object.prototype.hasOwnProperty.call(requestInit, 'signal'))
                            requestInit.signal = input.signal;
                        request = new Request(preparedUrl, requestInit);
                    } catch (err) {
                        request = preparedUrl;
                    }
                } else if (preparedUrl && preparedUrl !== url) {
                    request = preparedUrl;
                }

                const finalUrl = typeof preparedUrl === 'string' && preparedUrl ? preparedUrl : url;
                const isTarget = SmartFilter.isSmartFilterRequest(finalUrl);

                if (isTarget)
                    SmartFilter.handleFetchStart(finalUrl);

                const fetchPromise = SmartFilter.originalFetch.call(this, request, init);

                return fetchPromise.then((response) => {
                    if (!response)
                        return response;

                    const cloneForMetadata = typeof response.clone === 'function' ? response.clone() : null;
                    const cloneForData = typeof response.clone === 'function' ? response.clone() : null;

                    if (cloneForMetadata)
                        SmartFilter.captureFetchMetadata(cloneForMetadata);

                    if (isTarget && cloneForData)
                        return SmartFilter.handleFetchComplete(finalUrl, cloneForData).then(() => response);

                    if (isTarget)
                        return SmartFilter.handleFetchComplete(finalUrl, null).then(() => response);

                    return response;
                }).catch((error) => {
                    if (isTarget)
                        SmartFilter.handleFetchError();
                    throw error;
                });
            };
        },

        isSmartFilterProgressRequest(url) {
            if (typeof url !== 'string')
                return false;

            return url.indexOf('/lite/smartfilter/progress') !== -1;
        },

        isSmartFilterRequest(url) {
            if (typeof url !== 'string')
                return false;

            if (this.isSmartFilterProgressRequest(url))
                return false;

            return url.indexOf('/lite/smartfilter') !== -1;
        },

        handleRequestStart(xhr) {
            if (this.isSmartFilterProgressRequest(xhr.__smartfilter_url))
                return;

            const info = this.parseRequestUrl(xhr.__smartfilter_url);
            this.progressHost = info.origin;
            this.progressKey = info.progressKey;
            this.cachedData = null;
            this.cachedItems = null;
            this.cachedMetadata = null;
            this.lastProgressState = null;
            this.progressReady = false;
            this.cancelAutoClose();
            this.hideProgress(true);
            this.notifySFilterModule([], { reset: true, ensureButton: true, container: document.querySelector('[data-smartfilter="true"]') });
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

                const flattened = this.flattenItems(data.results || data);
                this.cachedData = data;
                this.cachedItems = flattened;
                this.cachedMetadata = data.metadata || null;
                const container = document.querySelector('[data-smartfilter="true"]');
                this.notifySFilterModule(flattened, { ensureButton: true, container, metadata: this.cachedMetadata });
            } catch (err) {
                this.cachedData = null;
                this.cachedItems = null;
                this.cachedMetadata = null;
                this.notifySFilterModule([], { reset: true, ensureButton: true, container: document.querySelector('[data-smartfilter="true"]') });
            }
        },

        handleFetchStart(url) {
            if (this.isSmartFilterProgressRequest(url))
                return;

            const info = this.parseRequestUrl(url);
            this.progressHost = info.origin;
            this.progressKey = info.progressKey;
            this.cachedData = null;
            this.cachedItems = null;
            this.lastProgressState = null;
            this.progressReady = false;
            this.cancelAutoClose();
            this.hideProgress(true);
            this.notifySFilterModule([], { reset: true, ensureButton: true, container: document.querySelector('[data-smartfilter="true"]') });
            this.startProgress();
        },

        handleFetchComplete(url, response) {
            this.stopProgress();

            if (!this.lastProgressState || !this.lastProgressState.ready)
                this.hideProgress(true);

            if (!response)
                return Promise.resolve();

            return response.text().then((text) => {
                if (!text)
                    return;

                const trimmed = text.trim();
                if (!trimmed || (trimmed[0] !== '{' && trimmed[0] !== '['))
                    return;

                let data = null;
                try {
                    data = JSON.parse(trimmed);
                } catch (err) {
                    data = null;
                }

                if (!data)
                    return;

                const flattened = this.flattenItems(data.results || data);
                this.cachedData = data;
                this.cachedItems = flattened;
                this.cachedMetadata = data.metadata || null;
                const container = document.querySelector('[data-smartfilter="true"]');
                this.notifySFilterModule(flattened, { ensureButton: true, container, cachedData: data, cachedItems: flattened, metadata: this.cachedMetadata });
            }).catch(() => {
                this.cachedData = null;
                this.cachedItems = null;
                this.cachedMetadata = null;
                const container = document.querySelector('[data-smartfilter="true"]');
                this.notifySFilterModule([], { reset: true, ensureButton: true, container });
            });
        },

        handleFetchError() {
            this.stopProgress();
            this.hideProgress(true);
            this.cachedData = null;
            this.cachedItems = null;
            this.cachedMetadata = null;
            const container = document.querySelector('[data-smartfilter="true"]');
            this.notifySFilterModule([], { reset: true, ensureButton: true, container });
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

        normalizeSeriesDataset(payload) {
            const container = this.extractSeriesContainer(payload);
            if (!container)
                return null;

            const dataset = {
                hasSeries: false,
                items: [],
                seasons: [],
                episodes: [],
                grouped: null,
                voices: [],
                qualities: [],
                maxQuality: null
            };

            const normalizedSeasons = [];
            const seasonSeenGlobal = Object.create(null);
            const groupedSeasons = typeof container.groupedSeasons === 'object' && container.groupedSeasons ? container.groupedSeasons : null;

            if (groupedSeasons) {
                const groups = {};
                Object.keys(groupedSeasons).forEach((key) => {
                    const label = typeof key === 'string' && key ? key : 'default';
                    const list = groupedSeasons[key];
                    if (!Array.isArray(list) || !list.length)
                        return;

                    const normalizedGroup = [];
                    list.forEach((item) => {
                        const normalized = this.normalizeSeriesItem(item);
                        const uniqueKey = this.buildSeriesItemKey(normalized);
                        if (normalized && !seasonSeenGlobal[uniqueKey]) {
                            seasonSeenGlobal[uniqueKey] = true;
                            normalizedGroup.push(normalized);
                        }
                    });

                    if (normalizedGroup.length)
                        groups[label] = normalizedGroup;
                });

                const groupKeys = Object.keys(groups);
                if (groupKeys.length) {
                    dataset.grouped = groups;
                    groupKeys.forEach((key) => {
                        const entries = groups[key];
                        for (let index = 0; index < entries.length; index += 1)
                            normalizedSeasons.push(entries[index]);
                    });
                }
            }

            const seasonList = Array.isArray(container.seasons) ? container.seasons : null;
            if (seasonList && seasonList.length) {
                seasonList.forEach((item) => {
                    const normalized = this.normalizeSeriesItem(item);
                    if (!normalized)
                        return;

                    const uniqueKey = this.buildSeriesItemKey(normalized);
                    if (seasonSeenGlobal[uniqueKey])
                        return;

                    seasonSeenGlobal[uniqueKey] = true;

                    normalizedSeasons.push(normalized);
                });
            }

            const normalizedEpisodes = [];
            const episodeList = Array.isArray(container.episodes) ? container.episodes : null;
            if (episodeList && episodeList.length) {
                const episodeSeen = Object.create(null);
                episodeList.forEach((item) => {
                    const normalized = this.normalizeSeriesItem(item);
                    if (!normalized)
                        return;

                    const uniqueKey = this.buildSeriesItemKey(normalized);
                    if (episodeSeen[uniqueKey])
                        return;

                    episodeSeen[uniqueKey] = true;
                    normalizedEpisodes.push(normalized);
                });
            }

            dataset.seasons = normalizedSeasons;
            dataset.episodes = normalizedEpisodes;
            dataset.items = normalizedSeasons.concat(normalizedEpisodes);
            dataset.hasSeries = dataset.items.length > 0;

            const sourceMaxQuality = container.maxquality || container.maxQuality || payload.maxquality || payload.maxQuality;
            if (sourceMaxQuality)
                dataset.maxQuality = sourceMaxQuality;

            dataset.voices = this.collectSeriesVoices(dataset.items, container.voice || container.voices || container.voice_list || container.translations || payload.voice || payload.voices || payload.voice_list || payload.translations);
            dataset.qualities = this.collectSeriesQualities(dataset.items, dataset.maxQuality, payload);

            if (Array.isArray(dataset.voices))
                dataset.voices.sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' }));

            dataset.qualities = this.sortQualityOptions(dataset.qualities);

            return dataset;
        },

        extractSeriesContainer(payload) {
            if (!payload || typeof payload !== 'object')
                return null;

            const stack = [payload];
            const seen = typeof WeakSet === 'function' ? new WeakSet() : null;

            while (stack.length) {
                const current = stack.pop();
                if (!current || typeof current !== 'object')
                    continue;

                if (seen) {
                    if (seen.has(current))
                        continue;
                    seen.add(current);
                }

                if (Array.isArray(current)) {
                    for (let index = 0; index < current.length; index += 1)
                        stack.push(current[index]);
                    continue;
                }

                const seasons = current.seasons || current.Seasons;
                const episodes = current.episodes || current.Episodes;
                const grouped = current.groupedSeasons || current.grouped || current.providers;

                if ((Array.isArray(seasons) && seasons.length) || (Array.isArray(episodes) && episodes.length) || (grouped && typeof grouped === 'object' && Object.keys(grouped).length))
                    return current;

                const keys = ['data', 'results', 'items', 'playlist', 'playlists', 'children', 'list', 'series', 'content'];
                for (let keyIndex = 0; keyIndex < keys.length; keyIndex += 1) {
                    const key = keys[keyIndex];
                    if (current[key] !== undefined)
                        stack.push(current[key]);
                }
            }

            return null;
        },

        normalizeSeriesItem(source) {
            if (!source || typeof source !== 'object')
                return null;

            const item = Object.assign({}, source);

            if (!item.type) {
                if (item.episode !== undefined || item.e !== undefined)
                    item.type = 'episode';
                else
                    item.type = 'season';
            }

            if (!item.title && item.name)
                item.title = item.name;

            if (!item.voice && item.translate)
                item.voice = item.translate;

            const qualityCandidate = Array.isArray(item.quality) ? item.quality[0] : (item.quality || item.maxquality || item.maxQuality);
            const normalizedQuality = this.normalizeQualityLabel(qualityCandidate);
            if (normalizedQuality && !item.maxquality)
                item.maxquality = normalizedQuality;
            if (normalizedQuality)
                item.smartfilterQuality = normalizedQuality;

            const voiceLabel = this.normalizeVoiceLabel(item.voice || item.translate || item.voice_name || item.voiceName);
            if (voiceLabel)
                item.smartfilterVoice = voiceLabel;

            return item;
        },

        buildSeriesItemKey(item) {
            if (!item || typeof item !== 'object')
                return '';

            const parts = [];

            if (item.provider)
                parts.push(String(item.provider).toLowerCase());

            if (item.type)
                parts.push(String(item.type).toLowerCase());

            if (item.season !== undefined)
                parts.push(`s${item.season}`);

            if (item.episode !== undefined)
                parts.push(`e${item.episode}`);

            const voice = item.smartfilterVoice || item.voice || item.translate || item.voice_name || item.voiceName;
            if (voice)
                parts.push(this.normalizeFilterText(voice));

            if (item.url)
                parts.push(String(item.url).toLowerCase());

            return parts.join('|');
        },

        collectSeriesVoices(items, voiceSource) {
            const voices = [];
            const seen = Object.create(null);

            const pushVoice = (value) => {
                const label = this.normalizeVoiceLabel(value);
                const normalized = this.normalizeFilterText(label);
                if (!normalized || seen[normalized])
                    return;
                seen[normalized] = true;
                voices.push(label);
            };

            const visit = (value) => {
                if (value === null || value === undefined)
                    return;

                if (Array.isArray(value)) {
                    value.forEach(visit);
                    return;
                }

                if (typeof value === 'object') {
                    pushVoice(value.name || value.title || value.label || value.translate || value.voice || value.voice_name || value.voiceName);
                    if (Array.isArray(value.list))
                        value.list.forEach(visit);
                    if (Array.isArray(value.items))
                        value.items.forEach(visit);
                    return;
                }

                pushVoice(value);
            };

            if (voiceSource !== undefined)
                visit(voiceSource);

            (Array.isArray(items) ? items : []).forEach((item) => {
                if (!item || typeof item !== 'object')
                    return;
                pushVoice(item.smartfilterVoice || item.voice || item.translate || item.voice_name || item.voiceName);
            });

            return voices;
        },

        collectSeriesQualities(items, maxQuality, payload) {
            const qualities = [];
            const seen = Object.create(null);

            const pushQuality = (value) => {
                const label = this.normalizeQualityLabel(value);
                const normalized = this.normalizeFilterText(label);
                if (!normalized || seen[normalized])
                    return;
                seen[normalized] = true;
                qualities.push(label);
            };

            const visit = (value) => {
                if (value === null || value === undefined)
                    return;

                if (Array.isArray(value)) {
                    value.forEach(visit);
                    return;
                }

                if (typeof value === 'object') {
                    visit(value.maxquality || value.maxQuality || value.quality || value.label || value.name);
                    if (Array.isArray(value.list))
                        value.list.forEach(visit);
                    if (Array.isArray(value.items))
                        value.items.forEach(visit);
                    return;
                }

                pushQuality(value);
            };

            if (maxQuality)
                pushQuality(maxQuality);

            (Array.isArray(items) ? items : []).forEach((item) => {
                if (!item || typeof item !== 'object')
                    return;
                visit(item.smartfilterQuality || item.maxquality || item.maxQuality || item.quality);
            });

            if (payload && typeof payload === 'object')
                visit(payload.quality || payload.qualities || payload.maxquality || payload.maxQuality);

            return qualities;
        },

        sortQualityOptions(list) {
            if (!Array.isArray(list))
                return [];

            const priorities = {
                '2160p': 90,
                '1440p': 80,
                '1080p': 70,
                'web-dl': 65,
                'webrip': 60,
                'hdrip': 55,
                '720p': 50,
                'web': 45,
                '480p': 40,
                'sd': 35,
                '360p': 30,
                'camrip': 20,
                'ts': 10
            };

            const items = list.slice();
            items.sort((a, b) => {
                const normalizedA = this.normalizeFilterText(a);
                const normalizedB = this.normalizeFilterText(b);
                const scoreA = Object.prototype.hasOwnProperty.call(priorities, normalizedA) ? priorities[normalizedA] : 0;
                const scoreB = Object.prototype.hasOwnProperty.call(priorities, normalizedB) ? priorities[normalizedB] : 0;

                if (scoreA === scoreB)
                    return a.localeCompare(b, undefined, { sensitivity: 'base' });

                return scoreB - scoreA;
            });

            return items;
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

        captureFetchMetadata(response) {
            if (!response || typeof response.text !== 'function')
                return Promise.resolve();

            return response.text().then((text) => {
                if (!text)
                    return;

                const trimmed = text.trim();
                if (!trimmed || (trimmed[0] !== '{' && trimmed[0] !== '['))
                    return;

                let payload = null;
                try {
                    payload = JSON.parse(trimmed);
                } catch (err) {
                    payload = null;
                }

                if (!payload)
                    return;

                const meta = this.extractMetadata(payload);
                if (meta)
                    this.updateMetadata(meta);
            }).catch(() => {});
        },

        extractMetadata(payload) {
            if (!payload || typeof payload !== 'object')
                return null;

            const meta = {};
            const stack = [payload];
            const seen = new WeakSet();
            const maxNodes = Number.isFinite(this.metadataScanLimit) && this.metadataScanLimit > 0
                ? this.metadataScanLimit
                : 2000;
            const arrayLimit = Number.isFinite(this.metadataArrayLimit) && this.metadataArrayLimit > 0
                ? this.metadataArrayLimit
                : 200;
            let processed = 0;

            while (stack.length && processed < maxNodes) {
                const current = stack.pop();
                if (!current || typeof current !== 'object')
                    continue;

                if (seen.has(current))
                    continue;

                seen.add(current);
                processed += 1;

                if (this.isMetadataComplete(meta))
                    break;

                if (Array.isArray(current)) {
                    const length = Math.min(current.length, arrayLimit);
                    for (let index = 0; index < length; index += 1)
                        stack.push(current[index]);
                    continue;
                }

                const entries = Object.entries(current);
                for (let entryIndex = 0; entryIndex < entries.length; entryIndex += 1) {
                    const key = entries[entryIndex][0];
                    const value = entries[entryIndex][1];
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
                        if (textValue && !meta.title)
                            meta.title = textValue;
                    } else if (lower === 'original_title' || lower === 'originalname' || lower === 'original_name' || lower === 'orig_title') {
                        const original = this.normalizeText(value);
                        if (original && !meta.original_title)
                            meta.original_title = original;
                    } else if (lower === 'year') {
                        const year = this.parsePositiveInt(value, 4);
                        if (year && !meta.year)
                            meta.year = year;
                    } else if (lower === 'release_date' || lower === 'first_air_date' || lower === 'air_date' || lower === 'premiere_ru' || lower === 'premiere_world') {
                        const parsedYear = this.parseYearFromDate(value);
                        if (parsedYear && !meta.year)
                            meta.year = parsedYear;
                    } else if (lower === 'is_serial' || lower === 'serial' || lower === 'season_count' || lower === 'seasons') {
                        const serial = this.parseSerialFlag(lower, value);
                        if (serial !== null)
                            meta.serial = serial;
                    } else if (lower === 'type' || lower === 'content_type' || lower === 'category') {
                        const serial = this.parseSerialFromType(value);
                        if (serial !== null)
                            meta.serial = serial;
                    }

                    if (this.isMetadataComplete(meta))
                        break;

                    if (value && typeof value === 'object')
                        stack.push(value);
                }
            }

            if (!Object.keys(meta).length)
                return null;

            return meta;
        },

        isMetadataComplete(meta) {
            if (!meta || typeof meta !== 'object')
                return false;

            const hasId = Boolean(meta.kinopoisk_id || meta.imdb_id);
            return hasId && Boolean(meta.title) && Boolean(meta.year);
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
                this.requestJson(url, (data) => {
                    if (!data)
                        return;

                    this.renderProgress(data);
                    if (data.ready)
                        this.stopProgress();
                }, () => { });
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

        ensureProgressStructure(container) {
            if (!container)
                return null;

            if (container.__smartfilterStructure)
                return container.__smartfilterStructure;

            container.innerHTML = `
                <div class="smartfilter-progress__header">
                    <div class="smartfilter-progress__loader"></div>
                    <div class="smartfilter-progress__titles">
                        <div class="smartfilter-progress__title">SmartFilter</div>
                        <div class="smartfilter-progress__subtitle" data-smartfilter-subtitle></div>
                    </div>
                </div>
                <div class="smartfilter-progress__stats">
                    <span data-smartfilter-total></span>
                    <span data-smartfilter-progress></span>
                    <span data-smartfilter-items></span>
                </div>
                <div class="smartfilter-progress__bar">
                    <div class="smartfilter-progress__bar-inner" data-smartfilter-bar></div>
                </div>
                <div class="smartfilter-progress__providers" data-smartfilter-providers></div>
                <div class="smartfilter-progress__hint" data-smartfilter-hint></div>
            `;

            const refs = {
                loader: container.querySelector('.smartfilter-progress__loader'),
                subtitle: container.querySelector('[data-smartfilter-subtitle]'),
                total: container.querySelector('[data-smartfilter-total]'),
                progress: container.querySelector('[data-smartfilter-progress]'),
                items: container.querySelector('[data-smartfilter-items]'),
                bar: container.querySelector('[data-smartfilter-bar]'),
                providers: container.querySelector('[data-smartfilter-providers]'),
                hint: container.querySelector('[data-smartfilter-hint]')
            };

            container.__smartfilterStructure = refs;
            return refs;
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

            const refs = this.ensureProgressStructure(container);

            this.removeClass(container, 'smartfilter-progress--closing');
            if (this.legacyMode)
                this.addClass(container, 'smartfilter-progress--legacy');
            else
                this.removeClass(container, 'smartfilter-progress--legacy');

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
                const itemsCount = provider.items != null ? provider.items : (provider.Items != null ? provider.Items : 0);
                const responseTime = provider.responseTime != null
                    ? provider.responseTime
                    : (provider.ResponseTime != null ? provider.ResponseTime : 0);
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

            if (refs.loader) {
                refs.loader.className = 'smartfilter-progress__loader';
                this.toggleClass(refs.loader, 'smartfilter-progress__loader--success', ready);
            }

            if (refs.subtitle)
                refs.subtitle.textContent = summarySubtitle;

            if (refs.total)
                refs.total.textContent = `Источников: ${total}`;

            if (refs.progress)
                refs.progress.textContent = `Готовность: ${progressDisplay}%`;

            if (refs.items)
                refs.items.textContent = `Ссылок: ${items}`;

            if (refs.bar)
                refs.bar.style.width = `${progressValue}%`;

            if (refs.providers)
                refs.providers.innerHTML = providersHtml;

            if (refs.hint)
                refs.hint.textContent = hint;

            if (this.legacyMode) {
                container.style.opacity = '1';
                container.style.display = 'block';
                container.style.transform = 'translate(-50%, -50%)';
                if (window.jQuery)
                    window.jQuery(container).stop(true, true).fadeIn(120);
            }

            this.toggleClass(container, 'smartfilter-progress--ready', ready);

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
                if (this.legacyMode && window.jQuery) {
                    window.jQuery(container).stop(true, true).fadeOut(160, () => {
                        if (container.parentElement)
                            container.remove();
                    });
                } else {
                    this.addClass(container, 'smartfilter-progress--closing');
                    setTimeout(() => {
                        if (container.parentElement)
                            container.remove();
                    }, 250);
                }
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
                this.toggleClass(target, 'smartfilter-expanded', expand);

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

        isBackNavigation(event) {
            if (!event)
                return false;

            const key = typeof event.key === 'string' ? event.key.toLowerCase() : '';
            if (['back', 'backspace', 'escape', 'esc', 'browserback'].includes(key))
                return true;

            const keyCode = event.keyCode || event.which || event.detail;
            return [8, 27, 461, 10009, 166].includes(keyCode);
        },

    };

    if (document)
        SmartFilter.init();

    if (typeof module !== 'undefined' && module.exports)
        module.exports = SmartFilter;

    if (window && typeof window === 'object')
        window.SmartFilter = SmartFilter;
})(typeof window !== 'undefined' ? window : (typeof globalThis !== 'undefined' ? globalThis : this));
