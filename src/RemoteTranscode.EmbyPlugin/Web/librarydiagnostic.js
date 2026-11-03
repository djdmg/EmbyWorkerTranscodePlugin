define(['baseView', 'loading', 'emby-scroller'], function (BaseView, loading) {
    'use strict';

    // Emby 4.10's web app loads modules as ES modules compiled to AMD: the value is under "default".
    BaseView = (BaseView && BaseView.default) || BaseView;
    loading = (loading && loading.default) || loading;

    var Levels = {
        'gere': ['géré par le plugin', '#52b54b'],
        'a-surveiller': ['à surveiller', '#d88a00'],
        'inconnu': ['inconnu', '#7a7a7a']
    };

    // HTML-escapes any server value before it goes into innerHTML: file paths come from outside.
    function esc(value) {
        return String(value === null || value === undefined ? '' : value)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function badge(level) {
        var l = Levels[level] || [level, '#555'];
        return '<span class="rtd-badge" style="background:' + l[1] + '">' + esc(l[0]) + '</span>';
    }

    function when(date) {
        return date ? new Date(date).toLocaleString() : '';
    }

    // Emby 4.10's ApiClient rejects with the fetch Response itself.
    function errorText(err) {
        if (err && err.status) return 'erreur ' + err.status;
        return String(err || 'erreur');
    }

    function View(view, params) {
        BaseView.apply(this, arguments);
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function () {
        BaseView.prototype.onResume.apply(this, arguments);
        var self = this;
        loading.show();
        this.load(true).then(function () { loading.hide(); }, function () { loading.hide(); });
        // Counts and state only while the page is shown; the list is reloaded when a run ends. Stopped in onPause.
        this.timer = setInterval(function () { self.load(false); }, 10000);
    };

    View.prototype.onPause = function () {
        BaseView.prototype.onPause.apply(this, arguments);
        clearInterval(this.timer);
    };

    View.prototype.load = function (withFiles) {
        var self = this;
        var url = ApiClient.getUrl('RemoteTranscode/LibraryDiagnostic', withFiles ? {} : { IncludeFiles: false });
        return ApiClient.getJSON(url).then(function (report) {
            self.view.querySelector('.rtd-load-error').textContent = '';
            self.renderState(report);
            self.renderCategories(report);
            if (report.files) {
                self.renderFiles(report);
            } else if (self.wasRunning && !report.state.running) {
                self.load(true);
            }
            self.wasRunning = report.state.running;
        }, function (err) {
            self.view.querySelector('.rtd-load-error').textContent = 'Résultats illisibles : ' + errorText(err);
        });
    };

    View.prototype.renderState = function (report) {
        var s = report.state, t = report.totals, text;
        if (s.running) {
            text = 'Analyse en cours : ' + s.done + ' / ' + s.total + ' fichiers';
            if (s.pausedBecause) text += ', en pause (' + s.pausedBecause + ')';
        } else if (s.lastOutcome === 'annule') {
            text = 'Analyse arrêtée le ' + when(s.endedAt) + ' : elle reprendra où elle en était.';
        } else if (s.lastOutcome === 'echec') {
            text = 'Analyse interrompue par une erreur le ' + when(s.endedAt) + ' (voir le journal d\'Emby).';
        } else if (s.lastOutcome === 'termine') {
            text = 'Analyse terminée le ' + when(s.endedAt) + '.';
        } else {
            text = 'Aucune analyse en cours.';
        }
        this.view.querySelector('.rtd-state').textContent = text;
        var totals = t.analysed + ' fichier(s) analysé(s) : ' + t.clean + ' sans signalement, ' + t.atRisk + ' signalé(s), ' + t.unreadable + ' non analysé(s).';
        if (t.analysed > 0) totals += ' En moyenne ' + String(t.sampledSecondsPerFile).replace('.', ',') + ' s lues par fichier.';
        if (t.endNotRead > 0) totals += ' Fin non lue sur ' + t.endNotRead + ' fichier(s) : leur fin de vidéo n\'est pas comparée au son.';
        this.view.querySelector('.rtd-totals').textContent = totals;
        var limits = (report.limits || []).map(function (l) { return '<li>' + esc(l) + '</li>'; }).join('');
        this.view.querySelector('.rtd-limits').innerHTML = limits;
    };

    View.prototype.renderCategories = function (report) {
        var rows = report.categories.map(function (c) {
            return '<tr><td>' + esc(c.title) + '<br>' + badge(c.level) + '</td><td>' + esc(c.count) + '</td><td>' + esc(c.meaning) + '</td><td>' + esc(c.consequence) + '</td></tr>';
        }).join('');
        this.view.querySelector('.rtd-categories').innerHTML =
            '<table><thead><tr><th>Catégorie</th><th>Fichiers</th><th>Ce que cela veut dire</th><th>Conséquence pour une conversion</th></tr></thead><tbody>' + rows + '</tbody></table>';
    };

    View.prototype.renderFiles = function (report) {
        // One list per category; a file with several findings appears in each.
        var html = report.categories.filter(function (c) { return c.count > 0; }).map(function (c) {
            var items = report.files.map(function (file) {
                var finding = file.findings.filter(function (x) { return x.category === c.id; })[0];
                return finding ? '<tr><td class="rtd-path">' + esc(file.path) + '</td><td>' + esc(finding.detail) + '</td></tr>' : '';
            }).join('');
            return '<details><summary>' + esc(c.title) + ' (' + esc(c.count) + ')</summary><table><tbody>' + items + '</tbody></table></details>';
        }).join('');
        this.view.querySelector('.rtd-files').innerHTML = html || '<p class="rtd-muted">Aucun fichier signalé pour l\'instant.</p>';
    };

    return View;
});
