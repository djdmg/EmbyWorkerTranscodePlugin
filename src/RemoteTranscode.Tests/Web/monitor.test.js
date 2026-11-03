// The monitoring page's script under Node, with a fake status and a fake Emby: what it renders, how it escapes, when it
// polls, and its switch. Run by MonitoringStatusTests (dotnet test), or alone: node --test src/RemoteTranscode.Tests/Web/monitor.test.js
'use strict';

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const web = path.join(__dirname, '..', '..', 'RemoteTranscode.EmbyPlugin', 'Web');
const source = fs.readFileSync(path.join(web, 'remotetranscodemonitor.js'), 'utf8');
const page = fs.readFileSync(path.join(web, 'remotetranscodemonitor.html'), 'utf8');

const Evil = '<script>alert(1)</script>';
const EvilAttr = '"><img src=x onerror=alert(2)>';

function element() {
    const listeners = {};
    return {
        textContent: '', innerHTML: '', checked: false, disabled: true,
        addEventListener(name, fn) { listeners[name] = fn; },
        fire(name, event) { listeners[name](event); },
    };
}

// The page's script with fake Emby globals. Each Status call stays pending until the test answers it.
function load() {
    const env = { elements: {}, requests: [], pending: [], timers: new Map(), nextTimer: 1, docListeners: new Set() };
    env.document = {
        hidden: false,
        addEventListener: (name, fn) => env.docListeners.add(fn),
        removeEventListener: (name, fn) => env.docListeners.delete(fn),
    };
    env.settings = { encodeAudioAhead: 'false' };
    env.ApiClient = {
        getUrl: (p) => '/emby/' + p,
        getJSON(url) {
            env.requests.push(['GET', url]);
            if (url.endsWith('Settings')) return Promise.resolve(env.settings);
            return new Promise((resolve, reject) => env.pending.push({ resolve, reject }));
        },
        ajax(options) {
            env.requests.push([options.type, options.url, options.data]);
            return env.postResult ? env.postResult() : Promise.resolve({ encodeAudioAhead: JSON.parse(options.data).Enabled ? 'true' : 'false' });
        },
    };
    function BaseView(view) { this.view = view; }
    BaseView.prototype.onResume = function () {};
    BaseView.prototype.onPause = function () {};
    let factoryResult;
    const define = (deps, factory) => { factoryResult = factory({ default: BaseView }); };
    const setIntervalFake = (fn, ms) => { const id = env.nextTimer++; env.timers.set(id, { fn, ms }); return id; };
    const clearIntervalFake = (id) => env.timers.delete(id);
    new Function('define', 'ApiClient', 'document', 'setInterval', 'clearInterval', source)(define, env.ApiClient, env.document, setIntervalFake, clearIntervalFake);
    env.View = factoryResult;
    env.dom = { querySelector: (sel) => (env.elements[sel] ??= element()) };
    return env;
}

const flush = () => new Promise((resolve) => setImmediate(resolve));
const statusCalls = (env) => env.requests.filter((r) => r[1].endsWith('Status')).length;

const render = load().View.render;

function evilStatus() {
    return {
        queueRemaining: 3,
        conversionsRefused: Evil,
        settings: { encodeAudioAhead: true },
        workers: [
            {
                id: 'w1', name: Evil, state: 'Busy', reason: Evil + EvilAttr, version: Evil, ffmpeg: Evil, encoders: ['h264_nvenc', Evil], gpus: [Evil],
                load: { cpuPercent: 40.4, ownCpuPercent: 10, gpuPercent: 55, ownGpuPercent: 20, memoryPercent: 30 },
                jobs: 2, maxJobs: 3, playbacks: 1, conversionsRefused: Evil,
            },
            { id: 'w2', name: 'Revoked sample', revoked: true, state: 'Revoked' },
            { id: 'w3', name: 'Offline sample', state: Evil, jobs: 0, maxJobs: 0, playbacks: 0 },
        ],
        jobs: [
            { kind: 'Playback', status: 'Running', worker: Evil, input: '/media/' + Evil + '.mkv', speed: 2.5 },
            { kind: 'Conversion', status: 'Completed', worker: Evil, input: '/media/finished-job.mkv' },
        ],
        encodes: [
            {
                status: 'Running', input: '/media/' + Evil + '.mkv', percent: 42.5, speed: 3.25, remainingSeconds: 3900, slices: 8, slicesDone: 3,
                runs: [{ worker: Evil, slice: 4, speed: 1.5 }], audio: Evil, note: Evil + EvilAttr, createdAt: '2026-01-01T10:00:00Z', updatedAt: '2026-01-01T10:10:00Z',
            },
            {
                status: 'Failed', input: 'C:\\media\\' + Evil + '.mkv', worker: Evil + ', second machine', error: Evil, note: EvilAttr, check: Evil, audio: Evil, embyResult: Evil, embyResultProblem: Evil,
                createdAt: '2026-01-01T08:00:00Z', updatedAt: '2026-01-01T09:05:30Z',
            },
            { status: 'Completed', input: '/media/sample-movie.mkv', worker: 'machine-a', percent: 100, createdAt: '2026-01-01T07:00:00Z', updatedAt: '2026-01-01T07:12:30Z' },
            { status: 'Failed', input: '/media/stopped.mkv', error: 'q reçu', note: "échec de l'assemblage", createdAt: '2026-01-01T06:00:00Z', updatedAt: '2026-01-01T06:00:45Z' },
        ],
    };
}

