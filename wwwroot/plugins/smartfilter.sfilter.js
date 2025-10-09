(function () {
    'use strict';

    const SFilterUI = {
        smartfilter: null,
        button: null,
        buttonClickHandler: null,
        items: [],
        container: null,
        modal: null,
        keyHandler: null,
        backHandler: null,
        previousController: null,
        controllerName: 'smartfilter-modal',

        init(smartfilter) {
            if (smartfilter && this.smartfilter !== smartfilter)
                this.smartfilter = smartfilter;

            this.ensureButton();
        },

        ensureButton() {
            const filterBlock = document.querySelector('.filter--filter');
            if (!filterBlock || !filterBlock.parentElement)
                return;

            const parent = filterBlock.parentElement;
            let button = parent.querySelector('.smartfilter-sfilter-button');

            if (!this.buttonClickHandler)
                this.buttonClickHandler = this.onButtonClick.bind(this);

            if (!button) {
                button = document.createElement('div');
                button.className = 'simple-button simple-button--filter selector smartfilter-sfilter-button';
                button.innerHTML = '<span>SFilter</span>';
                parent.insertBefore(button, filterBlock.nextSibling);
            }

            if (!button.__smartfilterSFilterBound) {
                button.addEventListener('click', this.buttonClickHandler);
                button.__smartfilterSFilterBound = true;
            }

            this.button = button;
            this.updateButtonState();
        },

        onButtonClick() {
            this.openModal();
        },

        ensureContainer() {
            if (this.container && document.contains(this.container))
                return this.container;

            const element = document.querySelector('[data-smartfilter="true"]');
            if (element)
                this.container = element;

            return this.container;
        },

        updateData(items, container) {
            if (Array.isArray(items))
                this.items = items.filter((item) => item && typeof item === 'object');
            else
                this.items = [];

            if (container)
                this.container = container;
            else
                this.ensureContainer();

            this.ensureButton();
            this.clearFilterState();
            this.updateButtonState();
        },

        reset() {
            this.clearFilterState();
            this.items = [];
            this.updateButtonState();
        },

        updateButtonState() {
            if (!this.button)
                return;

            if (this.items.length > 0)
                this.addClass(this.button, 'enabled');
            else
                this.removeClass(this.button, 'enabled');
        },

        clearFilterState() {
            const container = this.ensureContainer();
            if (!container)
                return;

            this.forEachNode(container.querySelectorAll('.videos__item'), (item) => {
                if (item && item.style)
                    item.style.display = '';

                if (item && item.dataset)
                    delete item.dataset.hiddenByFilter;
            });

            if (this.smartfilter && typeof this.smartfilter.syncAllProviderVisibility === 'function')
                this.smartfilter.syncAllProviderVisibility(container);
        },

        openModal() {
            if (!this.items || !this.items.length) {
                if (window.Lampa && Lampa.Toast)
                    Lampa.Toast.show('Данные еще загружаются', 2500);
                return;
            }

            const voiceSeen = Object.create(null);
            const voices = [];
            const qualitySeen = Object.create(null);
            const qualities = [];

            this.items.forEach((item) => {
                if (!item || typeof item !== 'object')
                    return;

                const translateSource = item.translate || item.voice || 'Оригинал';
                const translate = translateSource !== null && translateSource !== undefined
                    ? String(translateSource)
                    : 'Оригинал';

                if (!voiceSeen[translate]) {
                    voiceSeen[translate] = true;
                    voices.push(translate);
                }

                const qualitySource = item.maxquality || item.quality;
                if (qualitySource !== null && qualitySource !== undefined) {
                    const quality = String(qualitySource);
                    if (!qualitySeen[quality]) {
                        qualitySeen[quality] = true;
                        qualities.push(quality);
                    }
                }
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
                        <div>${voices.map((voice) => this.createChip('voice', voice)).join('')}</div>
                    </div>
                    <div class="smartfilter-modal__section">
                        <h3>Качество</h3>
                        <div>${qualities.map((quality) => this.createChip('quality', quality)).join('')}</div>
                    </div>
                    <div style="margin-top:20px;display:flex;justify-content:flex-end;gap:10px;">
                        <button class="simple-button selector" id="smartfilter-reset">Сбросить</button>
                        <button class="simple-button selector" id="smartfilter-apply">Применить</button>
                    </div>
                </div>`;

            document.body.appendChild(modal);
            this.modal = modal;

            const closeModal = () => this.closeModal();

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
                    const selectedVoices = this.getSelectedValues(modal, 'voice');
                    const selectedQuality = this.getSelectedValues(modal, 'quality');
                    this.applyFilters(selectedVoices, selectedQuality);
                    closeModal();
                });

            this.forEachNode(modal.querySelectorAll('.smartfilter-chip'), (chip) => {
                chip.addEventListener('click', () => this.toggleClass(chip, 'active'));
            });

            this.keyHandler = (event) => {
                const back = this.smartfilter && typeof this.smartfilter.isBackNavigation === 'function'
                    ? this.smartfilter.isBackNavigation(event)
                    : this.isBackEvent(event);

                if (!back)
                    return;

                event.preventDefault();
                event.stopPropagation();
                closeModal();
            };

            window.addEventListener('keydown', this.keyHandler, true);

            this.backHandler = () => closeModal();
            document.addEventListener('backbutton', this.backHandler, true);

            this.setupController(modal);
        },

        closeModal() {
            if (!this.modal)
                return;

            if (this.keyHandler)
                window.removeEventListener('keydown', this.keyHandler, true);

            if (this.backHandler)
                document.removeEventListener('backbutton', this.backHandler, true);

            this.keyHandler = null;
            this.backHandler = null;

            this.teardownController();

            if (this.modal.parentElement)
                this.modal.remove();

            this.modal = null;
        },

        setupController(modal) {
            if (!window.Lampa || !Lampa.Controller || typeof Lampa.Controller.add !== 'function')
                return;

            const enabled = typeof Lampa.Controller.enabled === 'function' ? Lampa.Controller.enabled() : null;
            this.previousController = enabled && enabled.name ? enabled.name : null;

            Lampa.Controller.add(this.controllerName, {
                toggle: () => {
                    if (typeof Lampa.Controller.collectionSet === 'function')
                        Lampa.Controller.collectionSet(modal);

                    const target = modal.querySelector('.smartfilter-chip.active')
                        || modal.querySelector('#smartfilter-apply')
                        || modal.querySelector('#smartfilter-modal-close');

                    if (target && typeof Lampa.Controller.collectionFocus === 'function')
                        Lampa.Controller.collectionFocus(target, modal);
                },
                back: () => this.closeModal()
            });

            if (typeof Lampa.Controller.toggle === 'function')
                Lampa.Controller.toggle(this.controllerName);
        },

        teardownController() {
            if (!window.Lampa || !Lampa.Controller)
                return;

            if (typeof Lampa.Controller.remove === 'function')
                Lampa.Controller.remove(this.controllerName);

            if (this.previousController && typeof Lampa.Controller.toggle === 'function')
                Lampa.Controller.toggle(this.previousController);

            this.previousController = null;
        },

        getSelectedValues(modal, type) {
            const selected = [];
            this.forEachNode(modal.querySelectorAll('.smartfilter-chip[data-type="' + type + '"].active'), (chip) => {
                const value = chip.getAttribute('data-value');
                if (value)
                    selected.push(value);
            });
            return selected;
        },

        applyFilters(voices, qualities) {
            const container = this.ensureContainer();
            if (!container)
                return;

            const voiceList = Array.isArray(voices)
                ? voices.filter((voice) => voice !== null && voice !== undefined && String(voice).trim() !== '').map((voice) => String(voice))
                : [];
            const qualityList = Array.isArray(qualities)
                ? qualities.filter((quality) => quality !== null && quality !== undefined && String(quality).trim() !== '').map((quality) => String(quality))
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

                        const translateSource = payload.translate || payload.voice || 'Оригинал';
                        const translate = translateSource !== null && translateSource !== undefined
                            ? String(translateSource)
                            : 'Оригинал';
                        const qualitySource = payload.maxquality || payload.quality;
                        const maxquality = qualitySource !== null && qualitySource !== undefined
                            ? String(qualitySource)
                            : '';

                        const voiceMatch = !voiceList.length || voiceList.indexOf(translate) !== -1;
                        const qualityMatch = !qualityList.length || !maxquality || qualityList.indexOf(maxquality) !== -1;

                        if (!voiceMatch || !qualityMatch)
                            item.style.display = 'none';
                    } catch (err) {
                        /* ignore */
                    }
                });

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

                const translateSource = payload.translate || payload.voice || 'Оригинал';
                const translate = translateSource !== null && translateSource !== undefined
                    ? String(translateSource)
                    : 'Оригинал';
                const qualitySource = payload.maxquality || payload.quality;
                const maxquality = qualitySource !== null && qualitySource !== undefined
                    ? String(qualitySource)
                    : '';

                const voiceMatch = !voiceList.length || voiceList.indexOf(translate) !== -1;
                const qualityMatch = !qualityList.length || !maxquality || qualityList.indexOf(maxquality) !== -1;

                if (!voiceMatch || !qualityMatch)
                    item.dataset.hiddenByFilter = 'true';
                else
                    delete item.dataset.hiddenByFilter;
            });

            if (this.smartfilter && typeof this.smartfilter.syncAllProviderVisibility === 'function')
                this.smartfilter.syncAllProviderVisibility(container);
        },

        createChip(type, value) {
            return `<label class="smartfilter-chip" data-type="${type}" data-value="${value}">
                <input type="checkbox" />
                <span>${value}</span>
            </label>`;
        },

        addClass(element, className) {
            if (!element)
                return;

            if (element.classList)
                element.classList.add(className);
            else if (!this.hasClass(element, className))
                element.className = `${element.className} ${className}`.trim();
        },

        removeClass(element, className) {
            if (!element)
                return;

            if (element.classList)
                element.classList.remove(className);
            else if (this.hasClass(element, className))
                element.className = element.className.replace(new RegExp('(^|\\s)' + className + '(?:\\s|$)'), ' ').trim();
        },

        hasClass(element, className) {
            if (!element || !className)
                return false;

            if (element.classList)
                return element.classList.contains(className);

            return (` ${element.className} `).indexOf(` ${className} `) !== -1;
        },

        toggleClass(element, className) {
            if (!element)
                return;

            if (element.classList)
                element.classList.toggle(className);
            else if (this.hasClass(element, className))
                this.removeClass(element, className);
            else
                this.addClass(element, className);
        },

        forEachNode(collection, callback) {
            if (!collection || typeof callback !== 'function')
                return;

            const items = Array.prototype.slice.call(collection);
            for (let i = 0; i < items.length; i += 1)
                callback(items[i], i);
        },

        isBackEvent(event) {
            if (!event)
                return false;

            const key = typeof event.key === 'string' ? event.key.toLowerCase() : '';
            if (['back', 'backspace', 'escape', 'esc', 'browserback'].indexOf(key) !== -1)
                return true;

            const keyCode = event.keyCode || event.which || event.detail;
            return [8, 27, 461, 10009, 166].indexOf(keyCode) !== -1;
        }
    };

    window.SFilterUI = SFilterUI;
})();
