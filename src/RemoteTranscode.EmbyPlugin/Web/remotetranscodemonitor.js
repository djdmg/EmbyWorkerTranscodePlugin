define(['baseView', 'emby-checkbox', 'emby-scroller'], function (BaseView) {
    'use strict';

    // Same loading and API conventions as remotetranscode.js (the administration page), whose helpers are private to
    // its module. The render functions below only build strings: src/RemoteTranscode.Tests/Web tests them under Node.

    // Emby 4.10's web app loads modules as ES modules compiled to AMD: the value is under "default".
    BaseView = (BaseView && BaseView.default) || BaseView;

    var RefreshMs = 5000;

    var StateLabels = {
        Available: ['disponible', '#52b54b'], Busy: ['occupé', '#2f7bd8'], Draining: ['en drainage', '#d88a00'],
        Unavailable: ['indisponible', '#7a7a7a'], Offline: ['hors ligne', '#555'], Error: ['en erreur', '#cf2e2e'],
        Revoked: ['révoqué', '#555']
    };
    var EncodeLabels = {
        Pending: ['en attente', '#7a7a7a'], Running: ['en cours', '#2f7bd8'], Assembling: ['assemblage', '#2f7bd8'],
        Completed: ['terminé', '#52b54b'], Failed: ['échec', '#cf2e2e'], Cancelled: ['annulé', '#555']
    };
    var JobStatesShown = { Running: true, Offered: true, Stopping: true };

    // The shim's verdict when Emby cancels during the assembly: the task ends as failed, but nothing failed.
    var CancelledDuringAssembly = 'q reçu';

    // Every value from the server goes through this before innerHTML: file and worker names are untrusted.
    function esc(value) {
        return String(value === null || value === undefined ? '' : value)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    // A finite number from the server, or null: never trusted as a number just because the field should be one.
    function num(value) {
        return typeof value === 'number' && isFinite(value) ? value : null;
    }

    function badge(labels, key) {
        var known = Object.prototype.hasOwnProperty.call(labels, key);
        var label = known ? labels[key][0] : key;
        var color = known ? labels[key][1] : '#555';
        return '<span class="rtm-badge" style="background:' + color + '">' + esc(label) + '</span>';
    }

    // The last part of a path, whichever system wrote it.
    function fileName(path) {
        return path ? String(path).split(/[\\/]/).pop() : '';
    }

    function percent(value) {
        var v = num(value);
        return v === null ? '—' : Math.round(v) + ' %';
    }

    function speedText(value) {
        var v = num(value);
        return v ? v.toFixed(1) + 'x' : '';
    }

    // Time in words: "≈ 12 min", "≈ 1 h 05".
    function eta(seconds) {
        var v = num(seconds);
        if (v === null || v < 0) return '';
        var m = Math.ceil(v / 60);
        return m < 60 ? '≈ ' + m + ' min' : '≈ ' + Math.floor(m / 60) + ' h ' + (m % 60 < 10 ? '0' : '') + (m % 60);
    }

    // From createdAt to updatedAt: "45 s", "12 min 30 s", "1 h 05 min". Empty when a date is missing or out of order.
    function duration(from, to) {
        var ms = new Date(to).getTime() - new Date(from).getTime();
        if (!(ms >= 0)) return '';
        var s = Math.round(ms / 1000), h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), r = s % 60;
        if (h) return h + ' h ' + (m < 10 ? '0' : '') + m + ' min';
        return m ? m + ' min ' + (r < 10 ? '0' : '') + r + ' s' : r + ' s';
    }

    function dateText(value) {
        var d = new Date(value);
        return isNaN(d.getTime()) ? '' : d.toLocaleString();
    }

    function list(value) {
        return Array.isArray(value) ? value : [];
    }

    // conversionsRefused may be a flag, a count, a text or a list: whatever it is, shown as text, nothing when empty.
    function refusedText(value) {
        if (value === null || value === undefined || value === false || value === 0 || value === '') return '';
        if (value === true) return 'oui';
        if (Array.isArray(value)) return value.map(refusedText).filter(Boolean).join(', ');
        return typeof value === 'object' ? JSON.stringify(value) : String(value);
    }

    function isActive(e) {
        return e.status === 'Pending' || e.status === 'Running' || e.status === 'Assembling';
    }

    function table(headers, rows) {
        return '<div class="rtm-table"><table><thead><tr>' + headers.map(function (h) { return '<th>' + esc(h) + '</th>'; }).join('') +
            '</tr></thead><tbody>' + rows.join('') + '</tbody></table></div>';
    }

    function muted(text) {
        return text ? '<div class="rtm-muted">' + esc(text) + '</div>' : '';
    }

    // ---- Workers ----

    function loadText(load) {
        if (!load) return '—';
        var gpu = num(load.gpuPercent);
        return 'CPU ' + esc(percent(load.cpuPercent)) + ' (tâches ' + esc(percent(load.ownCpuPercent)) + ')<br/>' +
            'GPU ' + esc(percent(gpu)) + (gpu === null ? '' : ' (tâches ' + esc(percent(load.ownGpuPercent)) + ')') + '<br/>' +
            'RAM ' + esc(percent(load.memoryPercent));
    }

    // What the worker is running, by name: the status gives the job's worker name, not its id.
    function jobLines(worker, jobs) {
        return list(jobs).filter(function (j) {
            return j.worker === worker.name && Object.prototype.hasOwnProperty.call(JobStatesShown, j.status);
        }).map(function (j) {
            var speed = speedText(j.speed);
            return muted((j.kind === 'Playback' ? 'Lecture : ' : 'Conversion : ') + fileName(j.input) + (speed ? ' (' + speed + ')' : ''));
        }).join('');
    }

    function renderWorkers(status) {
        var rows = list(status.workers).filter(function (w) { return !w.revoked; }).map(function (w) {
            var refused = refusedText(w.conversionsRefused);
            return '<tr><td><b>' + esc(w.name) + '</b></td>' +
                '<td>' + badge(StateLabels, w.state) + muted(w.reason) +
                (w.backgroundPaused ? muted('conversions suspendues pendant les lectures') : '') +
                (refused ? muted('Conversions refusées : ' + refused) : '') + '</td>' +
                '<td>' + esc(w.version || '—') + muted(w.ffmpeg) + '</td>' +
                '<td>' + esc(list(w.encoders).join(', ') || '—') + muted(list(w.gpus).join('; ')) + '</td>' +
                '<td class="rtm-muted">' + loadText(w.load) + '</td>' +
                '<td>' + esc(num(w.jobs) === null ? 0 : w.jobs) + ' / ' + esc(num(w.maxJobs) === null ? 0 : w.maxJobs) +
                muted('dont ' + (num(w.playbacks) === null ? 0 : w.playbacks) + ' lecture(s)') + jobLines(w, status.jobs) + '</td></tr>';
        });
        return rows.length
            ? table(['Worker', 'État', 'Version', 'Encodeurs', 'Charge', 'Tâches en cours'], rows)
            : '<p class="rtm-muted">Aucun worker associé.</p>';
    }

    // ---- Conversions in progress ----

    function machines(e) {
        var runs = list(e.runs).map(function (r) {
            var speed = speedText(r.speed);
            return r.worker + ' (tranche ' + r.slice + (speed ? ', ' + speed : '') + ')';
        });
        return runs.length ? runs.join(', ') : e.worker || '';
    }

    // No value: the audio is converted during the assembly, as when the setting is off.
    function audioText(e) {
        return e.audio || "converti à l'assemblage";
    }

    function renderCurrent(status) {
        var boxes = list(status.encodes).filter(isActive).map(function (e) {
            var value = Math.max(0, Math.min(100, num(e.percent) || 0));
            var parts = [value.toFixed(1) + ' %'];
            if (speedText(e.speed)) parts.push(speedText(e.speed));
            if (eta(e.remainingSeconds)) parts.push('reste ' + eta(e.remainingSeconds));
            if (e.slices) parts.push('tranches ' + e.slicesDone + '/' + e.slices);
            return '<div class="rtm-box"><div class="rtm-title">' + esc(fileName(e.input)) + '</div>' +
                '<div class="rtm-bar"><div style="width:' + value + '%"></div></div>' +
                '<div>' + badge(EncodeLabels, e.status) + ' ' + esc(parts.join(' — ')) + '</div>' +
                muted('Machines : ' + (machines(e) || 'aucune pour le moment')) +
                muted('Audio : ' + audioText(e)) +
                muted(e.note) + (e.error ? '<div class="rtm-error">' + esc(e.error) + '</div>' : '') + '</div>';
        });
        return boxes.length ? boxes.join('') : '<p class="rtm-muted">Aucune conversion en cours.</p>';
    }

    // ---- History ----

    function renderHistory(status) {
        var rows = list(status.encodes).filter(function (e) { return !isActive(e); }).map(function (e) {
            var cancelled = e.status === 'Failed' && e.error === CancelledDuringAssembly;
            var reason = cancelled
                ? muted("Annulée par Emby pendant l'assemblage")
                : (e.error ? '<div class="rtm-error">' + esc(e.error) + '</div>' : '') + muted(e.note);
            return '<tr><td>' + esc(fileName(e.input)) + muted(dateText(e.createdAt)) + '</td>' +
                '<td>' + badge(EncodeLabels, cancelled ? 'Cancelled' : e.status) + '</td>' +
                '<td>' + esc(duration(e.createdAt, e.updatedAt) || '—') + '</td>' +
                '<td>' + esc(e.worker || '—') + '</td>' +
                '<td>' + reason + (e.check ? muted('Contrôle : ' + e.check) : '') + (e.audio ? muted('Audio : ' + e.audio) : '') + '</td></tr>';
        });
        return rows.length
            ? table(['Fichier', 'État', 'Durée', 'Machines', 'Note et raison'], rows)
            : '<p class="rtm-muted">Aucune conversion dans l\'historique.</p>';
    }

    // ---- Lines above the tables (text only: they go through textContent) ----

    function queueText(status) {
        var n = num(status.queueRemaining);
        return n === null
            ? "File de conversion d'Emby : —"
            : "File de conversion d'Emby : " + n + ' élément(s) à convertir (en attente ou en cours).';
    }

    function refusedLine(status) {
        var refused = refusedText(status.conversionsRefused);
        return refused ? 'Conversions refusées : ' + refused : '';
    }

    // Emby 4.10's ApiClient rejects with the fetch Response, whose body holds the endpoints' {"error": "..."};
    // older clients gave an XHR-like object with responseJSON.
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

    // ApiClient adds the server address and the user's access token; the endpoints are admin-only (Api.cs).
    function api(path) {
        return ApiClient.getUrl('RemoteTranscode/' + path);
    }

    // ---- The view ----

    function View(view, params) {
        BaseView.apply(this, arguments);
        var self = this;
        // Bumped by each switch: a status asked for before it says nothing about the new value.
        this.audioGeneration = 0;
        this.onVisibility = function () { self.updateTimer(); };
        view.querySelector('.chkAudioAhead').addEventListener('change', function (e) { self.setAudioAhead(e.target.checked); });
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        var self = this;
        this.shown = true;
        document.addEventListener('visibilitychange', this.onVisibility);
        // /Settings rather than only /Status: it still answers when the coordinator is stopped.
        var generation = this.audioGeneration;
        ApiClient.getJSON(api('Settings')).then(function (s) {
            if (self.audioGeneration === generation) self.showAudioAhead(s.encodeAudioAhead === 'true');
        }, function (err) {
            return errorText(err).then(function (text) {
                self.view.querySelector('.rtm-audio-state').textContent = 'État illisible : ' + text;
            });
        });
        this.updateTimer();
    };

    View.prototype.onPause = function () {
        BaseView.prototype.onPause.apply(this, arguments);
        this.shown = false;
        document.removeEventListener('visibilitychange', this.onVisibility);
        this.updateTimer();
    };

    // Polls only while the page is shown and its tab visible; coming back refreshes at once.
    View.prototype.updateTimer = function () {
        var self = this;
        var run = this.shown && !document.hidden;
        if (this.timer && !run) {
            clearInterval(this.timer);
            this.timer = null;
        }
        if (run && !this.timer) {
            this.timer = setInterval(function () { self.refresh(); }, RefreshMs);
            this.refresh();
        }
    };

    // Rewritten only when different: a table rebuilt every 5 s would lose the administrator's selection and scrolling.
    View.prototype.setHtml = function (selector, html) {
        var el = this.view.querySelector(selector);
        if (el.rtmRendered === html) return;
        el.innerHTML = html;
        el.rtmRendered = html;
    };

    View.prototype.refresh = function () {
        var self = this;
        // A slow answer never overlaps the next tick.
        if (this.refreshing) return this.refreshing;
        var generation = this.audioGeneration;
        var done = function () { self.refreshing = null; };
        this.refreshing = ApiClient.getJSON(api('Status')).then(function (status) {
            if (!self.shown) return;
            self.view.querySelector('.rtm-status-error').textContent = '';
            self.setHtml('.rtm-workers', renderWorkers(status));
            self.setHtml('.rtm-current', renderCurrent(status));
            self.setHtml('.rtm-history', renderHistory(status));
            self.view.querySelector('.rtm-queue').textContent = queueText(status);
            self.view.querySelector('.rtm-refused').textContent = refusedLine(status);
            self.lastUpdate = new Date().toLocaleTimeString();
            self.view.querySelector('.rtm-updated').textContent = 'Actualisé à ' + self.lastUpdate + ' (toutes les 5 s).';
            if (status.settings && typeof status.settings.encodeAudioAhead === 'boolean' && self.audioGeneration === generation)
                self.showAudioAhead(status.settings.encodeAudioAhead);
        }, function (err) {
            return errorText(err).then(function (text) {
                if (!self.shown) return;
                self.view.querySelector('.rtm-status-error').textContent = 'Coordinateur : ' + text;
                self.view.querySelector('.rtm-updated').textContent = self.lastUpdate
                    ? 'Valeurs affichées : ' + self.lastUpdate + ', pas de réponse depuis.'
                    : 'Aucune valeur reçue pour le moment.';
            });
        }).then(done, done);
        return this.refreshing;
    };

    // ---- Audio converted ahead ----

    View.prototype.showAudioAhead = function (on) {
        var box = this.view.querySelector('.chkAudioAhead');
        box.checked = on;
        box.disabled = false;
        this.audioAhead = on;
        this.view.querySelector('.rtm-audio-state').textContent = on ? 'Activé' : 'Désactivé';
    };

    View.prototype.setAudioAhead = function (enable) {
        var self = this;
        var box = this.view.querySelector('.chkAudioAhead');
        box.disabled = true;
        this.audioGeneration++;
        return ApiClient.ajax({
            type: 'POST', url: api('AudioAhead'), data: JSON.stringify({ Enabled: enable }), contentType: 'application/json', dataType: 'json'
        }).then(function (s) {
            self.showAudioAhead(s.encodeAudioAhead === 'true');
        }, function (err) {
            // Back to what the server still has.
            self.showAudioAhead(!enable);
            return errorText(err).then(function (text) {
                self.view.querySelector('.rtm-audio-state').textContent = (self.audioAhead ? 'Activé' : 'Désactivé') + ' (changement refusé : ' + text + ')';
            });
        });
    };

    // For the tests under Node; the page itself goes through the view.
    View.render = { esc: esc, fileName: fileName, duration: duration, eta: eta, refusedText: refusedText, renderWorkers: renderWorkers, renderCurrent: renderCurrent, renderHistory: renderHistory, queueText: queueText, refusedLine: refusedLine };

    // Older web apps take the factory itself, Emby 4.10 takes its "default".
    View.default = View;
    return View;
});