// Every tag of the output must be one the page writes itself, with attributes it writes itself.
function assertOnlyOurMarkup(html) {
    const allowedTags = new Set(['div', 'span', 'b', 'br', 'p', 'table', 'thead', 'tbody', 'tr', 'th', 'td']);
    for (const match of html.matchAll(/<\/?([a-zA-Z0-9]+)((?:\s+[^<>]*)?)\/?>/g)) {
        assert.ok(allowedTags.has(match[1].toLowerCase()), 'unexpected tag: ' + match[0]);
        const attrs = match[2].replace(/\s*(class|style)="[^"]*"/g, '').replace(/\//g, '').trim();
        assert.strictEqual(attrs, '', 'unexpected attribute: ' + match[0]);
    }
    assert.ok(!/<script/i.test(html), 'raw script tag');
    assert.ok(!/<img/i.test(html), 'raw img tag');
}

test('hostile names, notes and errors never become markup', () => {
    const status = evilStatus();
    for (const html of [render.renderWorkers(status), render.renderCurrent(status), render.renderHistory(status)]) {
        assertOnlyOurMarkup(html);
        assert.ok(html.includes('&lt;script&gt;alert(1)&lt;/script&gt;'), 'the name is still shown, escaped');
    }
    assert.strictEqual(render.esc(`<>&"'`), '&lt;&gt;&amp;&quot;&#39;');
});

test('a value of the wrong type cannot reach a style attribute or break the page', () => {
    const hostile = { encodes: [{ status: 'Running', input: '/a.mkv', percent: '1%"><script>', speed: 'x', remainingSeconds: {}, runs: 'no' }], workers: [{ name: 'a', load: { cpuPercent: '<b>' }, encoders: 'no', jobs: '<b>' }], jobs: 'no' };
    for (const html of [render.renderCurrent(hostile), render.renderWorkers(hostile), render.renderHistory(hostile)])
        assertOnlyOurMarkup(html);
    assert.ok(render.renderCurrent(hostile).includes('width:0%'));
});

test('workers: name, state and reason, version, encoders, load, current jobs', () => {
    const html = render.renderWorkers({
        workers: [{
            name: 'machine-a', state: 'Busy', reason: 'lecture en cours', version: '1.2.3', encoders: ['h264_nvenc', 'libx264'],
            load: { cpuPercent: 40.4, ownCpuPercent: 10, gpuPercent: null, memoryPercent: 30 }, jobs: 2, maxJobs: 3, playbacks: 1,
        }, { name: 'gone', revoked: true }],
        jobs: [
            { kind: 'Playback', status: 'Running', worker: 'machine-a', input: '/media/sample-movie.mkv' },
            { kind: 'Conversion', status: 'Completed', worker: 'machine-a', input: '/media/finished-job.mkv' },
            { kind: 'Conversion', status: 'Running', worker: 'machine-b', input: '/media/other-machine.mkv' },
        ],
    });
    for (const expected of ['machine-a', 'occupé', 'lecture en cours', '1.2.3', 'h264_nvenc, libx264', 'CPU 40 %', 'GPU —', 'RAM 30 %', '2 / 3', 'Lecture : sample-movie.mkv'])
        assert.ok(html.includes(expected), expected);
    assert.ok(!html.includes('gone'), 'revoked workers are left out');
    assert.ok(!html.includes('finished-job'), 'finished jobs are left out');
    assert.ok(!html.includes('other-machine'), "another worker's jobs are left out");
    assert.strictEqual(render.renderWorkers({}), '<p class="rtm-muted">Aucun worker associé.</p>');
});

