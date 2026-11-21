define(['baseView', 'loading', 'emby-scroller'], function (BaseView, loading) {
    'use strict';

    // Torry for Emby — page d'administration (navigateur uniquement).
    // Toutes les chaînes issues des torrents sont échappées avant affichage.

    var POLL_MS = 2000;

    function escapeHtml(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    var numberFormat = new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 1, minimumFractionDigits: 0 });

    function formatBytes(bytes) {
        if (bytes == null || isNaN(bytes)) {
            return '—';
        }
        var units = ['o', 'Kio', 'Mio', 'Gio', 'Tio'];
        var value = Number(bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.length - 1) {
            value /= 1024;
            unit++;
        }
        return numberFormat.format(value) + ' ' + units[unit];
    }

    function formatRate(bytesPerSecond) {
        return formatBytes(bytesPerSecond || 0) + '/s';
    }

    function formatDuration(seconds) {
        if (seconds == null || seconds < 0) {
            return '';
        }
        seconds = Math.round(seconds);
        var d = Math.floor(seconds / 86400);
        var h = Math.floor((seconds % 86400) / 3600);
        var m = Math.floor((seconds % 3600) / 60);
        var s = seconds % 60;
        if (d > 0) {
            return d + ' j ' + h + ' h';
        }
        if (h > 0) {
            return h + ' h ' + (m < 10 ? '0' : '') + m + ' min';
        }
        if (m > 0) {
            return m + ' min ' + (s < 10 ? '0' : '') + s + ' s';
        }
        return s + ' s';
    }

    function api(method, path, body) {
        var options = {
            type: method,
            url: ApiClient.getUrl(path),
            dataType: 'json'
        };
        if (body !== undefined) {
            options.data = JSON.stringify(body);
            options.contentType = 'application/json';
        }
        return ApiClient.ajax(options);
    }

    function readError(err) {
        if (err && typeof err.text === 'function') {
            var status = err.status;
            return err.text().then(function (text) {
                text = (text || '').trim();
                if (text.charAt(0) === '{') {
                    try {
                        var parsed = JSON.parse(text);
                        text = parsed.Message || parsed.message || text;
                    } catch (e) {
                        // texte brut
                    }
                }
                if (status === 401) {
                    return 'Session expirée : reconnectez-vous.';
                }
                if (status === 403) {
                    return 'Accès réservé aux administrateurs.';
                }
                return text || ('Erreur HTTP ' + status);
            }, function () {
                return 'Erreur HTTP ' + status;
            });
        }
        return Promise.resolve((err && err.message) || 'Erreur inconnue.');
    }

    function badgeClass(state) {
        if (state === 'Error') {
            return 'torry-badge torry-badge-err';
        }
        if (state === 'Seeding' || state === 'Completed') {
            return 'torry-badge torry-badge-ok';
        }
        return 'torry-badge';
    }

    function renderTask(task) {
        var html = '<div class="torry-task" data-id="' + escapeHtml(task.Id) + '">';
        html += '<div class="torry-task-head"><div class="torry-task-name">' + escapeHtml(task.Name) + '</div>';
        html += '<span class="' + badgeClass(task.State) + '">' + escapeHtml(task.StateLabel) + '</span></div>';

        var progress = Math.max(0, Math.min(100, task.Progress || 0));
        html += '<div class="torry-bar" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="' + progress.toFixed(0) + '"><div style="width:' + progress.toFixed(1) + '%"></div></div>';

        var meta = [];
        if (task.State === 'FetchingMetadata') {
            meta.push('Recherche de pairs pour obtenir la liste des fichiers…');
            meta.push(task.Peers + ' pair(s) connecté(s)');
        } else {
            meta.push(numberFormat.format(progress) + ' %');
            meta.push(formatBytes(task.DoneBytes) + ' / ' + formatBytes(task.SelectedBytes));
            if (task.SelectedFileCount !== task.FileCount) {
                meta.push(task.SelectedFileCount + ' fichier(s) sur ' + task.FileCount);
            } else if (task.FileCount) {
                meta.push(task.FileCount + ' fichier(s)');
            }
        }
        if (task.State === 'Downloading' || task.State === 'Checking' || task.State === 'Seeding') {
            meta.push('↓ ' + formatRate(task.DownloadRate));
            meta.push('↑ ' + formatRate(task.UploadRate));
            meta.push(task.Peers + ' pair(s)');
        }
        if (task.EtaSeconds != null) {
            meta.push('reste ' + formatDuration(task.EtaSeconds));
        }
        if (task.State === 'Seeding' || task.State === 'Completed') {
            meta.push('ratio ' + numberFormat.format(task.Ratio));
            if (task.SeedingSeconds) {
                meta.push('partagé ' + formatDuration(task.SeedingSeconds));
            }
        }
        if (task.IsPrivate) {
            meta.push('tracker privé');
        }
        html += '<div class="torry-meta">' + meta.map(function (m) { return '<span>' + escapeHtml(m) + '</span>'; }).join('') + '</div>';

        if (task.ErrorMessage) {
            html += '<div class="torry-error">' + escapeHtml(task.ErrorMessage) + '</div>';
        }
        if (task.FinalFolder) {
            html += '<div class="torry-note">Dossier : ' + escapeHtml(task.FinalFolder) + '</div>';
        }
        if (task.LibraryNote) {
            html += '<div class="torry-note">' + escapeHtml(task.LibraryNote) + '</div>';
        }

        html += '<div class="torry-actions">';
        if (task.NeedsSelection) {
            html += '<button type="button" class="torry-btn torry-btn-primary torry-btn-small" data-action="select">Choisir les fichiers</button>';
        }
        if (task.CanPause) {
            html += '<button type="button" class="torry-btn torry-btn-small" data-action="pause">Pause</button>';
        }
        if (task.CanResume) {
            html += '<button type="button" class="torry-btn torry-btn-small" data-action="resume">Reprendre</button>';
        }
        if (task.CanCancel) {
            html += '<button type="button" class="torry-btn torry-btn-small" data-action="cancel">Annuler</button>';
        }
        if (task.CanStopSeeding) {
            html += '<button type="button" class="torry-btn torry-btn-small" data-action="stopseed">Arrêter le partage</button>';
        }
        html += '<button type="button" class="torry-btn torry-btn-small" data-action="delete">Supprimer…</button>';
        html += '</div></div>';
        return html;
    }

    function renderEngine(engine) {
        var html = '';
        if (engine.StartupError) {
            html += '<div class="torry-error">' + escapeHtml(engine.StartupError) + '</div>';
        }
        (engine.Warnings || []).forEach(function (w) {
            html += '<div class="torry-warn">' + escapeHtml(w) + '</div>';
        });
        var parts = [];
        parts.push('Port BitTorrent ' + engine.ListenPort + (engine.Listening
            ? ' : en écoute'
            : ' : fermé tant qu\'aucun torrent n\'est actif'));
        parts.push(engine.DhtEnabled ? ('DHT : ' + engine.DhtNodes + ' nœud(s)') : 'DHT désactivé');
        parts.push('↓ ' + formatRate(engine.DownloadRate));
        parts.push('↑ ' + formatRate(engine.UploadRate));
        if (engine.IncompleteFreeBytes != null) {
            parts.push('Libre (en cours) : ' + formatBytes(engine.IncompleteFreeBytes));
        }
        if (engine.CompletedFreeBytes != null) {
            parts.push('Libre (terminés) : ' + formatBytes(engine.CompletedFreeBytes));
        }
        html += '<div class="torry-status" style="margin-bottom:1em;">' + parts.map(function (p) { return '<span>' + escapeHtml(p) + '</span>'; }).join('') + '</div>';
        return html;
    }

    function View(view, params) {
        BaseView.apply(this, arguments);
        var instance = this;
        this.lastStatusJson = null;
        this.timer = null;
        this.files = [];
        this.selectTaskId = null;
        this.deleteTaskId = null;

        view.querySelectorAll('.torry-tab').forEach(function (tab) {
            tab.addEventListener('click', function () {
                instance.showTab(tab.getAttribute('data-tab'));
            });
        });

        view.querySelector('.torry-add-form').addEventListener('submit', function (e) {
            e.preventDefault();
            instance.addMagnet();
        });

        var fileInput = view.querySelector('.torry-file-input');
        view.querySelector('.torry-btn-import').addEventListener('click', function () {
            fileInput.value = '';
            fileInput.click();
        });
        fileInput.addEventListener('change', function () {
            if (fileInput.files && fileInput.files[0]) {
                instance.importTorrent(fileInput.files[0]);
            }
        });

        view.querySelector('.torry-tasks').addEventListener('click', function (e) {
            var button = e.target.closest('button[data-action]');
            if (!button) {
                return;
            }
            var taskElement = button.closest('.torry-task');
            instance.onTaskAction(button.getAttribute('data-action'), taskElement.getAttribute('data-id'));
        });

        view.querySelector('.torry-settings-form').addEventListener('submit', function (e) {
            e.preventDefault();
            instance.saveSettings();
        });

        // Sélection des fichiers
        var filesModal = view.querySelector('.torry-files-modal');
        view.querySelector('.torry-files-close').addEventListener('click', function () { filesModal.hidden = true; });
        view.querySelector('.torry-sel-all').addEventListener('click', function () { instance.setSelection(function () { return true; }); });
        view.querySelector('.torry-sel-none').addEventListener('click', function () { instance.setSelection(function () { return false; }); });
        view.querySelector('.torry-sel-video').addEventListener('click', function () { instance.setSelection(function (f) { return f.IsVideo; }); });
        view.querySelector('.torry-files').addEventListener('change', function () { instance.updateSelectionSummary(); });
        view.querySelector('.torry-files-start').addEventListener('click', function () { instance.startSelected(); });

        // Suppression
        var deleteModal = view.querySelector('.torry-delete-modal');
        var deleteFiles = view.querySelector('.torry-delete-files');
        var deleteConfirm = view.querySelector('.torry-delete-confirm');
        deleteFiles.addEventListener('change', function () { instance.updateDeleteState(); });
        deleteConfirm.addEventListener('input', function () { instance.updateDeleteState(); });
        view.querySelector('.torry-delete-cancel').addEventListener('click', function () { deleteModal.hidden = true; });
        view.querySelector('.torry-delete-ok').addEventListener('click', function () { instance.confirmDelete(); });

        view.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') {
                filesModal.hidden = true;
                deleteModal.hidden = true;
            }
        });
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        var instance = this;
        this.refresh();
        this.loadSettings();
        if (!this.timer) {
            this.timer = setInterval(function () { instance.refresh(); }, POLL_MS);
        }
    };

    View.prototype.onPause = function () {
        BaseView.prototype.onPause.apply(this, arguments);
        if (this.timer) {
            clearInterval(this.timer);
            this.timer = null;
        }
    };

    View.prototype.destroy = function () {
        if (this.timer) {
            clearInterval(this.timer);
            this.timer = null;
        }
        BaseView.prototype.destroy.apply(this, arguments);
    };

    View.prototype.showTab = function (name) {
        var view = this.view;
        view.querySelectorAll('.torry-tab').forEach(function (tab) {
            tab.setAttribute('aria-selected', tab.getAttribute('data-tab') === name ? 'true' : 'false');
        });
        view.querySelectorAll('.torry-panel').forEach(function (panel) {
            panel.hidden = panel.getAttribute('data-panel') !== name;
        });
    };

    View.prototype.notify = function (message, isError) {
        var engine = this.view.querySelector('.torry-engine');
        var box = this.view.querySelector('.torry-notice');
        if (!box) {
            box = document.createElement('div');
            box.className = 'torry-notice';
            engine.parentNode.insertBefore(box, engine);
        }
        box.innerHTML = '<div class="' + (isError ? 'torry-error' : 'torry-warn') + '">' + escapeHtml(message) + '</div>';
        clearTimeout(this.noticeTimer);
        this.noticeTimer = setTimeout(function () { box.innerHTML = ''; }, isError ? 12000 : 5000);
    };

    View.prototype.refresh = function () {
        var instance = this;
        return api('GET', 'Torry/Status').then(function (status) {
            var json = JSON.stringify(status);
            if (json === instance.lastStatusJson) {
                return status;
            }
            instance.lastStatusJson = json;
            instance.view.querySelector('.torry-engine').innerHTML = renderEngine(status.Engine || {});
            var tasks = status.Tasks || [];
            instance.view.querySelector('.torry-tasks').innerHTML = tasks.length
                ? tasks.map(renderTask).join('')
                : '<div class="torry-empty">Aucun téléchargement. Collez un lien magnet ou importez un fichier .torrent.</div>';
            return status;
        }, function (err) {
            return readError(err).then(function (message) {
                instance.view.querySelector('.torry-engine').innerHTML = '<div class="torry-error">' + escapeHtml(message) + '</div>';
            });
        });
    };

    View.prototype.addMagnet = function () {
        var instance = this;
        var input = this.view.querySelector('.torry-magnet');
        var magnet = input.value.trim();
        if (!magnet) {
            return;
        }
        loading.show();
        api('POST', 'Torry/Tasks/Magnet', { Magnet: magnet }).then(function () {
            input.value = '';
            instance.notify('Lien ajouté. Récupération de la liste des fichiers auprès des pairs…');
            return instance.refresh();
        }, function (err) {
            return readError(err).then(function (m) { instance.notify(m, true); });
        }).then(function () { loading.hide(); });
    };

    View.prototype.importTorrent = function (file) {
        var instance = this;
        if (file.size > 10 * 1024 * 1024) {
            this.notify('Fichier .torrent trop volumineux (10 Mio maximum).', true);
            return;
        }
        var reader = new FileReader();
        reader.onload = function () {
            loading.show();
            api('POST', 'Torry/Tasks/TorrentFile', { Data: reader.result }).then(function (task) {
                instance.notify('Torrent importé : choisissez les fichiers à télécharger.');
                return instance.refresh().then(function () {
                    if (task && task.NeedsSelection) {
                        instance.openSelection(task.Id, task.Name);
                    }
                });
            }, function (err) {
                return readError(err).then(function (m) { instance.notify(m, true); });
            }).then(function () { loading.hide(); });
        };
        reader.onerror = function () {
            instance.notify('Lecture du fichier impossible.', true);
        };
        reader.readAsDataURL(file);
    };

    View.prototype.onTaskAction = function (action, id) {
        var instance = this;
        var task = this.findTask(id);
        if (action === 'select') {
            this.openSelection(id, task ? task.Name : '');
            return;
        }
        if (action === 'delete') {
            this.openDelete(id, task ? task.Name : '');
            return;
        }
        var routes = { pause: 'Pause', resume: 'Resume', cancel: 'Cancel', stopseed: 'Cancel' };
        if (!routes[action]) {
            return;
        }
        loading.show();
        api('POST', 'Torry/Tasks/' + encodeURIComponent(id) + '/' + routes[action], {}).then(function () {
            return instance.refresh();
        }, function (err) {
            return readError(err).then(function (m) { instance.notify(m, true); });
        }).then(function () { loading.hide(); });
    };

    View.prototype.findTask = function (id) {
        try {
            var status = JSON.parse(this.lastStatusJson || '{}');
            return (status.Tasks || []).filter(function (t) { return t.Id === id; })[0];
        } catch (e) {
            return null;
        }
    };

    // ---- sélection des fichiers

    View.prototype.openSelection = function (id, name) {
        var instance = this;
        loading.show();
        api('GET', 'Torry/Tasks/' + encodeURIComponent(id) + '/Files').then(function (files) {
            instance.selectTaskId = id;
            instance.files = files || [];
            instance.view.querySelector('.torry-files-title').textContent = 'Choisir les fichiers — ' + (name || '');
            instance.view.querySelector('.torry-files').innerHTML = instance.files.map(function (f) {
                return '<label class="torry-file"><input type="checkbox" data-index="' + f.Index + '"' + (f.Selected ? ' checked' : '') + ' />'
                    + '<span class="torry-file-path">' + escapeHtml(f.Path) + '</span>'
                    + '<span class="torry-file-size">' + escapeHtml(formatBytes(f.Length)) + '</span></label>';
            }).join('');
            instance.updateSelectionSummary();
            instance.view.querySelector('.torry-files-modal').hidden = false;
        }, function (err) {
            return readError(err).then(function (m) { instance.notify(m, true); });
        }).then(function () { loading.hide(); });
    };

    View.prototype.setSelection = function (predicate) {
        var files = this.files;
        this.view.querySelectorAll('.torry-files input[type=checkbox]').forEach(function (box) {
            var index = parseInt(box.getAttribute('data-index'), 10);
            var file = files.filter(function (f) { return f.Index === index; })[0];
            box.checked = !!(file && predicate(file));
        });
        this.updateSelectionSummary();
    };

    View.prototype.selectedIndices = function () {
        var result = [];
        this.view.querySelectorAll('.torry-files input[type=checkbox]').forEach(function (box) {
            if (box.checked) {
                result.push(parseInt(box.getAttribute('data-index'), 10));
            }
        });
        return result;
    };

    View.prototype.updateSelectionSummary = function () {
        var selected = this.selectedIndices();
        var total = 0;
        this.files.forEach(function (f) {
            if (selected.indexOf(f.Index) >= 0) {
                total += f.Length;
            }
        });
        this.view.querySelector('.torry-files-summary').textContent = selected.length + ' fichier(s) sélectionné(s) · ' + formatBytes(total);
        this.view.querySelector('.torry-files-start').disabled = selected.length === 0;
    };

    View.prototype.startSelected = function () {
        var instance = this;
        var indices = this.selectedIndices();
        if (!indices.length || !this.selectTaskId) {
            return;
        }
        loading.show();
        api('POST', 'Torry/Tasks/' + encodeURIComponent(this.selectTaskId) + '/Start', { SelectedFiles: indices.join(',') }).then(function () {
            instance.view.querySelector('.torry-files-modal').hidden = true;
            return instance.refresh();
        }, function (err) {
            return readError(err).then(function (m) { instance.notify(m, true); });
        }).then(function () { loading.hide(); });
    };

    // ---- suppression

    View.prototype.openDelete = function (id, name) {
        this.deleteTaskId = id;
        this.view.querySelector('.torry-delete-name').textContent = '« ' + (name || '') + ' »';
        this.view.querySelector('.torry-delete-files').checked = false;
        this.view.querySelector('.torry-delete-confirm').value = '';
        this.updateDeleteState();
        this.view.querySelector('.torry-delete-modal').hidden = false;
    };

    View.prototype.updateDeleteState = function () {
        var withFiles = this.view.querySelector('.torry-delete-files').checked;
        var typed = this.view.querySelector('.torry-delete-confirm').value.trim().toUpperCase();
        this.view.querySelector('.torry-delete-confirm-zone').hidden = !withFiles;
        var ok = this.view.querySelector('.torry-delete-ok');
        ok.disabled = withFiles && typed !== 'SUPPRIMER';
        ok.textContent = withFiles ? 'Supprimer la tâche et les fichiers' : 'Supprimer la tâche (garder les fichiers)';
    };

    View.prototype.confirmDelete = function () {
        var instance = this;
        var id = this.deleteTaskId;
        var withFiles = this.view.querySelector('.torry-delete-files').checked;
        if (withFiles && this.view.querySelector('.torry-delete-confirm').value.trim().toUpperCase() !== 'SUPPRIMER') {
            return;
        }
        loading.show();
        api('POST', 'Torry/Tasks/' + encodeURIComponent(id) + '/Delete', {
            DeleteFiles: withFiles,
            Confirmation: withFiles ? id : ''
        }).then(function (result) {
            instance.view.querySelector('.torry-delete-modal').hidden = true;
            var message = withFiles ? ('Tâche supprimée, ' + (result && result.DeletedFiles || 0) + ' fichier(s) effacé(s).') : 'Tâche supprimée, fichiers conservés.';
            if (result && result.Messages && result.Messages.length) {
                message += ' ' + result.Messages.join(' ');
            }
            instance.notify(message);
            return instance.refresh();
        }, function (err) {
            return readError(err).then(function (m) { instance.notify(m, true); });
        }).then(function () { loading.hide(); });
    };

    // ---- réglages

    View.prototype.loadSettings = function () {
        var form = this.view.querySelector('.torry-settings-form');
        return api('GET', 'Torry/Settings').then(function (settings) {
            Object.keys(settings).forEach(function (key) {
                var field = form.elements[key];
                if (!field) {
                    return;
                }
                if (field.type === 'checkbox') {
                    field.checked = !!settings[key];
                } else {
                    field.value = settings[key];
                }
            });
        });
    };

    View.prototype.saveSettings = function () {
        var instance = this;
        var form = this.view.querySelector('.torry-settings-form');
        var message = this.view.querySelector('.torry-settings-message');
        var body = {};
        Array.prototype.forEach.call(form.elements, function (field) {
            if (!field.name) {
                return;
            }
            if (field.type === 'checkbox') {
                body[field.name] = field.checked;
            } else if (field.type === 'number') {
                body[field.name] = Number(field.value);
            } else {
                body[field.name] = field.value.trim();
            }
        });
        loading.show();
        api('POST', 'Torry/Settings', body).then(function () {
            message.innerHTML = '<div class="torry-warn">Réglages enregistrés et appliqués.</div>';
            instance.lastStatusJson = null;
            return instance.refresh();
        }, function (err) {
            return readError(err).then(function (m) {
                message.innerHTML = '<div class="torry-error">' + escapeHtml(m) + '</div>';
            });
        }).then(function () { loading.hide(); });
    };

    return View;
});
