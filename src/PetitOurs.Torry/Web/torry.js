define(['baseView', 'loading', 'emby-scroller'], function (BaseView, loading) {
    'use strict';

    // Torry for Emby — page d'administration (navigateur uniquement).
    // Toutes les chaînes issues des torrents sont échappées avant affichage.

    var POLL_MS = 2000;

    var CONTENT_TYPES = [
        ['movies', 'Films'], ['tvshows', 'Séries'], ['music', 'Musique'], ['musicvideos', 'Clips musicaux'],
        ['homevideos', 'Vidéos personnelles et photos'], ['photos', 'Photos'], ['books', 'Livres'],
        ['audiobooks', 'Livres audio'], ['mixed', 'Contenu mixte']
    ];

    var ICONS = { folder: 'folder', video: 'movie', audio: 'music_note', image: 'image', book: 'menu_book', subtitle: 'subtitles', other: 'description' };

    function contentTypeLabel(type) {
        for (var i = 0; i < CONTENT_TYPES.length; i++) {
            if (CONTENT_TYPES[i][0] === type) {
                return CONTENT_TYPES[i][1];
            }
        }
        return 'Contenu mixte';
    }

    function joinPath(a, b) {
        return a ? (b ? a + '/' + b : a) : (b || '');
    }

    function parentPath(path) {
        var i = (path || '').lastIndexOf('/');
        return i < 0 ? '' : path.substring(0, i);
    }

    var CATEGORY_LABELS = { video: 'vidéo', audio: 'audio', image: 'image', book: 'livre', subtitle: 'sous-titre', other: 'autre' };

    function describeAnalysis(analysis, entry) {
        if (!analysis) {
            return entry.IsDirectory ? 'Dossier' : 'Fichier';
        }
        var parts = [];
        if (analysis.IsDirectory) {
            var counts = Object.keys(analysis.Categories || {}).filter(function (k) { return k !== 'other'; }).map(function (k) {
                return analysis.Categories[k] + ' ' + (CATEGORY_LABELS[k] || k);
            });
            parts.push('Dossier · ' + analysis.FileCount + ' fichier(s)' + (counts.length ? ' dont ' + counts.join(', ') : '') + (analysis.Truncated ? ' (analyse partielle)' : ''));
        } else {
            parts.push('Fichier ' + (CATEGORY_LABELS[analysis.DominantCategory] || ''));
        }
        parts.push(formatBytes(analysis.TotalBytes));
        parts.push(analysis.SuggestedContentTypeLabel ? 'type détecté : ' + analysis.SuggestedContentTypeLabel : 'type non reconnu');
        return parts.join(' · ');
    }

    function mergeAnalyses(list, count) {
        if (!list.length) {
            return null;
        }
        if (list.length === 1 && count === 1) {
            return list[0];
        }
        var merged = { IsDirectory: true, FileCount: 0, TotalBytes: 0, Categories: {}, CompatibleContentTypes: null };
        var votes = {};
        var types = {};
        list.forEach(function (a) {
            merged.FileCount += a.FileCount || 0;
            merged.TotalBytes += a.TotalBytes || 0;
            Object.keys(a.Categories || {}).forEach(function (k) { merged.Categories[k] = (merged.Categories[k] || 0) + a.Categories[k]; });
            if (a.SuggestedRootId) {
                votes[a.SuggestedRootId] = (votes[a.SuggestedRootId] || 0) + 1;
            }
            if (a.SuggestedContentType) {
                types[a.SuggestedContentType] = a.SuggestedContentTypeLabel;
            }
            // Bibliothèques compatibles avec TOUS les éléments.
            merged.CompatibleContentTypes = merged.CompatibleContentTypes === null
                ? (a.CompatibleContentTypes || []).slice()
                : merged.CompatibleContentTypes.filter(function (t) { return (a.CompatibleContentTypes || []).indexOf(t) >= 0; });
        });
        var best = Object.keys(votes).sort(function (x, y) { return votes[y] - votes[x]; })[0];
        merged.SuggestedRootId = best || null;
        var typeKeys = Object.keys(types);
        merged.SuggestedContentTypeLabel = typeKeys.length === 1 ? types[typeKeys[0]] : (typeKeys.length > 1 ? 'types variés' : null);
        merged.CompatibleContentTypes = merged.CompatibleContentTypes || [];
        return merged;
    }

    function describeSelection(analysis, count) {
        if (!analysis) {
            return count + ' éléments';
        }
        var counts = Object.keys(analysis.Categories || {}).filter(function (k) { return k !== 'other'; }).map(function (k) {
            return analysis.Categories[k] + ' ' + (CATEGORY_LABELS[k] || k);
        });
        return count + ' éléments · ' + analysis.FileCount + ' fichier(s)' + (counts.length ? ' dont ' + counts.join(', ') : '')
            + ' · ' + formatBytes(analysis.TotalBytes)
            + ' · ' + (analysis.SuggestedContentTypeLabel ? 'type détecté : ' + analysis.SuggestedContentTypeLabel : 'type non reconnu');
    }

    function newId() {
        return Math.random().toString(36).substring(2, 10) + Date.now().toString(36).substring(4);
    }

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
        if (task.DestinationName) {
            meta.push('→ ' + task.DestinationName);
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

        view.querySelector('.torry-btn-refresh-library').addEventListener('click', function () {
            instance.refreshLibrary();
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

        // Explorateur de fichiers
        this.fs = { root: null, path: '' };
        view.querySelector('.torry-fs-root').addEventListener('change', function (e) {
            instance.browse(e.target.value, '');
        });
        view.querySelector('.torry-fs-reload').addEventListener('click', function () { instance.browse(instance.fs.root, instance.fs.path); });
        view.querySelector('.torry-fs-mkdir').addEventListener('click', function () { instance.createFolder(); });
        view.querySelector('.torry-fs-crumbs').addEventListener('click', function (e) {
            var b = e.target.closest('button[data-path]');
            if (b) {
                instance.browse(instance.fs.root, b.getAttribute('data-path'));
            }
        });
        var fsList = view.querySelector('.torry-fs-list');
        fsList.addEventListener('click', function (e) {
            if (e.target.closest('.torry-fs-check')) {
                return; // géré par « change »
            }
            var b = e.target.closest('button[data-fs]');
            if (b) {
                var path = b.getAttribute('data-path');
                var action = b.getAttribute('data-fs');
                if (action === 'open') {
                    instance.browse(instance.fs.root, path);
                } else if (action === 'move') {
                    instance.openMove([path]);
                } else if (action === 'rename') {
                    instance.renameEntry(path);
                } else if (action === 'delete') {
                    instance.openDeleteFiles([path]);
                }
                return;
            }
            // Clic n'importe où sur la ligne d'un dossier : on l'ouvre.
            var row = e.target.closest('.torry-fs-dir');
            if (row) {
                instance.browse(instance.fs.root, row.getAttribute('data-path'));
            }
        });
        fsList.addEventListener('change', function (e) {
            var box = e.target.closest('.torry-fs-check');
            if (box) {
                instance.toggleSelection(box.getAttribute('data-path'), box.checked);
            }
        });
        view.querySelector('.torry-fs-all').addEventListener('change', function (e) {
            instance.selectAll(e.target.checked);
        });
        view.querySelector('.torry-fs-clear-sel').addEventListener('click', function () { instance.selectAll(false); });
        view.querySelector('.torry-fs-move-sel').addEventListener('click', function () {
            var paths = Object.keys(instance.fs.selected || {});
            if (paths.length) {
                instance.openMove(paths);
            }
        });
        view.querySelector('.torry-fs-delete-sel').addEventListener('click', function () {
            var paths = Object.keys(instance.fs.selected || {});
            if (paths.length) {
                instance.openDeleteFiles(paths);
            }
        });
        var fsDelModal = view.querySelector('.torry-fsdel-modal');
        view.querySelector('.torry-fsdel-cancel').addEventListener('click', function () { fsDelModal.hidden = true; });
        view.querySelector('.torry-fsdel-confirm').addEventListener('input', function (e) {
            view.querySelector('.torry-fsdel-ok').disabled = e.target.value.trim().toUpperCase() !== 'SUPPRIMER';
        });
        view.querySelector('.torry-fsdel-ok').addEventListener('click', function () { instance.confirmDeleteFiles(); });
        view.querySelector('.torry-fs-up').addEventListener('click', function () {
            if (instance.fs.path) {
                instance.browse(instance.fs.root, parentPath(instance.fs.path));
            }
        });

        // Fenêtre de déplacement
        var moveModal = view.querySelector('.torry-move-modal');
        view.querySelector('.torry-move-cancel').addEventListener('click', function () { moveModal.hidden = true; });
        view.querySelector('.torry-move-root').addEventListener('change', function (e) { instance.browseMoveTarget(e.target.value, ''); });
        view.querySelector('.torry-move-crumbs').addEventListener('click', function (e) {
            var b = e.target.closest('button[data-path]');
            if (b) {
                instance.browseMoveTarget(instance.move.root, b.getAttribute('data-path'));
            }
        });
        view.querySelector('.torry-move-folders').addEventListener('click', function (e) {
            var b = e.target.closest('button[data-path]');
            if (b) {
                instance.browseMoveTarget(instance.move.root, b.getAttribute('data-path'));
            }
        });
        view.querySelector('.torry-move-name').addEventListener('input', function () { instance.updateMoveTarget(); });
        view.querySelector('.torry-move-ok').addEventListener('click', function () { instance.confirmMove(); });
        view.querySelector('.torry-move-configure').addEventListener('click', function () {
            moveModal.hidden = true;
            instance.showTab('settings');
            var dests = view.querySelector('.torry-dests');
            if (dests && dests.scrollIntoView) {
                dests.scrollIntoView({ block: 'center' });
            }
        });

        // Fenêtre de saisie
        var promptModal = view.querySelector('.torry-prompt-modal');
        view.querySelector('.torry-prompt-cancel').addEventListener('click', function () { instance.closePrompt(null); });
        view.querySelector('.torry-prompt-ok').addEventListener('click', function () {
            instance.closePrompt(view.querySelector('.torry-prompt-input').value);
        });
        view.querySelector('.torry-prompt-input').addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                instance.closePrompt(e.target.value);
            }
        });

        // Dossiers de destination (réglages)
        view.querySelector('.torry-dest-add').addEventListener('click', function () {
            instance.addDestinationRow({ Id: newId(), Name: '', ContentType: 'movies', Path: '' });
        });
        view.querySelector('.torry-dest-import').addEventListener('click', function () { instance.importLibraries(); });
        view.querySelector('.torry-dest-prepare').addEventListener('click', function () { instance.prepareLibraries(); });
        view.querySelector('.torry-dests').addEventListener('click', function (e) {
            var b = e.target.closest('button[data-dest-remove]');
            if (b) {
                b.closest('.torry-dest-row').remove();
            }
        });

        view.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') {
                filesModal.hidden = true;
                deleteModal.hidden = true;
                moveModal.hidden = true;
                fsDelModal.hidden = true;
                if (!promptModal.hidden) {
                    instance.closePrompt(null);
                }
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
        this.activeTab = name;
        if (name === 'files') {
            this.renderRootSelect(this.view.querySelector('.torry-fs-root'), this.fs.root);
            this.browse(this.fs.root || (this.roots && this.roots[0] && this.roots[0].Id), this.fs.path || '');
            this.refreshOperations();
        }
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
            instance.roots = status.Roots || [];
            if (instance.activeTab === 'files') {
                instance.refreshOperations();
            }
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

    View.prototype.refreshLibrary = function () {
        var instance = this;
        var button = this.view.querySelector('.torry-btn-refresh-library');
        button.disabled = true;
        loading.show();
        api('POST', 'Torry/Library/Refresh', {}).then(function (result) {
            instance.notify((result && result.Message) || 'Actualisation demandée.', !(result && result.Refreshed));
        }, function (err) {
            return readError(err).then(function (m) { instance.notify(m, true); });
        }).then(function () {
            loading.hide();
            // Évite les demandes en rafale : le bouton est réactivé après quelques secondes.
            setTimeout(function () { button.disabled = false; }, 5000);
        });
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
        api('GET', 'Torry/Tasks/' + encodeURIComponent(id) + '/Files').then(function (result) {
            instance.selectTaskId = id;
            instance.files = (result && result.Files) || [];
            instance.renderDestinationSelect(result && result.SuggestedDestinationId, result && result.SuggestedContentType);
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
        var destination = this.view.querySelector('.torry-files-destination').value || '';
        api('POST', 'Torry/Tasks/' + encodeURIComponent(this.selectTaskId) + '/Start', { SelectedFiles: indices.join(','), DestinationId: destination }).then(function () {
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
        var instance = this;
        return api('GET', 'Torry/Settings').then(function (settings) {
            instance.renderDestinationRows(settings.Destinations || []);
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
        body.DestinationsJson = JSON.stringify(this.collectDestinations());
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

    // ---- destinations (sélection des fichiers)

    View.prototype.destinationRoots = function () {
        return (this.roots || []).filter(function (r) { return r.Id !== 'complete'; });
    };

    View.prototype.renderDestinationSelect = function (suggestedId, suggestedType) {
        var select = this.view.querySelector('.torry-files-destination');
        var html = '<option value="">Téléchargements terminés</option>';
        this.destinationRoots().forEach(function (r) {
            html += '<option value="' + escapeHtml(r.Id) + '">' + escapeHtml(r.Name + ' (' + (r.ContentTypeLabel || '') + ')') + '</option>';
        });
        select.innerHTML = html;
        select.value = suggestedId || '';
        select.title = suggestedType ? 'Type détecté : ' + contentTypeLabel(suggestedType) : '';
    };

    View.prototype.renderRootSelect = function (select, selected) {
        var html = '';
        (this.roots || []).forEach(function (r) {
            var label = r.Name + (r.ContentTypeLabel ? ' (' + r.ContentTypeLabel + ')' : '') + (r.Available ? '' : ' — introuvable');
            html += '<option value="' + escapeHtml(r.Id) + '">' + escapeHtml(label) + '</option>';
        });
        select.innerHTML = html;
        if (selected) {
            select.value = selected;
        }
    };

    // ---- explorateur de fichiers

    View.prototype.browse = function (rootId, path) {
        var instance = this;
        if (!rootId) {
            return Promise.resolve();
        }
        var changed = rootId !== this.fs.root || (path || '') !== this.fs.path;
        this.fs.root = rootId;
        this.fs.path = path || '';
        if (changed || !this.fs.selected) {
            this.fs.selected = {};
        }
        this.view.querySelector('.torry-fs-root').value = rootId;
        this.view.querySelector('.torry-fs-up').disabled = !this.fs.path;
        var message = this.view.querySelector('.torry-fs-message');
        return api('GET', 'Torry/Files?Root=' + encodeURIComponent(rootId) + '&Path=' + encodeURIComponent(this.fs.path)).then(function (listing) {
            message.innerHTML = '';
            instance.listing = listing;
            // Après un rafraîchissement, on ne garde sélectionnés que les éléments encore présents.
            var present = {};
            (listing.Entries || []).forEach(function (e) { present[e.Path] = true; });
            Object.keys(instance.fs.selected).forEach(function (p) {
                if (!present[p]) {
                    delete instance.fs.selected[p];
                }
            });
            instance.renderCrumbs(instance.view.querySelector('.torry-fs-crumbs'), listing.RootName, listing.Path);
            instance.renderListing(listing);
        }, function (err) {
            return readError(err).then(function (m) {
                message.innerHTML = '<div class="torry-error">' + escapeHtml(m) + '</div>';
                instance.view.querySelector('.torry-fs-list').innerHTML = '';
                instance.updateSelectionUi();
            });
        });
    };

    View.prototype.renderCrumbs = function (container, rootName, path) {
        var html = '<button type="button" data-path="">' + escapeHtml(rootName) + '</button>';
        var acc = '';
        (path ? path.split('/') : []).forEach(function (part) {
            acc = joinPath(acc, part);
            html += '<span>›</span><button type="button" data-path="' + escapeHtml(acc) + '">' + escapeHtml(part) + '</button>';
        });
        container.innerHTML = html;
    };

    View.prototype.renderListing = function (listing) {
        var selected = this.fs.selected || {};
        var html = '';
        (listing.Entries || []).forEach(function (e) {
            var isSel = !!selected[e.Path];
            var meta = e.IsDirectory ? 'Dossier' : formatBytes(e.Size);
            html += '<div class="torry-fs-row' + (e.IsDirectory ? ' torry-fs-dir' : '') + (isSel ? ' is-selected' : '') + '" data-path="' + escapeHtml(e.Path) + '"'
                + (e.IsDirectory ? ' title="Ouvrir le dossier"' : '') + '>'
                + '<input type="checkbox" class="torry-fs-check" data-path="' + escapeHtml(e.Path) + '"' + (isSel ? ' checked' : '')
                + ' aria-label="Sélectionner ' + escapeHtml(e.Name) + '" />'
                + '<i class="md-icon torry-fs-icon">' + (ICONS[e.Category] || 'description') + '</i>'
                + '<div class="torry-fs-name">' + escapeHtml(e.Name) + '</div>'
                + '<div class="torry-fs-meta">' + escapeHtml(meta) + '</div>'
                + '<button type="button" class="torry-btn torry-btn-small" data-fs="move" data-path="' + escapeHtml(e.Path) + '">Déplacer…</button>'
                + '<button type="button" class="torry-btn torry-btn-small" data-fs="rename" data-path="' + escapeHtml(e.Path) + '">Renommer…</button>'
                + '<button type="button" class="torry-btn torry-btn-small" data-fs="delete" data-path="' + escapeHtml(e.Path) + '" title="Supprimer définitivement">Supprimer…</button>'
                + '<i class="md-icon torry-fs-chevron">' + (e.IsDirectory ? 'chevron_right' : '') + '</i>'
                + '</div>';
        });
        if (!(listing.Entries || []).length) {
            html += '<div class="torry-empty" style="padding:1em;">Dossier vide.</div>';
        }
        if (listing.Truncated) {
            html += '<div class="torry-note" style="padding:.5em .7em;">Liste tronquée (2000 éléments maximum).</div>';
        }
        this.view.querySelector('.torry-fs-list').innerHTML = html;
        this.updateSelectionUi();
    };

    View.prototype.openDeleteFiles = function (paths) {
        var instance = this;
        paths = (paths || []).slice();
        var entries = paths.map(function (p) {
            return instance.findEntry(p) || { Name: p.split('/').pop(), Path: p, IsDirectory: false };
        });
        this.fsDelete = { root: this.fs.root, paths: paths };
        this.view.querySelector('.torry-fsdel-title').textContent = paths.length === 1
            ? 'Supprimer « ' + entries[0].Name + ' » définitivement'
            : 'Supprimer ' + paths.length + ' éléments définitivement';
        this.view.querySelector('.torry-fsdel-items').innerHTML = entries.map(function (e) {
            return '<div>• ' + escapeHtml(e.Name) + (e.IsDirectory ? ' <span class="torry-fs-meta">(dossier et tout son contenu)</span>' : '') + '</div>';
        }).join('');
        this.view.querySelector('.torry-fsdel-summary').textContent = 'Calcul de la taille…';
        var input = this.view.querySelector('.torry-fsdel-confirm');
        input.value = '';
        this.view.querySelector('.torry-fsdel-ok').disabled = true;
        this.view.querySelector('.torry-fsdel-modal').hidden = false;
        setTimeout(function () { input.focus(); }, 50);
        Promise.all(paths.slice(0, 30).map(function (p) {
            return api('GET', 'Torry/Files/Analyze?Root=' + encodeURIComponent(instance.fs.root) + '&Path=' + encodeURIComponent(p))
                .then(function (a) { return a; }, function () { return null; });
        })).then(function (results) {
            var list = results.filter(function (a) { return !!a; });
            var files = list.reduce(function (n, a) { return n + (a.FileCount || 0); }, 0);
            var bytes = list.reduce(function (n, a) { return n + (a.TotalBytes || 0); }, 0);
            instance.view.querySelector('.torry-fsdel-summary').textContent = files + ' fichier(s) · ' + formatBytes(bytes) + ' seront effacés.';
        });
    };

    View.prototype.confirmDeleteFiles = function () {
        var instance = this;
        var d = this.fsDelete;
        var confirmation = this.view.querySelector('.torry-fsdel-confirm').value.trim().toUpperCase();
        if (!d || confirmation !== 'SUPPRIMER') {
            return;
        }
        this.view.querySelector('.torry-fsdel-modal').hidden = true;
        loading.show();
        var errors = [];
        var chain = Promise.resolve();
        d.paths.forEach(function (p) {
            chain = chain.then(function () {
                return api('POST', 'Torry/Files/Delete', { Root: d.root, Path: p, Confirmation: confirmation })
                    .then(null, function (err) {
                        return readError(err).then(function (msg) { errors.push(p.split('/').pop() + ' : ' + msg); });
                    });
            });
        });
        chain.then(function () {
            loading.hide();
            var launched = d.paths.length - errors.length;
            instance.notifyFiles(launched + ' suppression(s) lancée(s).' + (errors.length ? ' Refusées : ' + errors.join(' ; ') : ''), errors.length > 0);
            instance.selectAll(false);
            instance.refreshOperations();
        });
    };

    View.prototype.toggleSelection = function (path, on) {
        if (on) {
            this.fs.selected[path] = true;
        } else {
            delete this.fs.selected[path];
        }
        this.updateSelectionUi();
    };

    View.prototype.selectAll = function (on) {
        var instance = this;
        this.fs.selected = {};
        if (on) {
            ((this.listing && this.listing.Entries) || []).forEach(function (e) { instance.fs.selected[e.Path] = true; });
        }
        this.view.querySelectorAll('.torry-fs-list .torry-fs-check').forEach(function (box) {
            box.checked = !!on;
        });
        this.updateSelectionUi();
    };

    View.prototype.updateSelectionUi = function () {
        var selected = this.fs.selected || {};
        var count = Object.keys(selected).length;
        var total = ((this.listing && this.listing.Entries) || []).length;
        this.view.querySelectorAll('.torry-fs-list .torry-fs-row[data-path]').forEach(function (row) {
            row.classList.toggle('is-selected', !!selected[row.getAttribute('data-path')]);
        });
        var all = this.view.querySelector('.torry-fs-all');
        all.checked = total > 0 && count === total;
        all.indeterminate = count > 0 && count < total;
        all.disabled = total === 0;
        this.view.querySelector('.torry-fs-selcount').textContent = count ? count + ' élément(s) sélectionné(s)' : '';
        var move = this.view.querySelector('.torry-fs-move-sel');
        move.disabled = count === 0;
        move.textContent = count > 1 ? 'Déplacer les ' + count + ' éléments…' : 'Déplacer la sélection…';
        this.view.querySelector('.torry-fs-clear-sel').disabled = count === 0;
        var del = this.view.querySelector('.torry-fs-delete-sel');
        del.disabled = count === 0;
        del.textContent = count > 1 ? 'Supprimer (' + count + ')…' : 'Supprimer…';
    };

    View.prototype.findEntry = function (path) {
        return ((this.listing && this.listing.Entries) || []).filter(function (e) { return e.Path === path; })[0];
    };

    View.prototype.prompt = function (title, value) {
        var instance = this;
        var modal = this.view.querySelector('.torry-prompt-modal');
        var input = this.view.querySelector('.torry-prompt-input');
        this.view.querySelector('.torry-prompt-title').textContent = title;
        input.value = value || '';
        modal.hidden = false;
        setTimeout(function () { input.focus(); input.select(); }, 50);
        return new Promise(function (resolve) { instance.promptResolve = resolve; });
    };

    View.prototype.closePrompt = function (value) {
        this.view.querySelector('.torry-prompt-modal').hidden = true;
        var resolve = this.promptResolve;
        this.promptResolve = null;
        if (resolve) {
            resolve(value == null ? null : String(value).trim());
        }
    };

    View.prototype.createFolder = function () {
        var instance = this;
        this.prompt('Nouveau dossier', '').then(function (name) {
            if (!name) {
                return;
            }
            api('POST', 'Torry/Files/Folder', { Root: instance.fs.root, Path: instance.fs.path, Name: name }).then(function () {
                return instance.browse(instance.fs.root, instance.fs.path);
            }, function (err) {
                return readError(err).then(function (m) { instance.notifyFiles(m, true); });
            });
        });
    };

    View.prototype.renameEntry = function (path) {
        var instance = this;
        var entry = this.findEntry(path);
        var current = entry ? entry.Name : path.split('/').pop();
        this.prompt('Renommer « ' + current + ' »', current).then(function (name) {
            if (!name || name === current) {
                return;
            }
            instance.submitMove(instance.fs.root, path, instance.fs.root, parentPath(path), name);
        });
    };

    View.prototype.notifyFiles = function (message, isError) {
        var box = this.view.querySelector('.torry-fs-message');
        box.innerHTML = '<div class="' + (isError ? 'torry-error' : 'torry-warn') + '">' + escapeHtml(message) + '</div>';
        clearTimeout(this.fsNoticeTimer);
        this.fsNoticeTimer = setTimeout(function () { box.innerHTML = ''; }, isError ? 12000 : 6000);
    };

    View.prototype.openMove = function (paths) {
        var instance = this;
        paths = (paths || []).slice();
        var entries = paths.map(function (p) {
            return instance.findEntry(p) || { Name: p.split('/').pop(), Path: p, IsDirectory: true };
        });
        var single = paths.length === 1;
        this.move = { sourceRoot: this.fs.root, sourcePaths: paths, entries: entries, root: null, path: '', analysis: null };
        this.view.querySelector('.torry-move-title').textContent = single
            ? 'Déplacer « ' + entries[0].Name + ' »'
            : 'Déplacer ' + paths.length + ' éléments';
        this.view.querySelector('.torry-move-name').value = single ? entries[0].Name : '';
        this.view.querySelector('.torry-move-name-step').hidden = !single;
        var items = this.view.querySelector('.torry-move-items');
        items.hidden = single;
        items.innerHTML = single ? '' : entries.map(function (e) {
            return '<div>• ' + escapeHtml(e.Name) + (e.IsDirectory ? ' <span class="torry-fs-meta">(dossier)</span>' : '') + '</div>';
        }).join('');
        this.view.querySelector('.torry-move-summary').textContent = 'Analyse du contenu…';
        this.view.querySelector('.torry-move-hint').textContent = '';
        this.view.querySelector('.torry-move-nodest').hidden = this.destinationRoots().length > 0;
        this.view.querySelector('.torry-move-modal').hidden = false;

        var analyze = function (p) {
            return api('GET', 'Torry/Files/Analyze?Root=' + encodeURIComponent(instance.fs.root) + '&Path=' + encodeURIComponent(p))
                .then(function (a) { return a; }, function () { return null; });
        };
        Promise.all(paths.slice(0, 30).map(analyze)).then(function (results) {
            var analysis = mergeAnalyses(results.filter(function (a) { return !!a; }), paths.length);
            instance.move.analysis = analysis;
            instance.view.querySelector('.torry-move-summary').textContent = single
                ? describeAnalysis(analysis, entries[0])
                : describeSelection(analysis, paths.length);
            instance.renderMoveRoots();
            var select = instance.view.querySelector('.torry-move-root');
            select.value = analysis && analysis.SuggestedRootId ? analysis.SuggestedRootId : instance.fs.root;
            if (!select.value && select.options.length) {
                select.selectedIndex = 0;
            }
            // Destination conseillée : on part de sa racine ; sinon du dossier courant.
            var startPath = (analysis && analysis.SuggestedRootId) ? '' : (select.value === instance.fs.root ? instance.fs.path : '');
            instance.browseMoveTarget(select.value, startPath);
        });
    };

    View.prototype.browseMoveTarget = function (rootId, path) {
        var instance = this;
        this.move.root = rootId;
        this.move.path = path || '';
        this.view.querySelector('.torry-move-root').value = rootId;
        this.renderMoveHint();
        var container = this.view.querySelector('.torry-move-folders');
        return api('GET', 'Torry/Files?Root=' + encodeURIComponent(rootId) + '&Path=' + encodeURIComponent(this.move.path)).then(function (listing) {
            instance.move.rootName = listing.RootName;
            instance.renderCrumbs(instance.view.querySelector('.torry-move-crumbs'), listing.RootName, listing.Path);
            var html = '';
            if (listing.Path) {
                html += '<div class="torry-fs-row"><i class="md-icon torry-fs-icon">arrow_upward</i><div class="torry-fs-name"><button type="button" data-path="'
                    + escapeHtml(parentPath(listing.Path)) + '">Remonter d\'un niveau</button></div></div>';
            }
            var folders = (listing.Entries || []).filter(function (e) { return e.IsDirectory; });
            folders.forEach(function (e) {
                var isSelf = instance.move.sourceRoot === rootId && instance.move.sourcePaths.some(function (p) {
                    return e.Path === p || e.Path.indexOf(p + '/') === 0;
                });
                html += '<div class="torry-fs-row"><i class="md-icon torry-fs-icon">folder</i><div class="torry-fs-name">'
                    + (isSelf ? escapeHtml(e.Name) + ' <span class="torry-fs-meta">(élément déplacé)</span>'
                        : '<button type="button" data-path="' + escapeHtml(e.Path) + '">' + escapeHtml(e.Name) + '</button>')
                    + '</div>' + (isSelf ? '' : '<span class="torry-fs-meta">ouvrir ›</span>') + '</div>';
            });
            if (!folders.length) {
                html += '<div class="torry-note" style="padding:.6em .7em;">Aucun sous-dossier : ' + (instance.move.sourcePaths.length > 1 ? 'les éléments seront placés' : 'l\'élément sera placé') + ' directement ici.</div>';
            }
            container.innerHTML = html;
            instance.updateMoveTarget();
        }, function (err) {
            return readError(err).then(function (m) {
                container.innerHTML = '<div class="torry-error">' + escapeHtml(m) + '</div>';
                instance.view.querySelector('.torry-move-ok').disabled = true;
            });
        });
    };

    View.prototype.renderMoveRoots = function () {
        var analysis = this.move.analysis;
        var compatible = (analysis && analysis.CompatibleContentTypes) || [];
        var html = '';
        (this.roots || []).forEach(function (r) {
            var label = r.Name + (r.ContentTypeLabel ? ' (' + r.ContentTypeLabel + ')' : '');
            if (analysis && r.Id === analysis.SuggestedRootId) {
                label += ' — conseillé';
            } else if (r.ContentType && compatible.length && compatible.indexOf(r.ContentType) < 0) {
                label += ' — type différent';
            }
            if (!r.Available) {
                label += ' — introuvable';
            }
            html += '<option value="' + escapeHtml(r.Id) + '">' + escapeHtml(label) + '</option>';
        });
        this.view.querySelector('.torry-move-root').innerHTML = html;
    };

    View.prototype.renderMoveHint = function () {
        var analysis = this.move.analysis;
        var root = (this.roots || []).filter(function (r) { return r.Id === this.move.root; }, this)[0];
        var hint = '';
        if (root && analysis && root.ContentType && analysis.CompatibleContentTypes && analysis.CompatibleContentTypes.length
            && analysis.CompatibleContentTypes.indexOf(root.ContentType) < 0) {
            hint = 'Attention : « ' + root.Name + ' » est une bibliothèque ' + (root.ContentTypeLabel || '') + ' ; Emby risque de ne pas afficher ce contenu.';
        } else if (root && root.Id === 'complete') {
            hint = 'Dossier des téléchargements terminés de Torry.';
        }
        this.view.querySelector('.torry-move-hint').textContent = hint;
    };

    View.prototype.updateMoveTarget = function () {
        var single = this.move.sourcePaths.length === 1;
        var name = this.view.querySelector('.torry-move-name').value.trim();
        var folders = [this.move.rootName || ''].concat(this.move.path ? this.move.path.split('/') : []);
        var result = this.view.querySelector('.torry-move-result');
        result.innerHTML = (single ? 'Sera déplacé dans : ' : this.move.sourcePaths.length + ' éléments seront déplacés dans : ')
            + '<strong>' + escapeHtml(folders.join(' › ')) + '</strong>'
            + (single && name ? '<span class="torry-fs-meta">sous le nom « ' + escapeHtml(name) + ' »</span>' : '');
        var ok = this.view.querySelector('.torry-move-ok');
        ok.disabled = single && !name;
        ok.textContent = 'Déplacer dans « ' + folders[folders.length - 1] + ' »';
    };

    View.prototype.confirmMove = function () {
        var instance = this;
        var m = this.move;
        var single = m.sourcePaths.length === 1;
        var name = this.view.querySelector('.torry-move-name').value.trim();
        this.view.querySelector('.torry-move-modal').hidden = true;
        if (single) {
            this.submitMove(m.sourceRoot, m.sourcePaths[0], m.root, m.path, name === m.entries[0].Name ? '' : name);
            return;
        }
        // Plusieurs éléments : un déplacement par élément, exécutés l'un après l'autre côté serveur.
        loading.show();
        var errors = [];
        var chain = Promise.resolve();
        m.sourcePaths.forEach(function (p) {
            chain = chain.then(function () {
                return api('POST', 'Torry/Files/Move', { SourceRoot: m.sourceRoot, SourcePath: p, TargetRoot: m.root, TargetPath: m.path, NewName: '' })
                    .then(null, function (err) {
                        return readError(err).then(function (msg) { errors.push(p.split('/').pop() + ' : ' + msg); });
                    });
            });
        });
        chain.then(function () {
            loading.hide();
            var launched = m.sourcePaths.length - errors.length;
            instance.notifyFiles(launched + ' déplacement(s) lancé(s).' + (errors.length ? ' Refusés : ' + errors.join(' ; ') : ' Suivez la progression ci-dessous.'), errors.length > 0);
            instance.selectAll(false);
            instance.refreshOperations();
        });
    };

    View.prototype.submitMove = function (sourceRoot, sourcePath, targetRoot, targetPath, newName) {
        var instance = this;
        loading.show();
        api('POST', 'Torry/Files/Move', {
            SourceRoot: sourceRoot,
            SourcePath: sourcePath,
            TargetRoot: targetRoot,
            TargetPath: targetPath,
            NewName: newName || ''
        }).then(function () {
            instance.notifyFiles('Déplacement lancé. Suivez sa progression ci-dessous.');
            instance.refreshOperations();
        }, function (err) {
            return readError(err).then(function (m) { instance.notifyFiles(m, true); });
        }).then(function () { loading.hide(); });
    };

    View.prototype.refreshOperations = function () {
        var instance = this;
        return api('GET', 'Torry/Files/Operations').then(function (ops) {
            ops = ops || [];
            var json = JSON.stringify(ops);
            if (json === instance.lastOpsJson) {
                return;
            }
            var firstLoad = !instance.finishedOps;
            instance.finishedOps = instance.finishedOps || {};
            var newlyFinished = false;
            ops.forEach(function (o) {
                if ((o.State === 'Done' || o.State === 'Error') && !instance.finishedOps[o.Id]) {
                    instance.finishedOps[o.Id] = true;
                    newlyFinished = true;
                }
            });
            instance.lastOpsJson = json;
            instance.lastOps = ops;
            var html = ops.map(function (o) {
                var pct = o.BytesTotal > 0 ? Math.min(100, 100 * o.BytesDone / o.BytesTotal) : (o.State === 'Done' ? 100 : 0);
                var label = { Pending: 'En attente', Running: o.Copying ? 'Copie en cours' : 'En cours', Done: o.Kind === 'delete' ? 'Supprimé' : 'Terminé', Error: 'Erreur' }[o.State] || o.State;
                return '<div class="torry-task"><div class="torry-task-head"><div class="torry-task-name">' + escapeHtml(o.Source) + '</div>'
                    + '<span class="' + (o.State === 'Error' ? 'torry-badge torry-badge-err' : (o.State === 'Done' ? 'torry-badge torry-badge-ok' : 'torry-badge')) + '">' + escapeHtml(label) + '</span></div>'
                    + (o.Kind === 'delete' ? '<div class="torry-note">Suppression définitive</div>' : '<div class="torry-note">→ ' + escapeHtml(o.Target) + '</div>')
                    + (o.State === 'Running' ? '<div class="torry-bar"><div style="width:' + pct.toFixed(1) + '%"></div></div><div class="torry-meta"><span>'
                        + escapeHtml(formatBytes(o.BytesDone) + ' / ' + formatBytes(o.BytesTotal)) + '</span></div>' : '')
                    + (o.Message ? '<div class="' + (o.State === 'Error' ? 'torry-error' : 'torry-note') + '">' + escapeHtml(o.Message) + '</div>' : '')
                    + '</div>';
            }).join('');
            instance.view.querySelector('.torry-fs-ops').innerHTML = html || '<div class="torry-empty" style="padding:.6em 0;">Aucun déplacement récent.</div>';
            if (newlyFinished && !firstLoad && instance.activeTab === 'files') {
                instance.browse(instance.fs.root, instance.fs.path);
            }
        });
    };

    // ---- réglages : dossiers de destination

    View.prototype.renderDestinationRows = function (destinations) {
        this.view.querySelector('.torry-dests').innerHTML = '';
        destinations.forEach(this.addDestinationRow, this);
    };

    View.prototype.addDestinationRow = function (d) {
        var row = document.createElement('div');
        row.className = 'torry-dest-row';
        row.setAttribute('data-id', d.Id || newId());
        var options = CONTENT_TYPES.map(function (t) {
            return '<option value="' + t[0] + '"' + (t[0] === d.ContentType ? ' selected' : '') + '>' + escapeHtml(t[1]) + '</option>';
        }).join('');
        row.innerHTML = '<input class="torry-input" data-dest="Name" type="text" placeholder="Nom (ex. Films)" value="' + escapeHtml(d.Name || '') + '" />'
            + '<select class="torry-input" data-dest="ContentType">' + options + '</select>'
            + '<input class="torry-input" data-dest="Path" type="text" placeholder="/media/films" spellcheck="false" value="' + escapeHtml(d.Path || '') + '" />'
            + '<button type="button" class="torry-btn torry-btn-small" data-dest-remove="1" title="Retirer">Retirer</button>';
        this.view.querySelector('.torry-dests').appendChild(row);
    };

    View.prototype.collectDestinations = function () {
        var result = [];
        this.view.querySelectorAll('.torry-dest-row').forEach(function (row) {
            var name = row.querySelector('[data-dest="Name"]').value.trim();
            var path = row.querySelector('[data-dest="Path"]').value.trim();
            if (!name && !path) {
                return;
            }
            result.push({
                Id: row.getAttribute('data-id'),
                Name: name,
                ContentType: row.querySelector('[data-dest="ContentType"]').value,
                Path: path
            });
        });
        return result;
    };

    View.prototype.prepareLibraries = function () {
        var instance = this;
        var ok = window.confirm('Torry va créer un dossier par bibliothèque Emby (dans « bibliotheques », à côté du dossier « en cours ») '
            + 'et l\'ajouter comme emplacement supplémentaire de chaque bibliothèque. Vos dossiers existants ne sont pas modifiés. Continuer ?');
        if (!ok) {
            return;
        }
        var box = this.view.querySelector('.torry-prepare-result');
        box.innerHTML = '';
        loading.show();
        api('POST', 'Torry/Libraries/Prepare', {}).then(function (result) {
            var html = '<div class="torry-warn" style="margin-top:.8em;">' + escapeHtml(result.Message || 'Terminé.') + '<ul style="margin:.4em 0 0;padding-left:1.2em;">';
            (result.Libraries || []).forEach(function (l) {
                var status = l.Error ? 'erreur : ' + l.Error
                    : (l.Attached ? 'emplacement ajouté à la bibliothèque' : (l.AlreadyAttached ? 'déjà prête' : 'prête'));
                html += '<li><strong>' + escapeHtml(l.Name) + '</strong> (' + escapeHtml(l.ContentTypeLabel || '') + ') → <code>'
                    + escapeHtml(l.Path || '') + '</code> : ' + escapeHtml(status) + '</li>';
            });
            html += '</ul>';
            if ((result.RemovedDestinations || []).length) {
                html += '<div style="margin-top:.4em;">Destinations retirées (Torry ne pouvait pas y écrire) : ' + escapeHtml(result.RemovedDestinations.join(', ')) + '.</div>';
            }
            box.innerHTML = html + '</div>';
            instance.view.querySelector('.torry-settings-message').innerHTML = '';
            instance.lastStatusJson = null;
            return Promise.all([instance.loadSettings(), instance.refresh()]);
        }, function (err) {
            return readError(err).then(function (m) {
                box.innerHTML = '<div class="torry-error" style="margin-top:.8em;">' + escapeHtml(m) + '</div>';
            });
        }).then(function () { loading.hide(); });
    };

    View.prototype.importLibraries = function () {
        var instance = this;
        api('GET', 'Torry/Libraries').then(function (libraries) {
            var existing = instance.collectDestinations().map(function (d) { return d.Path; });
            var names = instance.collectDestinations().map(function (d) { return d.Name.toLowerCase(); });
            var added = 0;
            (libraries || []).forEach(function (lib) {
                (lib.Locations || []).forEach(function (location, i) {
                    if (existing.indexOf(location) >= 0) {
                        return;
                    }
                    var name = lib.Name + ((lib.Locations || []).length > 1 ? ' ' + (i + 1) : '');
                    while (names.indexOf(name.toLowerCase()) >= 0) {
                        name += ' bis';
                    }
                    names.push(name.toLowerCase());
                    var type = CONTENT_TYPES.some(function (t) { return t[0] === lib.ContentType; }) ? lib.ContentType : 'mixed';
                    instance.addDestinationRow({ Id: newId(), Name: name, ContentType: type, Path: location });
                    added++;
                });
            });
            var message = instance.view.querySelector('.torry-settings-message');
            message.innerHTML = '<div class="torry-warn">' + escapeHtml(added
                ? added + ' bibliothèque(s) ajoutée(s). Vérifiez puis cliquez sur « Enregistrer ».'
                : 'Aucune nouvelle bibliothèque à importer.') + '</div>';
        }, function (err) {
            return readError(err).then(function (m) { instance.notify(m, true); });
        });
    };

    return View;
});