test('conversionsRefused is shown when present, whatever its form, and absence is fine', () => {
    const worker = (extra) => render.renderWorkers({ workers: [{ name: 'a', state: 'Available', ...extra }] });
    assert.ok(!worker({}).includes('refusées'));
    assert.ok(!worker({ conversionsRefused: 0 }).includes('refusées'));
    assert.ok(!worker({ conversionsRefused: false }).includes('refusées'));
    assert.ok(!worker({ conversionsRefused: [] }).includes('refusées'));
    assert.ok(worker({ conversionsRefused: 4 }).includes('Conversions refusées : 4'));
    assert.ok(worker({ conversionsRefused: true }).includes('Conversions refusées : oui'));
    assert.ok(worker({ conversionsRefused: 'charge trop haute' }).includes('charge trop haute'));
    assert.ok(worker({ conversionsRefused: ['a', 'b'] }).includes('Conversions refusées : a, b'));
    assertOnlyOurMarkup(worker({ conversionsRefused: { why: Evil } }));
    assert.strictEqual(render.refusedLine({}), '');
    assert.strictEqual(render.refusedLine({ conversionsRefused: 2 }), 'Conversions refusées : 2');
});

test('conversions in progress: title, percent, speed, ETA, machines, slices, audio, note', () => {
    const html = render.renderCurrent({
        encodes: [{
            status: 'Running', input: '/media/sample-movie.mkv', percent: 42.5, speed: 3.25, remainingSeconds: 3900, slices: 8, slicesDone: 3,
            runs: [{ worker: 'machine-a', slice: 4, speed: 1.5 }, { worker: 'machine-b', slice: 5 }], audio: 'prêt', note: 'tranche reprise',
        }, { status: 'Completed', input: '/media/old.mkv' }],
    });
    for (const expected of ['sample-movie.mkv', 'width:42.5%', '42.5 %', '3.3x', 'reste ≈ 1 h 05', 'tranches 3/8', 'machine-a (tranche 4, 1.5x), machine-b (tranche 5)', 'Audio : prêt', 'tranche reprise'])
        assert.ok(html.includes(expected), expected);
    assert.ok(!html.includes('old.mkv'), 'history is not in progress');
    assert.ok(render.renderCurrent({ encodes: [{ status: 'Pending', input: '/a.mkv' }] }).includes("Audio : converti à l&#39;assemblage"));
    assert.ok(render.renderCurrent({}).includes('Aucune conversion en cours'));
});

test('history: status, duration, machines, note, reason; a cancelled assembly is not a failure', () => {
    const html = render.renderHistory(evilStatus());
    for (const expected of ['terminé', '12 min 30 s', 'machine-a', 'sample-movie.mkv', '1 h 05 min', 'échec', 'Contrôle : ', 'livré tel quel, écart au contrôle', '45 s', 'Annulée par Emby'])
        assert.ok(html.includes(expected), expected);
    assert.ok(!html.includes('q reçu'));
    assert.ok(!html.includes('42.5'), 'conversions in progress are not in the history');
    // A Windows path is cut too.
    assert.ok(!html.includes('C:\\media'));
    assert.ok(render.renderHistory({}).includes('Aucune conversion'));
    assert.strictEqual(render.duration('nonsense', '2026-01-01T00:00:00Z'), '');
    assert.strictEqual(render.duration('2026-01-02T00:00:00Z', '2026-01-01T00:00:00Z'), '');
});

test('queue length', () => {
    assert.ok(render.queueText({ queueRemaining: 3 }).includes('3 élément(s)'));
    assert.ok(render.queueText({ queueRemaining: 0 }).includes('0 élément(s)'));
    assert.ok(render.queueText({ queueRemaining: null }).endsWith('—'));
    assert.ok(render.queueText({}).endsWith('—'));
});

test('polls every 5 s while shown and visible, never overlapping, and stops when hidden or left', async () => {
    const env = load();
    const view = new env.View(env.dom);
    view.onResume();
    await flush();
    assert.strictEqual(env.timers.size, 1);
    assert.strictEqual([...env.timers.values()][0].ms, 5000);
    assert.strictEqual(statusCalls(env), 1, 'refreshed at once');

    [...env.timers.values()][0].fn();
    assert.strictEqual(statusCalls(env), 1, 'a slow answer is not overlapped');
    env.pending.shift().resolve(evilStatus());
    await flush();
    assert.ok(env.elements['.rtm-workers'].innerHTML.includes('&lt;script&gt;'));
    assert.ok(env.elements['.rtm-queue'].textContent.includes('3 élément(s)'));
    [...env.timers.values()][0].fn();
    assert.strictEqual(statusCalls(env), 2, 'next tick');
    env.pending.shift().resolve(evilStatus());
    await flush();

    env.document.hidden = true;
    env.docListeners.forEach((fn) => fn());
    assert.strictEqual(env.timers.size, 0, 'stopped when the tab is hidden');
    env.document.hidden = false;
    env.docListeners.forEach((fn) => fn());
    assert.strictEqual(env.timers.size, 1, 'back when visible');
    assert.strictEqual(statusCalls(env), 3, 'and refreshed at once');
    env.pending.shift().resolve(evilStatus());
    await flush();

    view.onPause();
    assert.strictEqual(env.timers.size, 0, 'stopped when leaving the page');
    assert.strictEqual(env.docListeners.size, 0, 'a tab coming back cannot restart it');

    view.onResume();
    assert.strictEqual(env.timers.size, 1, 'and comes back with the page');
    view.onPause();
});

