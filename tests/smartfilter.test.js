const assert = require('assert');

const SmartFilter = require('../wwwroot/plugins/smartfilter.js');

function resetState() {
    SmartFilter.sfilterItems = [];
    SmartFilter.cachedData = null;
    SmartFilter.cachedItems = null;
}

(function testCollectSFilterOptions() {
    resetState();

    SmartFilter.sfilterItems = [
        { translate: 'AniDub', maxquality: '1080p' },
        { voice: 'NewStudio', quality: '720p' },
        { voice_name: 'Original', quality: 'SD' }
    ];

    SmartFilter.cachedData = {
        maxquality: '4K',
        voice: [
            { name: 'TVShows', url: 'http://example.com/tv', method: 'voice' },
            { translate: 'AniMedia', url: 'http://example.com/am', method: 'voice' }
        ],
        results: [
            { translate: 'AniDub', maxquality: '1080p' },
            { voice: 'AniMedia', quality: '720p' }
        ]
    };

    const options = SmartFilter.collectSFilterOptions();

    const voicesLower = options.voices.map((voice) => SmartFilter.normalizeFilterText(voice));
    assert(voicesLower.includes('anidub'), 'collectSFilterOptions should include AniDub');
    assert(voicesLower.includes('newstudio'), 'collectSFilterOptions should include NewStudio');
    assert(voicesLower.includes('tvshows'), 'collectSFilterOptions should include TVShows from cached voice list');
    assert(voicesLower.includes('animedia'), 'collectSFilterOptions should include AniMedia from cached voice list');
    assert(voicesLower.includes('оригинал'), 'collectSFilterOptions should include Original voice');

    const qualitiesLower = options.qualities.map((quality) => SmartFilter.normalizeFilterText(quality));
    assert(qualitiesLower.includes('1080p'), 'collectSFilterOptions should include 1080p quality');
    assert(qualitiesLower.includes('720p'), 'collectSFilterOptions should include 720p quality');
    assert(qualitiesLower.includes('4k'), 'collectSFilterOptions should include 4K quality from cached data');
})();

(function testMatchesSFilterPayload() {
    resetState();

    const voiceFilter = [SmartFilter.normalizeFilterText('AniDub')];
    const qualityFilter = [SmartFilter.normalizeFilterText('1080p')];

    const matchPayload = { translate: 'AniDub', maxquality: '1080p' };
    assert(SmartFilter.matchesSFilterPayload(matchPayload, voiceFilter, qualityFilter), 'Payload with matching voice and quality should pass');

    const mismatchVoice = { translate: 'AniMedia', quality: '1080p' };
    assert(!SmartFilter.matchesSFilterPayload(mismatchVoice, voiceFilter, qualityFilter), 'Payload with other voice should be filtered out');

    const mismatchQuality = { translate: 'AniDub', quality: '720p' };
    assert(!SmartFilter.matchesSFilterPayload(mismatchQuality, voiceFilter, qualityFilter), 'Payload with other quality should be filtered out');

    const originalVoice = { voice: 'Original', quality: '1080p' };
    const originalFilter = [SmartFilter.normalizeFilterText('Оригинал')];
    assert(SmartFilter.matchesSFilterPayload(originalVoice, originalFilter, []), 'Original voice should match normalized filter');

    const missingQuality = { translate: 'AniDub' };
    assert(SmartFilter.matchesSFilterPayload(missingQuality, voiceFilter, qualityFilter), 'Missing quality should not exclude payload when filtering by quality');
})();

console.log('SmartFilter tests passed');
