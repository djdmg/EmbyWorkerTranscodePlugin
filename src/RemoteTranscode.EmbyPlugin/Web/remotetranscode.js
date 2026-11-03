define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-select', 'emby-checkbox', 'emby-scroller'], function (BaseView, loading) {
    'use strict';

    // Emby 4.10's web app loads modules as ES modules compiled to AMD: the value is under "default".
    BaseView = (BaseView && BaseView.default) || BaseView;
    loading = (loading && loading.default) || loading;

    var StateLabels = {
        Available: ['disponible', '#52b54b'], Busy: ['occupé', '#2f7bd8'], Draining: ['en drainage', '#d88a00'],
        Unavailable: ['indisponible', '#7a7a7a'], Offline: ['hors ligne', '#555'], Error: ['en erreur', '#cf2e2e'],
        Revoked: ['révoqué', '#555'], On: ['activé', '#52b54b'], Off: ['désactivé', '#555']
    };
    var EncodeLabels = {
        Pending: ['en attente', '#7a7a7a'], Running: ['en cours', '#2f7bd8'], Assembling: ['assemblage', '#2f7bd8'],
        Completed: ['terminé', '#52b54b'], Failed: ['échec', '#cf2e2e'], Cancelled: ['annulé', '#555']
    };

    // HTML-escapes any server value before it goes into innerHTML (worker names, paths, errors come from outside).
    function esc(value) {
        return String(value === null || value === undefined ? '' : value)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function badge(labels, key) {
        var l = labels[key] || [key, '#555'];
        return '<span class="rt-badge" style="background:' + l[1] + '">' + esc(l[0]) + '</span>';
    }

    function fileName(path) {
        return path ? String(path).split('/').pop() : '';
    }

    function pct(v) {
        return v === null || v === undefined ? '—' : Math.round(v) + ' %';
    }

    // ApiClient adds the server address and the user's access token; the endpoints are admin-only (Api.cs).
    function api(path) {
        return ApiClient.getUrl('RemoteTranscode/' + path);
    }

    function post(path, body) {
        return ApiClient.ajax({ type: 'POST', url: api(path), data: JSON.stringify(body || {}), contentType: 'application/json', dataType: 'json' });
    }

    // Always a promise of the text to show. Emby 4.10's ApiClient rejects with the fetch Response itself, whose body
    // (the endpoints' {"error": "..."} in French) must be read; older clients gave an XHR-like object with responseJSON.
    function errorText(err) {
        if (err && err.responseJSON && err.responseJSON.error) return Promise.resolve(err.responseJSON.error);
        var fallback = err && err.status ? 'erreur ' + err.status : String(err || 'erreur');
        if (err && typeof err.text === 'function' && !err.bodyUsed) {
            return err.text().then(function (body) {
                try {
                    var parsed = JSON.parse(body);
                    if (parsed && parsed.error) return parsed.error;
                } catch (e) { /* not JSON */ }
                return body ? fallback + ' : ' + body : fallback;
            }, function () { return fallback; });
        }
        return Promise.resolve(fallback);
    }

    function showError(err) {
        return errorText(err).then(function (text) { alert(text); });
    }

    function View(view, params) {
        BaseView.apply(this, arguments);
        var self = this;
        this.settings = null;

        view.querySelector('.btnOffloadOn').addEventListener('click', function () { self.setOffload(true); });
        view.querySelector('.btnOffloadOff').addEventListener('click', function () { self.setOffload(false); });
        view.querySelector('.btnPair').addEventListener('click', function () { self.pairWorker(); });
        view.querySelector('.btnSaveSettings').addEventListener('click', function () { self.saveSettings(); });
        view.querySelector('.chkReportFailures').addEventListener('change', function (e) { self.setReportFailures(e.target.checked); });


        // One delegated handler for the buttons rendered in the tables.
        view.addEventListener('click', function (e) {
            var btn = e.target.closest('button[data-action]');
            if (!btn) return;
            var id = btn.getAttribute('data-id');
            switch (btn.getAttribute('data-action')) {
                case 'revoke':
                    if (confirm('Révoquer ce worker ? Il sera déconnecté et ne pourra plus se reconnecter.')) {
                        post('Workers/' + encodeURIComponent(id) + '/Revoke').then(function () { self.refresh(); }, showError);
                    }
                    break;
            }
        });
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        var self = this;
        loading.show();
        ApiClient.getJSON(api('Settings')).then(function (s) {
            self.settings = s;
            self.refreshOffload();
            self.view.querySelector('.txtWorkerUrl').value = s.workerUrl || '';
            self.view.querySelector('.txtPublicUrl').value = s.publicUrl || '';
            self.view.querySelector('.txtAdvertisedHost').value = s.advertisedHost || '';
        }, function (err) {
            errorText(err).then(function (text) {
                self.view.querySelector('.rt-offload-state').innerHTML = '<span class="rt-error">Réglages illisibles : ' + esc(text) + '</span>';
            });
        });
        this.refresh().then(function () { loading.hide(); }, function () { loading.hide(); });
        // Polled while the page is shown; stopped in onPause.
        this.timer = setInterval(function () { self.refresh(); }, 3000);
    };

    View.prototype.onPause = function () {
        BaseView.prototype.onPause.apply(this, arguments);
        clearInterval(this.timer);
    };

    View.prototype.refresh = function () {
        var self = this;
        return ApiClient.getJSON(api('Status')).then(function (status) {
            self.view.querySelector('.rt-status-error').textContent = '';
            self.renderWorkers(status.workers || []);
            self.renderJobs(status.jobs || []);
            self.renderEncodes(status.encodes || []);
            self.renderCurrent(status.encodes || []);
            self.view.querySelector('.rt-queue').textContent = status.queueRemaining == null
                ? "File de conversion d'Emby : —"
                : "File de conversion d'Emby : " + status.queueRemaining + " élément(s) à convertir (en attente ou en cours).";
        }, function (err) {
            return errorText(err).then(function (text) {
                self.view.querySelector('.rt-status-error').textContent = 'Coordinateur : ' + text;
            });
        });
    };

    // ---- On/off switch: the plugin applies it to Emby's ffmpeg setting (and at every Emby start) ----

    View.prototype.refreshOffload = function () {
        var s = this.settings || {};
        // The settings endpoint sends booleans as the strings "true"/"false".
        var on = s.active === 'true';
        this.view.querySelector('.rt-offload-state').innerHTML = badge(StateLabels, on ? 'On' : 'Off') +
            ' ffmpeg utilisé par Emby : ' + esc(s.encoderPath || '?') +
            (s.enabled === 'true' && !on ? ' <span class="rt-error">(activé mais non appliqué)</span>' : '');
        this.view.querySelector('.rt-service').innerHTML = (s.running === 'true'
            ? 'Coordinateur intégré à Emby : en service. '
            : '<span class="rt-error">Coordinateur intégré à Emby : arrêté' + (s.error ? ' (' + esc(s.error) + ')' : '') + '.</span> ') +
            "ffmpeg d'Emby détecté : " + esc(s.embyFfmpeg || '?') + ' — données : ' + esc(s.dataDir || '?');
        this.refreshReport();
    };

    // ---- Automatic GitHub reports of failed conversions ----

    View.prototype.refreshReport = function () {
        var s = this.settings || {};
        var box = this.view.querySelector('.chkReportFailures');
        box.checked = s.reportFailures === 'true';
        var state = s.reportFailuresState;
        this.view.querySelector('.rt-report-state').innerHTML = state === 'active'
            ? badge(StateLabels, 'On') + ' actif'
            : state === 'notoken'
                ? badge(StateLabels, 'Off') + " inactif : pas de jeton dans cette version (compilation locale ou fork), rien n'est envoyé"
                : badge(StateLabels, 'Off') + ' désactivé';
    };

    View.prototype.setReportFailures = function (enable) {
        var self = this;
        post('ReportFailures', { Enabled: enable }).then(function (s) {
            self.settings = s;
            self.refreshReport();
        }, function (err) {
            self.refreshReport();
            showError(err);
        });
    };

    View.prototype.saveSettings = function () {
        var self = this;
        loading.show();
        post('Settings', {
            WorkerUrl: this.view.querySelector('.txtWorkerUrl').value.trim(),
            PublicUrl: this.view.querySelector('.txtPublicUrl').value.trim(),
            AdvertisedHost: this.view.querySelector('.txtAdvertisedHost').value.trim()
        }).then(function (s) {
            loading.hide();
            self.settings = s;
            self.refreshOffload();
            self.refresh();
        }, function (err) {
            loading.hide();
            showError(err);
        });
    };

    View.prototype.setOffload = function (enable) {
        var self = this;
        loading.show();
        post('Enabled', { Enabled: enable }).then(function (s) {
            loading.hide();
            self.settings = s;
            self.refreshOffload();
        }, function (err) {
            loading.hide();
            showError(err);
        });
    };

    // ---- Workers ----

    View.prototype.renderWorkers = function (workers) {
        var rows = workers.filter(function (w) { return !w.revoked; }).map(function (w) {
            var load = w.load
                ? 'CPU ' + pct(w.load.cpuPercent) + ' (tâches ' + pct(w.load.ownCpuPercent) + ')<br/>GPU ' + pct(w.load.gpuPercent) +
                  (w.load.gpuPercent === null ? '' : ' (tâches ' + pct(w.load.ownGpuPercent) + ')') + '<br/>RAM ' + pct(w.load.memoryPercent)
                : '—';
            var caps = esc((w.encoders || []).join(', ')) + (w.gpus && w.gpus.length ? '<div class="rt-muted">' + esc(w.gpus.join('; ')) + '</div>' : '');
            var seen = w.lastSeen ? new Date(w.lastSeen).toLocaleTimeString() : '—';
            return '<tr><td><b>' + esc(w.name) + '</b><div class="rt-muted">' + esc(w.remoteAddress || '') + '</div></td>' +
                '<td>' + badge(StateLabels, w.state) + (w.reason ? '<div class="rt-muted">' + esc(w.reason) + '</div>' : '') +
                (w.backgroundPaused ? '<div class="rt-muted">conversions suspendues pendant les lectures</div>' : '') +
                (w.conversionsRefusedLabel ? '<div class="rt-muted">' + esc(w.conversionsRefusedLabel) + '</div>' : '') + '</td>' +
                '<td>' + esc(w.jobs) + ' / ' + esc(w.maxJobs) + '<div class="rt-muted">dont ' + esc(w.playbacks) + ' lecture(s)</div></td>' +
                '<td class="rt-muted">' + load + '</td><td>' + caps + '</td><td class="rt-muted">' + esc(seen) + '</td>' +
                '<td><button is="emby-button" type="button" class="raised" data-action="revoke" data-id="' + esc(w.id) + '"><span>Révoquer</span></button></td></tr>';
        });
        this.view.querySelector('.rt-workers').innerHTML = rows.length
            ? '<table><thead><tr><th>Worker</th><th>État</th><th>Tâches</th><th>Charge</th><th>Capacités validées</th><th>Vu</th><th></th></tr></thead><tbody>' + rows.join('') + '</tbody></table>'
            : '<p class="rt-muted">Aucun worker associé.</p>';
    };

    View.prototype.pairWorker = function () {
        var self = this;
        var name = this.view.querySelector('.txtWorkerName').value.trim();
        if (!name) {
            alert('Indiquez un nom pour le worker (par exemple le nom du PC).');
            return;
        }
        post('Workers/Pair', { Name: name }).then(function (result) {
            var box = self.view.querySelector('.rt-pairing');
            box.hidden = false;
            // Shown once: the coordinator keeps only a hash of the worker token.
            box.querySelector('.rt-code').textContent = result.code;
            self.view.querySelector('.txtWorkerName').value = '';
            self.refresh();
        }, showError);
    };

    // ---- Jobs ----

    View.prototype.renderJobs = function (jobs) {
        var rows = jobs.filter(function (j) { return j.status === 'Running' || j.status === 'Offered' || j.status === 'Stopping'; }).map(function (j) {
            // Seconds formatted as hh:mm:ss (valid below 24 h).
            var position = j.position ? new Date(j.position * 1000).toISOString().substr(11, 8) : '—';
            return '<tr><td>' + (j.kind === 'Playback' ? 'Lecture' : 'Conversion') + '</td><td>' + esc(fileName(j.input)) + '</td>' +
                '<td>' + esc(j.worker) + '</td><td>' + esc(position) + '</td><td>' + esc(j.segments) + '</td>' +
                '<td>' + (j.speed ? esc(j.speed.toFixed(1)) + 'x' : '—') + '</td></tr>';
        });
        this.view.querySelector('.rt-jobs').innerHTML = rows.length
            ? '<table><thead><tr><th>Type</th><th>Fichier</th><th>Worker</th><th>Position</th><th>Segments</th><th>Vitesse</th></tr></thead><tbody>' + rows.join('') + '</tbody></table>'
            : '<p class="rt-muted">Aucune tâche sur les workers. Sans worker, Emby transcode sur le serveur comme d\'habitude.</p>';
    };

    // ---- Encodes ----

    // Film time as h:mm:ss (or m:ss under an hour).
    function clock(seconds) {
        var s = Math.max(0, Math.round(seconds || 0)), h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), r = s % 60;
        return (h ? h + ':' + (m < 10 ? '0' : '') : '') + m + ':' + (r < 10 ? '0' : '') + r;
    }

    // Remaining time in words: "≈ 12 min", "≈ 1 h 05".
    function remaining(seconds) {
        var m = Math.ceil(seconds / 60);
        return m < 60 ? '≈ ' + m + ' min' : '≈ ' + Math.floor(m / 60) + ' h ' + (m % 60 < 10 ? '0' : '') + (m % 60);
    }

    // The conversion(s) in progress, above the history: what is converted, how far, until when, and by which machines.
    View.prototype.renderCurrent = function (encodes) {
        var current = encodes.filter(function (e) { return e.status === 'Running' || e.status === 'Pending' || e.status === 'Assembling'; });
        this.view.querySelector('.rt-current').innerHTML = current.map(function (e) {
            var pct = Math.min(100, e.percent || 0);
            var who = (e.runs || []).map(function (r) { return esc(r.worker) + ' (tranche ' + esc(r.slice) + (r.speed ? ', ' + esc(r.speed.toFixed(1)) + 'x' : '') + ')'; }).join(', ');
            return '<div class="rt-current-box"><div class="rt-current-title">Conversion en cours : ' + esc(fileName(e.input)) + '</div>' +
                '<div class="rt-bar"><div style="width:' + pct + '%"></div></div>' +
                '<div>' + badge(EncodeLabels, e.status) + ' ' + esc(pct.toFixed(1)) + ' %' +
                (e.speed ? ' — ' + esc(e.speed.toFixed(1)) + 'x' : '') +
                (e.remainingSeconds != null ? ' — reste ' + esc(remaining(e.remainingSeconds)) : '') +
                (e.slices ? ' — tranches ' + esc(e.slicesDone + '/' + e.slices) : '') + '</div>' +
                '<div class="rt-muted">' + (who ? 'En cours sur : ' + who : esc(e.note || '')) + '</div></div>';
        }).join('');
    };

    View.prototype.renderEncodes = function (encodes) {
        var rows = encodes.map(function (e) {
            var progress = '<div class="rt-bar"><div style="width:' + Math.min(100, e.percent || 0) + '%"></div></div><div class="rt-muted">' +
                esc((e.percent || 0).toFixed(1)) + ' %' + (e.speed ? ' — ' + esc(e.speed.toFixed(1)) + 'x' : '') +
                (e.remainingSeconds != null ? ' — reste ' + esc(remaining(e.remainingSeconds)) : '') + '</div>';
            // Running slices: who encodes which part of the film, and how far it got.
            var runs = (e.runs || []).map(function (r) {
                var pct = r.end > r.start ? Math.min(100, 100 * (r.position - r.start) / (r.end - r.start)) : 0;
                return '<div><b>' + esc(r.worker) + '</b> : tranche ' + esc(r.slice) + '/' + esc(e.slices) +
                    '<div class="rt-muted">' + esc(clock(r.start)) + ' → ' + esc(clock(r.end)) + ', ' + esc(pct.toFixed(0)) + ' %' +
                    (r.speed ? ', ' + esc(r.speed.toFixed(1)) + 'x' : '') + '</div></div>';
            }).join('');
            var actions = '';
            return '<tr><td>' + esc(fileName(e.input)) + '<div class="rt-muted">' + esc(e.profile) + ' → ' + esc(fileName(e.output)) + '</div></td>' +
                '<td>' + badge(EncodeLabels, e.status) + '<div class="rt-muted">' + esc(e.error || e.note || '') + '</div></td>' +
                '<td>' + progress + '</td><td>' + (runs || esc(e.worker || '—')) + '</td><td>' +
                (e.slices ? esc(e.slicesDone + '/' + e.slices) : '—') + '<div class="rt-muted">' + esc(e.chunks) + ' morceaux</div></td>' +
                '<td class="rt-muted">' + esc(new Date(e.updatedAt).toLocaleString()) + '</td><td>' + actions + '</td></tr>';
        });
        this.view.querySelector('.rt-encodes').innerHTML = rows.length
            ? '<table><thead><tr><th>Fichier</th><th>État</th><th>Progression</th><th>Workers</th><th>Tranches</th><th>Mise à jour</th><th></th></tr></thead><tbody>' + rows.join('') + '</tbody></table>'
            : '<p class="rt-muted">Aucune conversion.</p>';
    };

    // Older web apps take the factory itself, Emby 4.10 takes its "default".
    View.default = View;
    return View;
});