test('an unchanged table is not rewritten', async () => {
    const env = load();
    const view = new env.View(env.dom);
    view.onResume();
    env.pending.shift().resolve(evilStatus());
    await flush();
    const el = env.elements['.rtm-history'];
    let writes = 0;
    let stored = el.innerHTML;
    Object.defineProperty(el, 'innerHTML', { get: () => stored, set: (v) => { writes++; stored = v; } });
    [...env.timers.values()][0].fn();
    env.pending.shift().resolve(evilStatus());
    await flush();
    assert.strictEqual(writes, 0);
    view.onPause();
});

test('a coordinator error is shown as text, with the age of the values', async () => {
    const env = load();
    const view = new env.View(env.dom);
    view.onResume();
    env.pending.shift().reject({ status: 503, text: () => Promise.resolve(JSON.stringify({ error: 'coordinateur arrêté : ' + Evil })) });
    await flush();
    assert.strictEqual(env.elements['.rtm-status-error'].textContent, 'Coordinateur : coordinateur arrêté : ' + Evil);
    assert.strictEqual(env.elements['.rtm-status-error'].innerHTML, '', 'through textContent, never innerHTML');
    assert.ok(env.elements['.rtm-updated'].textContent.includes('Aucune valeur'));
    // The next tick works again.
    [...env.timers.values()][0].fn();
    env.pending.shift().resolve(evilStatus());
    await flush();
    assert.strictEqual(env.elements['.rtm-status-error'].textContent, '');
    view.onPause();
});

test('the switch shows the server truth and posts to /AudioAhead', async () => {
    const env = load();
    env.settings = { encodeAudioAhead: 'true' };
    const view = new env.View(env.dom);
    const box = env.dom.querySelector('.chkAudioAhead');
    view.onResume();
    await flush();
    assert.strictEqual(box.checked, true);
    assert.strictEqual(box.disabled, false);
    assert.strictEqual(env.elements['.rtm-audio-state'].textContent, 'Activé');

    box.checked = false;
    box.fire('change', { target: box });
    assert.strictEqual(box.disabled, true, 'no second click while posting');
    await flush();
    const post = env.requests.find((r) => r[0] === 'POST');
    assert.strictEqual(post[1], '/emby/RemoteTranscode/AudioAhead');
    assert.strictEqual(post[2], '{"Enabled":false}');
    assert.strictEqual(box.checked, false);
    assert.strictEqual(box.disabled, false);

    // The Status asked for before the switch answers after it, with the old value: ignored.
    env.pending.shift().resolve({ settings: { encodeAudioAhead: true } });
    await flush();
    assert.strictEqual(box.checked, false);

    // Later ones are the truth (another administrator switched it).
    [...env.timers.values()][0].fn();
    env.pending.shift().resolve({ settings: { encodeAudioAhead: true } });
    await flush();
    assert.strictEqual(box.checked, true);

    // A status without the field changes nothing.
    [...env.timers.values()][0].fn();
    env.pending.shift().resolve({});
    await flush();
    assert.strictEqual(box.checked, true);
    view.onPause();
});

test('a refused switch goes back and says why', async () => {
    const env = load();
    const view = new env.View(env.dom);
    const box = env.dom.querySelector('.chkAudioAhead');
    view.onResume();
    await flush();
    assert.strictEqual(box.checked, false);
    env.postResult = () => Promise.reject({ status: 503, text: () => Promise.resolve(JSON.stringify({ error: 'plugin non initialisé' })) });
    box.checked = true;
    box.fire('change', { target: box });
    await flush();
    assert.strictEqual(box.checked, false);
    assert.strictEqual(box.disabled, false);
    assert.ok(env.elements['.rtm-audio-state'].textContent.includes('plugin non initialisé'));
    view.onPause();
});

test('the script only uses elements the page has, and the page loads nothing from outside', () => {
    for (const match of source.matchAll(/querySelector\('\.([\w-]+)'\)|setHtml\('\.([\w-]+)'/g))
        assert.ok(page.includes(match[1] || match[2]), match[0]);
    assert.ok(page.includes('data-controller="__plugin/remotetranscodemonitorjs"'));
    for (const text of [source, page]) {
        assert.ok(!/https?:\/\//.test(text), 'no external address');
        assert.ok(!/<script|<link|<iframe|@import|src=/i.test(text), 'no external resource');
    }
});
