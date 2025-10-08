(function() {
    'use strict';
    
    // Создаем объект SmartFilter
    var SmartFilter = {
        name: 'SmartFilter',
        version: '1.0.7',
        initialized: false,
        initAttempts: 0,
        maxInitAttempts: 150,
        initRetryDelay: 200,
        cachedData: null,
        sfilterModal: null,
        sourceSelected: false,
        loadingTracker: {
            overlay: null,
            currentProgress: 0,
            totalProviders: 0,
            loadedProviders: 0,
            providersStatus: {},
            originalXHRSend: null,
            observers: []
        },
        
        // Основная функция инициализации
        init: function() {
            console.log('SmartFilter: Starting initialization sequence (attempt ' + (this.initAttempts + 1) + ')');
            
            // Проверяем, не была ли уже инициализация успешной
            if (this.initialized) {
                console.log('SmartFilter: Already initialized, skipping');
                return;
            }
            
            // Проверяем, готова ли Lampa
            if (!this.isLampaReady()) {
                this.scheduleRetry();
                return;
            }
            
            // Устанавливаем глобальный обработчик ошибок
            this.setupGlobalErrorHandler();
            
            // Инициализируем основные компоненты
            this.hookXHR();
            this.trackSourceSelection();
            
            // Помечаем как инициализированный
            this.initialized = true;
            
            // Немедленно начинаем отслеживать элементы
            this.startElementMonitoring();
            
            console.log('SmartFilter: Initialization completed successfully');
        },
        
        // Проверка готовности Lampa
        isLampaReady: function() {
            // Проверяем базовое наличие Lampa
            if (typeof Lampa === 'undefined') {
                return false;
            }
            
            // Проверяем наличие критически важных компонентов
            if (!Lampa.Activity || !Lampa.Template || !Lampa.Storage) {
                return false;
            }
            
            // Проверяем наличие ЛЮБОГО из возможных контейнеров Lampa
            const lampaContainers = [
                '.app',
                '.explorer__files',
                '.explorer__files-head',
                '.online-prestige',
                '.torrent-filter',
                '.selectbox',
                '.selectbox-item'
            ];
            
            for (const selector of lampaContainers) {
                if (document.querySelector(selector)) {
                    return true;
                }
            }
            
            // Дополнительная проверка активности
            if (Lampa.Activity && Lampa.Activity.active && Lampa.Activity.active()) {
                return true;
            }
            
            return false;
        },
        
        // Планируем повторную попытку
        scheduleRetry: function() {
            this.initAttempts++;
            
            if (this.initAttempts >= this.maxInitAttempts) {
                console.error('SmartFilter: Initialization failed after ' + this.maxInitAttempts + ' attempts');
                return;
            }
            
            // Экспоненциальная задержка для снижения нагрузки
            var delay = this.initRetryDelay * Math.min(1, Math.pow(1.2, this.initAttempts));
            
            console.log('SmartFilter: Scheduling retry in ' + delay + 'ms (attempt ' + (this.initAttempts + 1) + '/' + this.maxInitAttempts + ')');
            
            setTimeout(this.init.bind(this), delay);
        },
        
        // Установка глобального обработчика ошибок
        setupGlobalErrorHandler: function() {
            window.SmartFilterErrorHandler = function(message, source, lineno, colno, error) {
                if (message && message.indexOf('SmartFilter') !== -1) {
                    console.error('SmartFilter: Global error:', message, 'at', source + ':' + lineno);
                    return true;
                }
                return false;
            };
            
            window.onerror = window.SmartFilterErrorHandler;
        },
        
        // Начало отслеживания элементов
        startElementMonitoring: function() {
            console.log('SmartFilter: Starting element monitoring');
            
            // Проверяем элементы каждые 300ms
            setInterval(this.checkElements.bind(this), 300);
            
            // Также проверяем при открытии меню выбора источников
            this.setupSourceMenuDetection();
            
            // Отслеживаем события Lampa
            this.setupLampaEvents();
        },
        
        // Отслеживание выбора источника
        trackSourceSelection: function() {
            var self = this;
            
            // Отслеживаем изменения в тексте кнопки выбора источника
            var filterButtonObserver = new MutationObserver(function(mutations) {
                var filterButton = document.querySelector('.filter--sort div');
                if (filterButton) {
                    // Проверяем, выбран ли SmartFilter Aggregator
                    self.sourceSelected = filterButton.textContent && filterButton.textContent.indexOf('SmartFilter') !== -1;
                    
                    // Обновляем состояние кнопки SFilter
                    var sfilterButton = document.querySelector('.smartfilter-sfilter-button');
                    if (sfilterButton) {
                        if (self.sourceSelected) {
                            sfilterButton.style.opacity = '1';
                            sfilterButton.style.cursor = 'pointer';
                            
                            // Если данные уже загружены, активируем кнопку
                            if (self.cachedData && self.cachedData.data && self.cachedData.data.length > 0) {
                                sfilterButton.removeAttribute('disabled');
                            } else {
                                sfilterButton.setAttribute('disabled', 'true');
                                sfilterButton.style.opacity = '0.7';
                            }
                        } else {
                            sfilterButton.style.opacity = '0.5';
                            sfilterButton.style.cursor = 'not-allowed';
                            sfilterButton.setAttribute('disabled', 'true');
                        }
                    }
                }
            });
            
            filterButtonObserver.observe(document.body, {
                childList: true,
                subtree: true,
                characterData: true
            });
            
            this.loadingTracker.observers.push(filterButtonObserver);
        },
        
        // Настройка отслеживания открытия меню выбора источников
        setupSourceMenuDetection: function() {
            // Отслеживаем появление меню выбора источников
            var self = this;
            var sourceMenuObserver = new MutationObserver(function(mutations) {
                self.checkElements();
            });
            
            sourceMenuObserver.observe(document.body, {
                childList: true,
                subtree: true
            });
            
            this.loadingTracker.observers.push(sourceMenuObserver);
        },
        
        // Настройка отслеживания событий Lampa
        setupLampaEvents: function() {
            var self = this;
            
            // Подписываемся на события Lampa
            if (Lampa && Lampa.Activity) {
                Lampa.Activity.listener.follow('activity', function(e) {
                    if (e.type === 'start' || e.type === 'open') {
                        console.log('SmartFilter: Activity started, checking elements');
                        setTimeout(function() {
                            self.checkElements();
                        }, 300);
                    }
                });
            }
            
            // Резервный метод для старых версий
            if (Lampa && Lampa.events && Lampa.events.listener) {
                Lampa.events.listener.follow('source', function(data) {
                    if (data && data.name === 'open') {
                        console.log('SmartFilter: Source menu opened, checking elements');
                        setTimeout(function() {
                            self.checkElements();
                        }, 300);
                    }
                });
            }
        },
        
        // Проверка и обновление элементов
        checkElements: function() {
            // Проверяем и обновляем SmartFilter Aggregator
            this.adjustSmartFilterPosition();
            
            // Проверяем и добавляем кнопку SFilter
            this.addSFilterButton();
        },
        
        // Проверка позиции SmartFilter
        adjustSmartFilterPosition: function() {
            var items = document.querySelectorAll('.selectbox-item');
            for (var i = 0; i < items.length; i++) {
                var titleEl = items[i].querySelector('.selectbox-item__title');
                if (titleEl && titleEl.textContent && titleEl.textContent.indexOf('SmartFilter') !== -1) {
                    // Проверяем, не обработан ли уже этот элемент
                    if (items[i].classList.contains('smartfilter-processed')) {
                        return;
                    }
                    
                    // Извлекаем качество из текста
                    var quality = '';
                    var qualityMatch = titleEl.textContent.match(/ - (.+)$/);
                    if (qualityMatch && qualityMatch[1]) {
                        quality = ' - ' + qualityMatch[1];
                    }
                    
                    // Перемещаем в начало
                    var parent = items[i].parentNode;
                    parent.insertBefore(items[i], parent.firstChild);
                    
                    // Стилизация
                    items[i].style.background = 'linear-gradient(90deg, #2E7D32 0%, #4CAF50 100%)';
                    items[i].style.opacity = '1';
                    items[i].style.color = 'white';
                    items[i].classList.add('smartfilter-processed');
                    
                    // Изменяем название на улучшенный стиль
                    var qualityText = quality.replace(' - ', '');
                    titleEl.innerHTML = `
                        <div class="smartfilter-source-container">
                            <div class="smartfilter-source-name">SmartFilter</div>
                            <div class="smartfilter-source-quality">${qualityText}</div>
                            <div class="smartfilter-source-badge">Агрегатор</div>
                        </div>
                    `;
                    
                    // Добавляем CSS если его еще нет
                    if (!document.getElementById('smartfilter-styles')) {
                        var style = document.createElement('style');
                        style.id = 'smartfilter-styles';
                        style.innerHTML = `
                            .smartfilter-source-container {
                                display: flex;
                                align-items: center;
                                width: 100%;
                                padding: 4px 0;
                            }
                            
                            .smartfilter-source-name {
                                font-weight: bold;
                                font-size: 1.1em;
                                flex-grow: 1;
                            }
                            
                            .smartfilter-source-quality {
                                background: rgba(255, 255, 255, 0.2);
                                border-radius: 4px;
                                padding: 2px 8px;
                                margin-left: 8px;
                                font-size: 0.9em;
                                white-space: nowrap;
                            }
                            
                            .smartfilter-source-badge {
                                background: linear-gradient(90deg, #ff9800, #ff5722);
                                color: white;
                                border-radius: 12px;
                                padding: 2px 10px;
                                font-size: 0.8em;
                                font-weight: bold;
                                margin-left: 8px;
                                white-space: nowrap;
                                box-shadow: 0 1px 2px rgba(0,0,0,0.2);
                            }
                        `;
                        document.head.appendChild(style);
                    }
                    
                    console.log('SmartFilter: Adjusted SmartFilter Aggregator position and style');
                    return;
                }
            }
        },
        
        // Добавление кнопки SFilter
        addSFilterButton: function() {
            var filterButton = document.querySelector('.filter--filter');
            if (filterButton && !document.querySelector('.smartfilter-sfilter-button')) {
                var sfilterButton = document.createElement('div');
                sfilterButton.className = 'simple-button simple-button--filter selector smartfilter-sfilter-button';
                sfilterButton.style.border = '2px solid #4CAF50';
                sfilterButton.style.marginLeft = '10px';
                sfilterButton.style.opacity = '0.5';
                sfilterButton.style.cursor = 'not-allowed';
                sfilterButton.setAttribute('disabled', 'true');
                sfilterButton.innerHTML = '<span>SFilter</span>';
                sfilterButton.addEventListener('click', this.showSFilterModal.bind(this));
                filterButton.parentNode.insertBefore(sfilterButton, filterButton.nextSibling);
                
                console.log('SmartFilter: Added SFilter button');
            }
        },
        
        // Отображение модального окна фильтрации
        showSFilterModal: function() {
            // Проверяем, что выбран именно SmartFilter Aggregator
            var filterButton = document.querySelector('.filter--sort div');
            if (!filterButton || filterButton.textContent.indexOf('SmartFilter') === -1) {
                if (typeof Lampa !== 'undefined' && Lampa.Toast) {
                    Lampa.Toast.show('Сначала выберите источник SmartFilter Aggregator', 3000, 'error');
                } else {
                    alert('Сначала выберите источник SmartFilter Aggregator');
                }
                return;
            }
            
            // Проверяем, загружены ли данные
            if (!this.cachedData || !this.cachedData.data || this.cachedData.data.length === 0) {
                if (typeof Lampa !== 'undefined' && Lampa.Toast) {
                    Lampa.Toast.show('Данные еще загружаются, подождите...', 3000, 'info');
                } else {
                    alert('Данные еще загружаются, подождите...');
                }
                return;
            }
            
            // Собираем уникальные озвучки и качества
            var voices = {};
            var qualities = {};
            
            for (var i = 0; i < this.cachedData.data.length; i++) {
                var item = this.cachedData.data[i];
                var cleanVoice = this.extractCleanVoice(item.translate || 'Оригинал', item.maxquality || '');
                voices[cleanVoice] = true;
                
                if (item.maxquality) {
                    qualities[item.maxquality] = true;
                }
            }
            
            this.createFilterModal(voices, qualities);
        },
        
        // Создание модального окна
        createFilterModal: function(voices, qualities) {
            // Удаляем предыдущее модальное окно, если есть
            if (this.sfilterModal && document.body.contains(this.sfilterModal)) {
                document.body.removeChild(this.sfilterModal);
            }
            
            // Создаем модальное окно
            this.sfilterModal = document.createElement('div');
            this.sfilterModal.className = 'smartfilter-modal';
            this.sfilterModal.style.cssText = [
                'position: fixed',
                'top: 50%',
                'left: 50%',
                'transform: translate(-50%, -50%)',
                'background: #1a1a1a',
                'color: white',
                'width: 90%',
                'max-width: 500px',
                'z-index: 10000',
                'border-radius: 10px',
                'overflow: hidden',
                'box-shadow: 0 0 20px rgba(0,0,0,0.5)'
            ].join(';');
            
            // Заголовок
            var header = document.createElement('div');
            header.style.cssText = [
                'padding: 15px',
                'background: #2E7D32',
                'font-weight: bold',
                'display: flex',
                'align-items: center'
            ].join(';');
            
            header.innerHTML = `
                <svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" style="margin-right: 10px;">
                    <path d="M3 17V19H9V17H3ZM3 5V7H13V5H3ZM13 9V11H3V9H13ZM3 13V15H9V13H3ZM16 11.14L18.86 14L22 10.86L20.59 9.45L18.86 11.17L16 9.45L16 11.14Z" fill="white"/>
                </svg>
                Фильтрация источников
            `;
            this.sfilterModal.appendChild(header);
            
            // Содержимое
            var content = document.createElement('div');
            content.style.padding = '15px';
            this.sfilterModal.appendChild(content);
            
            // Фильтр озвучки
            if (Object.keys(voices).length > 0) {
                var voiceHeader = document.createElement('h3');
                voiceHeader.style.cssText = [
                    'margin: 10px 0 5px',
                    'display: flex',
                    'align-items: center'
                ].join(';');
                
                voiceHeader.innerHTML = `
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" style="margin-right: 8px;">
                        <path d="M12 2C8.13 2 5 5.13 5 9C5 14.25 12 22 12 22C12 22 19 14.25 19 9C19 5.13 15.87 2 12 2ZM12 11.5C10.07 11.5 8.5 9.93 8.5 8C8.5 6.07 10.07 4.5 12 4.5C13.93 4.5 15.5 6.07 15.5 8C15.5 9.93 13.93 11.5 12 11.5Z" fill="#4CAF50"/>
                    </svg>
                    Озвучка:
                `;
                content.appendChild(voiceHeader);
                
                for (var voice in voices) {
                    var voiceCheck = document.createElement('div');
                    voiceCheck.style.cssText = [
                        'display: flex',
                        'align-items: center',
                        'margin: 5px 0',
                        'padding: 5px 10px',
                        'border-radius: 5px',
                        'transition: background 0.2s'
                    ].join(';');
                    
                    voiceCheck.addEventListener('mouseenter', function() {
                        this.style.background = 'rgba(255, 255, 255, 0.1)';
                    });
                    
                    voiceCheck.addEventListener('mouseleave', function() {
                        this.style.background = '';
                    });
                    
                    var checkbox = document.createElement('input');
                    checkbox.type = 'checkbox';
                    checkbox.className = 'smartfilter-voice';
                    checkbox.value = voice;
                    checkbox.checked = true;
                    checkbox.style.cssText = [
                        'margin-right: 8px',
                        'width: 16px',
                        'height: 16px',
                        'cursor: pointer'
                    ].join(';');
                    
                    var label = document.createElement('label');
                    label.textContent = voice;
                    label.style.cssText = [
                        'cursor: pointer',
                        'flex-grow: 1'
                    ].join(';');
                    
                    voiceCheck.appendChild(checkbox);
                    voiceCheck.appendChild(label);
                    content.appendChild(voiceCheck);
                }
            }
            
            // Фильтр качества
            if (Object.keys(qualities).length > 0) {
                var qualityHeader = document.createElement('h3');
                qualityHeader.style.cssText = [
                    'margin: 15px 0 5px',
                    'display: flex',
                    'align-items: center'
                ].join(';');
                
                qualityHeader.innerHTML = `
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" style="margin-right: 8px;">
                        <path d="M19 3H5C3.9 3 3 3.9 3 5V19C3 20.1 3.9 21 5 21H19C20.1 21 21 20.1 21 19V5C21 3.9 20.1 3 19 3ZM19 19H5V5H19V19Z" fill="#4CAF50"/>
                    </svg>
                    Качество:
                `;
                content.appendChild(qualityHeader);
                
                for (var quality in qualities) {
                    var qualityCheck = document.createElement('div');
                    qualityCheck.style.cssText = [
                        'display: flex',
                        'align-items: center',
                        'margin: 5px 0',
                        'padding: 5px 10px',
                        'border-radius: 5px',
                        'transition: background 0.2s'
                    ].join(';');
                    
                    qualityCheck.addEventListener('mouseenter', function() {
                        this.style.background = 'rgba(255, 255, 255, 0.1)';
                    });
                    
                    qualityCheck.addEventListener('mouseleave', function() {
                        this.style.background = '';
                    });
                    
                    var checkbox = document.createElement('input');
                    checkbox.type = 'checkbox';
                    checkbox.className = 'smartfilter-quality';
                    checkbox.value = quality;
                    checkbox.checked = true;
                    checkbox.style.cssText = [
                        'margin-right: 8px',
                        'width: 16px',
                        'height: 16px',
                        'cursor: pointer'
                    ].join(';');
                    
                    var label = document.createElement('label');
                    label.textContent = quality;
                    label.style.cssText = [
                        'cursor: pointer',
                        'flex-grow: 1'
                    ].join(';');
                    
                    qualityCheck.appendChild(checkbox);
                    qualityCheck.appendChild(label);
                    content.appendChild(qualityCheck);
                }
            }
            
            // Кнопки
            var buttons = document.createElement('div');
            buttons.style.cssText = [
                'display: flex',
                'justify-content: space-between',
                'padding: 15px',
                'background: #222'
            ].join(';');
            
            var applyBtn = document.createElement('div');
            applyBtn.className = 'simple-button';
            applyBtn.style.cssText = [
                'background: #2E7D32',
                'border-radius: 5px',
                'padding: 8px 15px',
                'transition: all 0.2s'
            ].join(';');
            
            applyBtn.innerHTML = `
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" style="margin-right: 5px; vertical-align: middle;">
                    <path d="M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41L9 16.17z" fill="white"/>
                </svg>
                Применить
            `;
            
            applyBtn.addEventListener('click', this.applyFilters.bind(this));
            buttons.appendChild(applyBtn);
            
            var cancelBtn = document.createElement('div');
            cancelBtn.className = 'simple-button';
            cancelBtn.style.cssText = [
                'border-radius: 5px',
                'padding: 8px 15px',
                'transition: all 0.2s'
            ].join(';');
            
            cancelBtn.innerHTML = `
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" style="margin-right: 5px; vertical-align: middle;">
                    <path d="M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12 19 6.41z" fill="#f44336"/>
                </svg>
                Отмена
            `;
            
            cancelBtn.addEventListener('click', this.closeFilterModal.bind(this));
            buttons.appendChild(cancelBtn);
            
            this.sfilterModal.appendChild(buttons);
            document.body.appendChild(this.sfilterModal);
        },
        
        // Применение фильтров
        applyFilters: function() {
            var selectedVoices = [];
            var voiceChecks = document.querySelectorAll('.smartfilter-voice:checked');
            for (var i = 0; i < voiceChecks.length; i++) {
                selectedVoices.push(voiceChecks[i].value);
            }
            
            var selectedQualities = [];
            var qualityChecks = document.querySelectorAll('.smartfilter-quality:checked');
            for (var i = 0; i < qualityChecks.length; i++) {
                selectedQualities.push(qualityChecks[i].value);
            }
            
            // Фильтруем данные
            var filteredData = [];
            for (var i = 0; i < this.cachedData.data.length; i++) {
                var item = this.cachedData.data[i];
                var cleanVoice = this.extractCleanVoice(item.translate || 'Оригинал', item.maxquality || '');
                var hasVoice = selectedVoices.length === 0 || selectedVoices.indexOf(cleanVoice) !== -1;
                var hasQuality = selectedQualities.length === 0 || selectedQualities.indexOf(item.maxquality || '') !== -1;
                
                if (hasVoice && hasQuality) {
                    filteredData.push(item);
                }
            }
            
            // Сохраняем отфильтрованные данные
            this.filteredData = filteredData;
            
            // Обновляем интерфейс
            this.updatePlayerSources(filteredData);
            
            // Закрываем модальное окно
            this.closeFilterModal();
            
            if (typeof Lampa !== 'undefined' && Lampa.Toast) {
                Lampa.Toast.show('Фильтрация применена', 2000, 'success');
            }
        },
        
        // Обновление источников плеера
        updatePlayerSources: function(data) {
            try {
                // Получаем текущий плеер
                var player = Lampa && Lampa.Player;
                if (!player) return;
                
                // Получаем текущий источник
                var currentSource = player.source();
                if (!currentSource || currentSource.name !== 'SmartFilter Aggregator') return;
                
                // Создаем новый список источников
                var newSources = [];
                for (var i = 0; i < data.length; i++) {
                    var item = data[i];
                    var cleanVoice = this.extractCleanVoice(item.translate || 'Оригинал', item.maxquality || '');
                    
                    newSources.push({
                        name: item.details + ' (' + cleanVoice + (item.maxquality ? ' - ' + item.maxquality : '') + ')',
                        url: item.url,
                        method: item.method || 'play',
                        stream: item.stream || null,
                        quality: item.maxquality || '',
                        translate: cleanVoice
                    });
                }
                
                // Обновляем источники
                currentSource.list = newSources;
                player.source(currentSource);
                
                // Показываем обновленный список
                player.source().open();
            } catch (e) {
                console.error('SmartFilter: Error updating player sources:', e);
            }
        },
        
        // Закрытие модального окна
        closeFilterModal: function() {
            if (this.sfilterModal && document.body.contains(this.sfilterModal)) {
                document.body.removeChild(this.sfilterModal);
                this.sfilterModal = null;
            }
        },
        
        // Перехват XHR запросов
        hookXHR: function() {
            var self = this;
            
            // Сохраняем оригинальный метод
            if (!this.loadingTracker.originalXHRSend) {
                this.loadingTracker.originalXHRSend = XMLHttpRequest.prototype.send;
                
                XMLHttpRequest.prototype.send = function() {
                    var xhr = this;
                    
                    xhr.addEventListener('loadstart', function() {
                        if (xhr.responseURL && xhr.responseURL.indexOf('/lite/smartfilter') !== -1) {
                            self.loadingTracker.showOverlay();
                        }
                    });
                    
                    xhr.addEventListener('load', function() {
                        if (xhr.responseURL && xhr.responseURL.indexOf('/lite/smartfilter') !== -1) {
                            try {
                                var response = JSON.parse(xhr.responseText);
                                self.cachedData = response;
                                self.loadingTracker.updateProgress(response);
                            } catch (e) {
                                console.log('SmartFilter: Could not parse response');
                            }
                        }
                    });
                    
                    self.loadingTracker.originalXHRSend.apply(this, arguments);
                };
            }
        },
        
        // Извлечение чистой озвучки
        extractCleanVoice: function(translate, maxQuality) {
            if (!translate || translate.trim() === '') {
                return 'Оригинал';
            }
            
            var result = translate;
            
            // Удаляем упоминание качества, если оно совпадает с maxQuality
            if (maxQuality && maxQuality.trim() !== '') {
                result = result.replace(new RegExp('\\b' + maxQuality.replace(/[-\/\\^$*+?.()|[\]{}]/g, '\\$&') + '\\b', 'gi'), '');
            }
            
            // Удаляем паттерны качества
            var qualityPatterns = [
                '\\b\\d{3,4}p?\\b',
                '\\bHD\\b',
                '\\bFullHD\\b',
                '\\b4K\\b',
                '\\bUltra HD\\b',
                '\\bHDRip\\b',
                '\\bBDRip\\b',
                '\\bWEB-DL\\b',
                '\\bWEBRip\\b',
                '\\bSDR\\b',
                '\\bHDR\\b'
            ];
            
            for (var i = 0; i < qualityPatterns.length; i++) {
                result = result.replace(new RegExp(qualityPatterns[i], 'gi'), '');
            }
            
            // Удаляем год в скобках
            result = result.replace(/\s*\([^)]*\d{4}[^)]*\)\s*/g, ' ');
            result = result.replace(/\s*\b\d{4}\b\s*/g, ' ');
            
            // Удаляем лишние символы и пробелы
            result = result.replace(/^\s*[-/|—•\[\]]+\s*|\s*[-/|—•\[\]]+\s*$/g, '');
            result = result.replace(/\s*[-/|—•]\s*/g, ', ');
            result = result.replace(/\s*,\s*,\s*/g, ', ');
            result = result.replace(/\s+/g, ' ').trim();
            
            if (!result || result.match(/^\s*[\s,\.]+\s*$/)) {
                return 'Оригинал';
            }
            
            return result;
        }
    };
    
    // Расширение для loadingTracker
    SmartFilter.loadingTracker.showOverlay = function() {
        if (this.overlay && document.body.contains(this.overlay)) {
            document.body.removeChild(this.overlay);
        }
        
        this.overlay = document.createElement('div');
        this.overlay.className = 'smartfilter-overlay';
        this.overlay.style.cssText = [
            'position: fixed',
            'top: 0',
            'left: 0',
            'width: 100%',
            'height: 100%',
            'background: rgba(0, 0, 0, 0.85)',
            'z-index: 9999',
            'display: flex',
            'flex-direction: column',
            'justify-content: center',
            'align-items: center'
        ].join(';');
        
        this.overlay.innerHTML = [
            '<div class="smartfilter-progress-container" style="width: 80%; max-width: 500px; background: #222; border-radius: 10px; padding: 20px; box-shadow: 0 0 20px rgba(0,0,0,0.5);">',
            '  <div class="smartfilter-progress-title" style="text-align: center; margin-bottom: 15px; font-weight: bold; color: white; font-size: 1.2em;">Загрузка источников...</div>',
            '  <div class="smartfilter-progress-bar" style="width: 100%; height: 20px; background: #333; border-radius: 10px; overflow: hidden; margin-bottom: 10px;">',
            '    <div class="smartfilter-progress-fill" style="width: 0%; height: 100%; background: linear-gradient(90deg, #2E7D32 0%, #4CAF50 100%); transition: width 0.3s ease;"></div>',
            '  </div>',
            '  <div class="smartfilter-progress-count" style="text-align: center; margin-bottom: 15px; color: #aaa; font-size: 0.9em;">0 из ?</div>',
            '  <div class="smartfilter-providers-list" style="max-height: 300px; overflow-y: auto; width: 100%;"></div>',
            '</div>'
        ].join('');
        
        document.body.appendChild(this.overlay);
        
        // Сбрасываем прогресс
        this.currentProgress = 0;
        this.totalProviders = 0;
        this.loadedProviders = 0;
        this.providersStatus = {};
        
        // Инициализируем список провайдеров
        var providersList = this.overlay.querySelector('.smartfilter-providers-list');
        if (providersList) {
            providersList.innerHTML = '<div style="text-align: center; padding: 10px; color: #aaa;">Инициализация...</div>';
        }
    };
    
    SmartFilter.loadingTracker.updateProgress = function(response) {
        if (!this.overlay || !document.body.contains(this.overlay)) return;
        
        // Проверяем валидность ответа
        if (!response || !response.providers) {
            console.warn('SmartFilter: Invalid response format for progress update');
            return;
        }
        
        // Обновляем статус провайдеров
        this.totalProviders = response.providers.length;
        this.providersStatus = {};
        
        for (var i = 0; i < response.providers.length; i++) {
            var provider = response.providers[i];
            this.providersStatus[provider.name] = provider.status;
        }
        
        // Обновляем прогресс
        this.loadedProviders = 0;
        for (var provider in this.providersStatus) {
            if (this.providersStatus[provider] === 'completed') {
                this.loadedProviders++;
            }
        }
        
        this.currentProgress = Math.min(100, Math.round((this.loadedProviders / this.totalProviders) * 100));
        
        // Обновляем UI
        var fill = this.overlay.querySelector('.smartfilter-progress-fill');
        var count = this.overlay.querySelector('.smartfilter-progress-count');
        var list = this.overlay.querySelector('.smartfilter-providers-list');
        
        if (fill) fill.style.width = this.currentProgress + '%';
        if (count) count.textContent = this.loadedProviders + ' из ' + this.totalProviders;
        
        // Обновляем список провайдеров
        if (list) {
            list.innerHTML = '';
            for (var provider in this.providersStatus) {
                var status = this.providersStatus[provider];
                var statusClass = '';
                var statusText = '';
                
                if (status === 'completed') {
                    statusClass = 'color: #4CAF50;';
                    statusText = '✓';
                } else if (status === 'error') {
                    statusClass = 'color: #f44336;';
                    statusText = '✗';
                } else {
                    statusClass = 'color: #ff9800;';
                    statusText = '…';
                }
                
                var providerEl = document.createElement('div');
                providerEl.style.cssText = [
                    'display: flex',
                    'justify-content: space-between',
                    'padding: 8px 0',
                    'border-bottom: 1px solid #333'
                ].join(';');
                
                providerEl.innerHTML = [
                    '<span>' + provider + '</span>',
                    '<span style="' + statusClass + '">' + statusText + '</span>'
                ].join('');
                
                list.appendChild(providerEl);
            }
        }
        
        // Если загрузка завершена
        if (this.currentProgress >= 100) {
            var self = this;
            setTimeout(function() {
                if (self.overlay && document.body.contains(self.overlay)) {
                    document.body.removeChild(self.overlay);
                    self.overlay = null;
                }
            }, 2000);
        }
    };
    
    SmartFilter.loadingTracker.hideOverlay = function() {
        if (this.overlay && document.body.contains(this.overlay)) {
            document.body.removeChild(this.overlay);
            this.overlay = null;
        }
    };
    
    // Функция для инициализации плагина
    function initSmartFilter() {
        // Проверяем, не был ли уже инициализирован
        if (window.SmartFilter && window.SmartFilter.initialized) {
            console.log('SmartFilter: Already initialized, skipping');
            return;
        }
        
        // Запускаем процесс инициализации
        SmartFilter.init();
        
        // Добавляем в глобальный объект
        window.SmartFilter = SmartFilter;
    }
    
    // Проверка готовности Lampa
    function checkLampaReady() {
        // Проверяем базовое наличие Lampa
        if (typeof Lampa !== 'undefined') {
            console.log('SmartFilter: Lampa API is available, starting initialization');
            initSmartFilter();
            return;
        }
        
        // Если Lampa не найдена, продолжаем проверять
        console.log('SmartFilter: Lampa API not available yet, scheduling retry');
        setTimeout(checkLampaReady, 200);
    }
    
    // Запускаем инициализацию
    if (document.readyState === 'complete' || document.readyState === 'interactive') {
        checkLampaReady();
    } else {
        document.addEventListener('DOMContentLoaded', checkLampaReady);
        window.addEventListener('load', function() {
            setTimeout(checkLampaReady, 200);
        });
    }
    
    // Дополнительная проверка для случаев, когда Lampa загружается позже
    setTimeout(checkLampaReady, 1000);
    setTimeout(checkLampaReady, 3000);
    setTimeout(checkLampaReady, 5000);
})();