using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;
using PetitOurs.Torry.Core;
using PetitOurs.Torry.Files;
using PetitOurs.Torry.Persistence;

namespace PetitOurs.Torry.Engine
{
    /// <summary>
    /// Orchestrateur des tâches : ajout, sélection, file d'attente, finalisation, partage, reprise après redémarrage.
    /// Toutes les mutations passent par un verrou asynchrone unique ; la lecture de l'état utilise un instantané
    /// recalculé après chaque mutation (aucun blocage des requêtes HTTP d'Emby).
    /// </summary>
    public sealed class TorrentTaskManager : IDisposable
    {
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(15);

        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly List<TaskRuntime> _tasks = new List<TaskRuntime>();
        private readonly TaskStore _store;
        private readonly ITorryLog _log;
        private readonly ILibraryNotifier _library;
        private readonly TimeSpan _tickInterval;

        private TorryOptions _options;
        private FileManager _files;
        private TorrentEngineHost _engine;
        private CancellationTokenSource _loopCts;
        private Task _loopTask;
        private volatile StatusView _status = new StatusView();
        private DateTime _lastSaveUtc = DateTime.MinValue;
        private bool _dirty;
        private string _startupError;
        private readonly List<string> _warnings = new List<string>();
        private bool _started;
        private bool _stopping;
        private DateTime _libraryCheckUtc = DateTime.MinValue;
        private bool _completedInLibrary;

        public TorrentTaskManager(TaskStore store, TorryOptions options, ITorryLog log, ILibraryNotifier library, TimeSpan? tickInterval = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _options = (options ?? new TorryOptions()).Clone();
            _log = log ?? new NullLog();
            _library = library ?? new NullLibraryNotifier();
            _tickInterval = tickInterval ?? TimeSpan.FromSeconds(1);
        }

        public bool IsStarted => _started;

        public TorryOptions Options => _options.Clone();

        /// <summary>Exposé pour les tests (ajout manuel de pairs locaux).</summary>
        internal ClientEngine Engine => _engine?.Engine;

        internal TorrentManager GetManagerForTests(string id) => _tasks.FirstOrDefault(t => t.Record.Id == id)?.Manager;

        // ------------------------------------------------------------------ cycle de vie

        /// <summary>Démarre le moteur et restaure les tâches. Ne lève pas : les erreurs sont exposées dans le statut.</summary>
        public async Task StartAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_started)
                {
                    return;
                }

                _store.EnsureDirectories();
                var records = _store.Load(out var warning);
                if (warning != null)
                {
                    _warnings.Add(warning);
                    _log.Warn(warning);
                }

                var errors = _options.Validate();
                if (errors.Count > 0)
                {
                    _startupError = "Configuration invalide : " + string.Join(" ", errors);
                }
                else
                {
                    _files = new FileManager(_options.IncompleteDirectory, _options.CompletedDirectory);
                    CheckFolders();
                }

                _engine = new TorrentEngineHost(_store.EngineCacheDirectory, _options);
                foreach (var record in records)
                {
                    _tasks.Add(new TaskRuntime(record));
                }

                _started = true;

                if (_startupError == null)
                {
                    foreach (var rt in _tasks.ToList())
                    {
                        await RestoreAsync(rt).ConfigureAwait(false);
                    }
                }

                SaveNow();
                RebuildStatus();
                _log.Info("Torry démarré : " + _tasks.Count + " tâche(s), port BitTorrent " + _options.ListenPort + ".");
            }
            catch (Exception ex)
            {
                _startupError = "Démarrage du moteur impossible : " + DiskErrors.Describe(ex);
                _log.Error("Torry: démarrage impossible", ex);
                RebuildStatus();
            }
            finally
            {
                _gate.Release();
            }

            _loopCts = new CancellationTokenSource();
            _loopTask = Task.Run(() => LoopAsync(_loopCts.Token));
        }

        /// <summary>Arrêt propre : stoppe les torrents (écrit le fast resume), sauvegarde l'état, libère le moteur.</summary>
        public async Task StopAsync()
        {
            _stopping = true;
            _loopCts?.Cancel();
            if (_loopTask != null)
            {
                try
                {
                    await _loopTask.ConfigureAwait(false);
                }
                catch
                {
                    // Boucle annulée.
                }
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var stops = _tasks.Where(t => t.Manager != null).Select(async t =>
                {
                    try
                    {
                        UpdateCounters(t);
                        if (t.Manager.State != TorrentState.Stopped)
                        {
                            await t.Manager.StopAsync(StopTimeout).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("Torry: arrêt incomplet de « " + t.Record.Name + " » : " + DiskErrors.SafeMessage(ex.Message));
                    }
                }).ToList();
                await Task.WhenAny(Task.WhenAll(stops), Task.Delay(StopTimeout + TimeSpan.FromSeconds(2))).ConfigureAwait(false);

                SaveNow();
                _engine?.Dispose();
                _engine = null;
                _started = false;
                _log.Info("Torry arrêté, état enregistré.");
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            if (_started)
            {
                Task.Run(StopAsync).Wait(TimeSpan.FromSeconds(25));
            }
        }

        // ------------------------------------------------------------------ lecture

        public StatusView GetStatus() => _status;

        public async Task<TaskFilesView> GetFilesAsync(string id)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var rt = Find(id);
                var files = rt.Manager?.HasMetadata == true ? rt.Manager.Files : null;
                var pairs = rt.Record.Files.Select(f => new KeyValuePair<string, long>(f.Path, f.Length)).ToList();
                var suggested = MediaTypes.SuggestDestination(_options.Destinations, pairs);
                var view = new TaskFilesView
                {
                    SuggestedContentType = MediaTypes.SuggestContentType(pairs),
                    SuggestedDestinationId = suggested?.Id
                };
                view.Files = rt.Record.Files.Select(f => new FileView
                {
                    Index = f.Index,
                    Path = f.Path,
                    Length = f.Length,
                    Selected = f.Selected,
                    IsVideo = TorrentInspector.IsVideo(f.Path),
                    Progress = files != null && f.Index < files.Count
                        ? Math.Round(files[f.Index].BitField.PercentComplete, 1)
                        : (f.FinalPath != null ? 100 : 0)
                }).ToList();
                return view;
            }
            finally
            {
                _gate.Release();
            }
        }

        // ------------------------------------------------------------------ ajout

        public async Task<TaskView> AddMagnetAsync(string magnetUri)
        {
            magnetUri = (magnetUri ?? string.Empty).Trim();
            if (!magnetUri.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) || magnetUri.Length > 8192
                || !MagnetLink.TryParse(magnetUri, out var magnet))
            {
                throw new TorryUserException("Lien magnet invalide.");
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureReady();
                var hash = TorrentInspector.GetInfoHashHex(magnet.InfoHashes);
                EnsureNotDuplicate(hash);

                var record = NewRecord(hash, "magnet");
                record.MagnetUri = magnetUri;
                record.Name = string.IsNullOrWhiteSpace(magnet.Name) ? "Magnet " + hash.Substring(0, 8) : magnet.Name.Trim();
                record.State = TaskState.FetchingMetadata;
                record.ResumeState = TaskState.FetchingMetadata;
                var rt = new TaskRuntime(record);
                _tasks.Add(rt);
                _log.Info("Torry: lien magnet ajouté (" + hash.Substring(0, 8) + "…).");

                // Métadonnées déjà en cache (lien déjà ajouté auparavant) : pas besoin de les retélécharger.
                var cached = Path.Combine(_engine.Engine.Settings.MetadataCacheDirectory, magnet.InfoHashes.V1OrV2.ToHex() + ".torrent");
                if (File.Exists(cached) && TryApplyMetadata(rt, File.ReadAllBytes(cached), out _))
                {
                    await AfterMetadataAsync(rt).ConfigureAwait(false);
                }
                else
                {
                    await StartMetadataFetchAsync(rt).ConfigureAwait(false);
                }

                SaveNow();
                RebuildStatus();
                return BuildView(rt);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<TaskView> AddTorrentFileAsync(byte[] data)
        {
            var torrent = TorrentInspector.LoadAndValidate(data);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureReady();
                var hash = TorrentInspector.GetInfoHashHex(torrent.InfoHashes);
                EnsureNotDuplicate(hash);
                var record = NewRecord(hash, "torrent");
                record.Name = string.IsNullOrWhiteSpace(torrent.Name) ? "Torrent " + hash.Substring(0, 8) : torrent.Name;
                var rt = new TaskRuntime(record);
                if (!TryApplyMetadata(rt, data, out var error))
                {
                    throw new TorryUserException(error);
                }

                _tasks.Add(rt);
                _log.Info("Torry: fichier .torrent ajouté : « " + record.Name + " » (" + record.Files.Count + " fichier(s)).");
                await AfterMetadataAsync(rt).ConfigureAwait(false);
                SaveNow();
                RebuildStatus();
                return BuildView(rt);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Confirme la sélection des fichiers et place la tâche en file d'attente.</summary>
        public Task<TaskView> StartWithSelectionAsync(string id, IEnumerable<int> selectedIndices) => StartWithSelectionAsync(id, selectedIndices, null);

        /// <param name="destinationId">Dossier de destination (null ou vide = téléchargements terminés).</param>
        public async Task<TaskView> StartWithSelectionAsync(string id, IEnumerable<int> selectedIndices, string destinationId)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureReady();
                var rt = Find(id);
                var r = rt.Record;
                if (r.State != TaskState.AwaitingSelection)
                {
                    throw new TorryUserException("La sélection des fichiers n'est possible qu'avant le début du téléchargement.");
                }

                var selection = new HashSet<int>(selectedIndices ?? Enumerable.Empty<int>());
                if (selection.Count == 0)
                {
                    throw new TorryUserException("Sélectionnez au moins un fichier.");
                }

                if (selection.Any(i => i < 0 || i >= r.Files.Count))
                {
                    throw new TorryUserException("Sélection de fichiers invalide.");
                }

                if (!string.IsNullOrEmpty(destinationId) && _options.FindDestination(destinationId) == null)
                {
                    throw new TorryUserException("Dossier de destination inconnu.");
                }

                ApplySelection(r, selection);
                r.DestinationId = string.IsNullOrEmpty(destinationId) ? null : destinationId;
                r.State = TaskState.Queued;
                r.ResumeState = TaskState.Queued;
                await ProcessQueueAsync().ConfigureAwait(false);
                SaveNow();
                RebuildStatus();
                return BuildView(rt);
            }
            finally
            {
                _gate.Release();
            }
        }

        // ------------------------------------------------------------------ actions

        public async Task<TaskView> PauseAsync(string id)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var rt = Find(id);
                var r = rt.Record;
                switch (r.State)
                {
                    case TaskState.FetchingMetadata:
                        await DetachManagerAsync(rt).ConfigureAwait(false);
                        r.ResumeState = TaskState.FetchingMetadata;
                        break;
                    case TaskState.Queued:
                        r.ResumeState = TaskState.Queued;
                        break;
                    case TaskState.Checking:
                    case TaskState.Downloading:
                    case TaskState.Seeding:
                        r.ResumeState = r.State == TaskState.Seeding ? TaskState.Seeding : TaskState.Queued;
                        await StopManagerAsync(rt).ConfigureAwait(false);
                        break;
                    default:
                        throw new TorryUserException("Impossible de mettre en pause dans l'état actuel.");
                }

                r.State = TaskState.Paused;
                SaveNow();
                RebuildStatus();
                return BuildView(rt);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<TaskView> ResumeAsync(string id)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureReady();
                var rt = Find(id);
                var r = rt.Record;
                if (r.State != TaskState.Paused && r.State != TaskState.Cancelled && r.State != TaskState.Error)
                {
                    throw new TorryUserException("Cette tâche n'est pas en pause.");
                }

                r.ErrorMessage = null;
                if (rt.Manager != null && rt.Manager.State == TorrentState.Error)
                {
                    // StopAsync efface l'erreur MonoTorrent et ramène le gestionnaire à l'arrêt.
                    await rt.Manager.StopAsync(StopTimeout).ConfigureAwait(false);
                }

                if (!r.HasMetadata)
                {
                    r.State = TaskState.FetchingMetadata;
                    await StartMetadataFetchAsync(rt).ConfigureAwait(false);
                }
                else if (!r.SelectionConfirmed)
                {
                    r.State = TaskState.AwaitingSelection;
                }
                else if (r.ResumeState == TaskState.Seeding || r.ResumeState == TaskState.Finalizing || r.FinalDirectory != null)
                {
                    // Fichiers déjà (ou partiellement) finalisés : on relance directement, hors file d'attente.
                    await ActivateAsync(rt, r.ResumeState == TaskState.Seeding ? TaskState.Seeding : TaskState.Downloading).ConfigureAwait(false);
                }
                else
                {
                    r.State = TaskState.Queued;
                    await ProcessQueueAsync().ConfigureAwait(false);
                }

                SaveNow();
                RebuildStatus();
                return BuildView(rt);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Annule le téléchargement (fichiers conservés) ou, pour une tâche en partage, arrête le partage.</summary>
        public async Task<TaskView> CancelAsync(string id)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var rt = Find(id);
                var r = rt.Record;
                if (r.State == TaskState.Seeding || (r.State == TaskState.Paused && r.ResumeState == TaskState.Seeding))
                {
                    await FinishSeedingAsync(rt, "Partage arrêté manuellement.").ConfigureAwait(false);
                }
                else if (r.State == TaskState.Completed || r.State == TaskState.Cancelled || r.State == TaskState.Finalizing)
                {
                    throw new TorryUserException("Impossible d'annuler dans l'état actuel.");
                }
                else
                {
                    await DetachManagerAsync(rt).ConfigureAwait(false);
                    r.ResumeState = !r.HasMetadata ? TaskState.FetchingMetadata : (!r.SelectionConfirmed ? TaskState.AwaitingSelection : TaskState.Queued);
                    r.State = TaskState.Cancelled;
                    _log.Info("Torry: téléchargement annulé : « " + r.Name + " ».");
                }

                SaveNow();
                RebuildStatus();
                return BuildView(rt);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Supprime une tâche. Par défaut les fichiers sont conservés. Pour supprimer aussi les fichiers,
        /// <paramref name="confirmation"/> doit être égal à l'identifiant de la tâche (confirmation explicite).
        /// </summary>
        public async Task<RemoveResult> RemoveAsync(string id, bool deleteFiles, string confirmation)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var rt = Find(id);
                var r = rt.Record;
                if (deleteFiles && !string.Equals(confirmation, r.Id, StringComparison.Ordinal))
                {
                    throw new TorryUserException("Confirmation explicite requise pour supprimer les fichiers.");
                }

                await DetachManagerAsync(rt).ConfigureAwait(false);

                var result = new RemoveResult();
                if (deleteFiles)
                {
                    var report = FileManager.DeleteTaskFiles(r);
                    result.DeletedFiles = report.DeletedFiles;
                    result.Messages.AddRange(report.Skipped.Distinct().Select(s => "Ignoré : " + s + "."));
                    result.Messages.AddRange(report.Errors.Distinct());
                    _log.Info("Torry: tâche « " + r.Name + " » supprimée avec ses fichiers (" + report.DeletedFiles + " fichier(s)).");
                }
                else
                {
                    _log.Info("Torry: tâche « " + r.Name + " » supprimée (fichiers conservés).");
                }

                try
                {
                    _store.DeleteTorrent(r.Id);
                }
                catch (Exception ex)
                {
                    result.Messages.Add(DiskErrors.Describe(ex));
                }

                _tasks.Remove(rt);
                SaveNow();
                RebuildStatus();
                return result;
            }
            finally
            {
                _gate.Release();
            }
        }

        // ------------------------------------------------------------------ explorateur de fichiers

        /// <summary>Racines navigables : « Téléchargements terminés » puis chaque destination configurée.</summary>
        public IReadOnlyList<BrowseRoot> GetBrowseRoots()
        {
            var options = _options;
            var roots = new List<BrowseRoot>();
            if (!string.IsNullOrWhiteSpace(options.CompletedDirectory) && Path.IsPathFullyQualified(options.CompletedDirectory))
            {
                roots.Add(new BrowseRoot
                {
                    Id = BrowseRoot.CompletedId,
                    Name = "Téléchargements terminés",
                    Path = PathGuard.NormalizeDirectory(options.CompletedDirectory)
                });
            }

            foreach (var destination in options.Destinations ?? new List<DestinationFolder>())
            {
                if (!string.IsNullOrWhiteSpace(destination.Path) && Path.IsPathFullyQualified(destination.Path))
                {
                    roots.Add(new BrowseRoot
                    {
                        Id = destination.Id,
                        Name = destination.Name,
                        Path = PathGuard.NormalizeDirectory(destination.Path),
                        ContentType = destination.ContentType
                    });
                }
            }

            return roots;
        }

        public BrowseRoot FindRoot(string id)
        {
            return GetBrowseRoots().FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal))
                ?? throw new FileOperationException("Dossier racine inconnu.");
        }

        public IReadOnlyList<LibraryInfo> GetLibraries() => _library.GetLibraries();

        /// <summary>Nom du dossier qui regroupe les dossiers de bibliothèque créés par Torry.</summary>
        public const string LibrariesFolderName = "bibliotheques";

        /// <summary>
        /// « Préparer les bibliothèques » : pour chaque bibliothèque Emby, crée un dossier accessible en écriture à côté
        /// du dossier « en cours » (même volume → déplacements instantanés), l'ajoute comme emplacement de la
        /// bibliothèque et l'enregistre comme destination. Torry ne peut pas changer les droits des dossiers qui ne lui
        /// appartiennent pas : il crée donc les siens. Idempotent : peut être relancé sans dommage.
        /// </summary>
        public async Task<PrepareLibrariesView> PrepareLibrariesAsync()
        {
            var options = Options;
            var incomplete = PathGuard.NormalizeDirectory(options.IncompleteDirectory);
            var parent = Path.GetDirectoryName(incomplete);
            if (string.IsNullOrEmpty(parent) || Path.GetPathRoot(parent) == parent)
            {
                throw new TorryUserException("Le dossier « en cours » doit être dans un sous-dossier (par ex. /downloads/incomplete) pour que Torry puisse créer les dossiers de bibliothèque à côté.");
            }

            var baseDirectory = Path.Combine(parent, LibrariesFolderName);
            var writeError = FileManager.CheckWritable(baseDirectory);
            if (writeError != null)
            {
                throw new TorryUserException("Impossible de créer « " + baseDirectory + " » : " + writeError);
            }

            var view = new PrepareLibrariesView { BaseDirectory = baseDirectory };
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var touchedDestinations = new HashSet<string>(StringComparer.Ordinal);
            var libraries = _library.GetLibraries().Where(l => MediaTypes.IsKnownContentType(l.ContentType)).ToList();
            if (libraries.Count == 0)
            {
                throw new TorryUserException("Aucune bibliothèque Emby compatible (Films, Séries, Musique…) : créez-en d'abord dans Emby.");
            }

            foreach (var library in libraries)
            {
                var item = new PreparedLibraryView { Name = library.Name, ContentTypeLabel = MediaTypes.Label(library.ContentType) };
                view.Libraries.Add(item);
                try
                {
                    var folderName = PathGuard.SanitizeFolderName(library.Name, "bibliotheque");
                    var candidate = folderName;
                    for (var i = 2; !used.Add(candidate); i++)
                    {
                        candidate = folderName + " (" + i + ")";
                    }

                    var path = PathGuard.CombineInside(baseDirectory, candidate);
                    item.Path = path;
                    item.Created = !Directory.Exists(path);
                    Directory.CreateDirectory(path);
                    var error = FileManager.CheckWritable(path);
                    if (error != null)
                    {
                        item.Error = error;
                        continue;
                    }

                    if ((library.Locations ?? new List<string>()).Any(l => string.Equals(PathGuard.NormalizeDirectory(l), path, StringComparison.Ordinal)))
                    {
                        item.AlreadyAttached = true;
                    }
                    else
                    {
                        _library.AddLibraryLocation(library.Name, path);
                        item.Attached = true;
                        _log.Info("Torry: emplacement « " + path + " » ajouté à la bibliothèque « " + library.Name + " ».");
                    }

                    // Destination : on reprend celle du même nom (même si son ancien chemin était inaccessible).
                    var destination = options.Destinations.FirstOrDefault(d => string.Equals(d.Name?.Trim(), library.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? options.Destinations.FirstOrDefault(d => !string.IsNullOrEmpty(d.Path) && string.Equals(PathGuard.NormalizeDirectory(d.Path), path, StringComparison.Ordinal));
                    if (destination == null)
                    {
                        destination = new DestinationFolder { Id = Guid.NewGuid().ToString("N").Substring(0, 12) };
                        options.Destinations.Add(destination);
                    }

                    destination.Name = library.Name.Trim();
                    destination.ContentType = library.ContentType;
                    destination.Path = path;
                    touchedDestinations.Add(destination.Id);
                }
                catch (Exception ex)
                {
                    item.Error = ex is TorryUserException || ex is UnsafePathException ? ex.Message : DiskErrors.Describe(ex);
                }
            }

            // Les autres destinations inaccessibles en écriture empêcheraient d'enregistrer : on les retire.
            foreach (var destination in options.Destinations.Where(d => !touchedDestinations.Contains(d.Id)).ToList())
            {
                if (FileManager.CheckWritable(destination.Path) != null)
                {
                    options.Destinations.Remove(destination);
                    view.RemovedDestinations.Add(destination.Name);
                }
            }

            await UpdateOptionsAsync(options).ConfigureAwait(false);
            view.Options = Options;
            var ok = view.Libraries.Count(l => l.Error == null);
            view.Message = ok + " bibliothèque(s) prête(s)" + (ok < view.Libraries.Count ? ", " + (view.Libraries.Count - ok) + " en erreur" : string.Empty) + ".";
            return view;
        }

        /// <summary>
        /// Avant un déplacement : refuse si une tâche finalise les fichiers concernés, et arrête le partage des tâches
        /// dont des fichiers vont être déplacés (le moteur ne peut plus les lire à l'ancien emplacement).
        /// Renvoie une note pour l'utilisateur (ou null).
        /// </summary>
        public async Task<string> BeforeMoveAsync(string source)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var stopped = new List<string>();
                foreach (var rt in _tasks.ToList())
                {
                    var r = rt.Record;
                    if (!TouchesTask(r, source))
                    {
                        continue;
                    }

                    if (r.State == TaskState.Finalizing || (r.State == TaskState.Error && r.ResumeState == TaskState.Finalizing))
                    {
                        throw new FileOperationException("« " + r.Name + " » est en cours de finalisation : réessayez une fois la tâche terminée.");
                    }

                    if (rt.Manager != null)
                    {
                        await FinishSeedingAsync(rt, "Partage arrêté : fichiers déplacés depuis l'explorateur.").ConfigureAwait(false);
                        stopped.Add(r.Name);
                    }
                }

                RebuildStatus();
                return stopped.Count == 0 ? null : "Partage arrêté pour : " + string.Join(", ", stopped) + ".";
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Après un déplacement réussi : met à jour les chemins des tâches concernées. Si le dossier de la tâche a été
        /// déplacé, la tâche le suit (suppression avec fichiers toujours possible) ; un fichier sorti du dossier de la
        /// tâche n'est plus géré par celle-ci.
        /// </summary>
        public async Task AfterMoveAsync(string source, string target, string targetRoot)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var rt in _tasks)
                {
                    var r = rt.Record;
                    if (!TouchesTask(r, source))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(r.FinalDirectory) && PathGuard.IsSameOrInside(source, r.FinalDirectory))
                    {
                        r.FinalDirectory = Rebase(r.FinalDirectory, source, target);
                        r.CompletedRoot = PathGuard.NormalizeDirectory(targetRoot);
                    }

                    foreach (var entry in r.Files.Where(f => !string.IsNullOrEmpty(f.FinalPath) && PathGuard.IsSameOrInside(source, f.FinalPath)))
                    {
                        var moved = Rebase(entry.FinalPath, source, target);
                        entry.FinalPath = !string.IsNullOrEmpty(r.FinalDirectory) && PathGuard.IsInside(r.FinalDirectory, moved) ? moved : null;
                    }
                }

                SaveNow();
                RebuildStatus();
            }
            finally
            {
                _gate.Release();
            }
        }

        private static bool TouchesTask(TaskRecord r, string source)
        {
            return (!string.IsNullOrEmpty(r.FinalDirectory) && PathGuard.IsSameOrInside(source, r.FinalDirectory))
                || r.Files.Any(f => !string.IsNullOrEmpty(f.FinalPath) && PathGuard.IsSameOrInside(source, f.FinalPath));
        }

        private static string Rebase(string path, string oldPrefix, string newPrefix)
        {
            var normalized = Path.GetFullPath(path);
            var prefix = PathGuard.NormalizeDirectory(oldPrefix);
            return string.Equals(normalized.TrimEnd('/', '\\'), prefix, StringComparison.Ordinal)
                ? PathGuard.NormalizeDirectory(newPrefix)
                : Path.Combine(PathGuard.NormalizeDirectory(newPrefix), normalized.Substring(prefix.Length).TrimStart('/', '\\'));
        }

        /// <summary>Actualise immédiatement la ou les bibliothèques Emby qui contiennent le dossier des téléchargements terminés.</summary>
        public LibraryRefreshResult RefreshLibraryNow()
        {
            var files = _files;
            if (!_started || files == null)
            {
                throw new TorryUserException("Le moteur Torry n'est pas démarré.");
            }

            var result = _library.RefreshNow(files.CompletedRoot) ?? new LibraryRefreshResult { Message = "Actualisation impossible." };
            _libraryCheckUtc = DateTime.MinValue;
            _log.Info("Torry: actualisation manuelle de la bibliothèque : " + result.Message);
            return result;
        }

        /// <summary>Applique de nouveaux réglages (débits, port, partage, file d'attente, dossiers).</summary>
        public async Task UpdateOptionsAsync(TorryOptions options)
        {
            var errors = options.Validate();
            if (errors.Count > 0)
            {
                throw new TorryUserException(string.Join(" ", errors));
            }

            var incompleteError = FileManager.CheckWritable(options.IncompleteDirectory);
            if (incompleteError != null)
            {
                throw new TorryUserException("Dossier « en cours » : " + incompleteError);
            }

            foreach (var destination in options.Destinations ?? new List<DestinationFolder>())
            {
                var destinationError = FileManager.CheckWritable(destination.Path);
                if (destinationError != null)
                {
                    throw new TorryUserException("Destination « " + destination.Name + " » (" + destination.Path + ") : " + destinationError);
                }
            }

            var completedError = FileManager.CheckWritable(options.CompletedDirectory);
            if (completedError != null)
            {
                throw new TorryUserException("Dossier « terminés » : " + completedError);
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var foldersChanged = !string.Equals(PathGuard.NormalizeDirectory(options.IncompleteDirectory), _files?.IncompleteRoot, StringComparison.Ordinal)
                    || !string.Equals(PathGuard.NormalizeDirectory(options.CompletedDirectory), _files?.CompletedRoot, StringComparison.Ordinal);
                if (foldersChanged && _files != null && _tasks.Any(t => IsActive(t.Record.State)))
                {
                    throw new TorryUserException("Mettez en pause ou terminez les tâches en cours avant de changer de dossier.");
                }

                _options = options.Clone();
                _files = new FileManager(_options.IncompleteDirectory, _options.CompletedDirectory);
                if (_engine != null)
                {
                    await _engine.ApplyAsync(_options).ConfigureAwait(false);
                }

                if (_startupError != null && _startupError.StartsWith("Configuration invalide", StringComparison.Ordinal))
                {
                    _startupError = null;
                    foreach (var rt in _tasks.ToList())
                    {
                        await RestoreAsync(rt).ConfigureAwait(false);
                    }
                }

                CheckFolders();
                _libraryCheckUtc = DateTime.MinValue;
                await ProcessQueueAsync().ConfigureAwait(false);
                RebuildStatus();
            }
            finally
            {
                _gate.Release();
            }
        }

        // ------------------------------------------------------------------ boucle de supervision

        private async Task LoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_tickInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (_started && !_stopping)
                    {
                        await TickAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error("Torry: erreur dans la boucle de supervision", ex);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }

        /// <summary>Exposé pour les tests : exécute immédiatement un cycle de supervision.</summary>
        internal async Task TickForTestsAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await TickAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task TickAsync()
        {
            var now = DateTime.UtcNow;
            foreach (var rt in _tasks.ToList())
            {
                var r = rt.Record;
                var m = rt.Manager;
                var elapsed = (now - rt.LastTickUtc).TotalSeconds;
                rt.LastTickUtc = now;
                if (m == null)
                {
                    continue;
                }

                UpdateCounters(rt);

                if (m.State == TorrentState.Error && r.State != TaskState.Error)
                {
                    SetError(rt, DiskErrors.Describe(m.Error?.Exception));
                    continue;
                }

                switch (r.State)
                {
                    case TaskState.FetchingMetadata:
                        if (m.HasMetadata)
                        {
                            await OnMetadataReadyAsync(rt, m).ConfigureAwait(false);
                        }

                        break;

                    case TaskState.Checking:
                    case TaskState.Downloading:
                        r.State = m.State == TorrentState.Hashing || m.State == TorrentState.Starting ? TaskState.Checking : TaskState.Downloading;
                        if (m.HasMetadata && m.State != TorrentState.Hashing && m.State != TorrentState.Starting)
                        {
                            r.Progress = Math.Round(m.PartialProgress, 2);
                        }

                        if (AllSelectedComplete(rt))
                        {
                            await FinalizeAsync(rt).ConfigureAwait(false);
                        }

                        break;

                    case TaskState.Seeding:
                        if (elapsed > 0 && elapsed < 60)
                        {
                            r.SeedingSeconds += elapsed;
                        }

                        _dirty = true;
                        if (SeedLimitReached(r))
                        {
                            await FinishSeedingAsync(rt, null).ConfigureAwait(false);
                        }

                        break;
                }
            }

            await ProcessQueueAsync().ConfigureAwait(false);

            if (_dirty && DateTime.UtcNow - _lastSaveUtc > SaveInterval)
            {
                SaveNow();
            }

            RebuildStatus();
        }

        private async Task ProcessQueueAsync()
        {
            if (_startupError != null || _engine == null)
            {
                return;
            }

            var active = _tasks.Count(t => t.Record.State == TaskState.Downloading || t.Record.State == TaskState.Checking);
            foreach (var rt in _tasks.Where(t => t.Record.State == TaskState.Queued).OrderBy(t => t.Record.AddedUtc).ToList())
            {
                if (active >= _options.MaxActiveDownloads)
                {
                    break;
                }

                await ActivateAsync(rt, TaskState.Downloading).ConfigureAwait(false);
                if (rt.Record.State == TaskState.Downloading || rt.Record.State == TaskState.Checking)
                {
                    active++;
                }
            }
        }

        // ------------------------------------------------------------------ métadonnées

        /// <summary>
        /// Récupération des métadonnées d'un lien magnet.
        /// On n'utilise pas ClientEngine.DownloadMetadataAsync : MonoTorrent y enregistre un gestionnaire « privé »
        /// qui refuse les connexions ENTRANTES (vérifié dans MonoTorrent 3.0.2, ListenManager ne cherche que dans
        /// Engine.Torrents). On ajoute donc un gestionnaire public, que l'on arrête dès que les métadonnées
        /// sont connues : MonoTorrent démarrerait sinon le téléchargement de tous les fichiers.
        /// </summary>
        private async Task StartMetadataFetchAsync(TaskRuntime rt)
        {
            var r = rt.Record;
            if (!MagnetLink.TryParse(r.MagnetUri ?? string.Empty, out var magnet))
            {
                SetError(rt, "Lien magnet enregistré illisible.");
                return;
            }

            await DetachManagerAsync(rt).ConfigureAwait(false);
            try
            {
                if (!PathGuard.IsInside(r.IncompleteRoot, r.WorkDirectory))
                {
                    throw new UnsafePathException("Dossier de travail hors du dossier autorisé.");
                }

                Directory.CreateDirectory(r.WorkDirectory);
                var manager = await _engine.Engine.AddAsync(magnet, r.WorkDirectory, TorrentEngineHost.BuildTorrentSettings(_options)).ConfigureAwait(false);
                rt.Manager = manager;
                rt.LastUploadCounter = 0;
                rt.LastDownloadCounter = 0;
                manager.TorrentStateChanged += (sender, args) =>
                {
                    if (manager.HasMetadata)
                    {
                        _ = Task.Run(() => WithGateAsync(() => OnMetadataReadyAsync(rt, manager)));
                    }
                };

                if (manager.HasMetadata)
                {
                    // Métadonnées déjà présentes dans le cache MonoTorrent.
                    await OnMetadataReadyAsync(rt, manager).ConfigureAwait(false);
                    return;
                }

                await manager.StartAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetError(rt, "Récupération des métadonnées impossible : " + DiskErrors.Describe(ex));
            }
        }

        private async Task OnMetadataReadyAsync(TaskRuntime rt, TorrentManager manager)
        {
            if (!_tasks.Contains(rt) || rt.Manager != manager || rt.Record.State != TaskState.FetchingMetadata || !manager.HasMetadata)
            {
                return;
            }

            // Arrêt immédiat : aucun contenu ne doit être téléchargé avant la sélection des fichiers.
            try
            {
                if (manager.State != TorrentState.Stopped)
                {
                    await manager.StopAsync(StopTimeout).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Torry: arrêt après métadonnées : " + DiskErrors.SafeMessage(ex.Message));
            }

            var cachePath = Path.Combine(_engine.Engine.Settings.MetadataCacheDirectory, manager.InfoHashes.V1OrV2.ToHex() + ".torrent");
            byte[] bytes = null;
            try
            {
                bytes = File.Exists(cachePath) ? File.ReadAllBytes(cachePath) : null;
            }
            catch (Exception ex)
            {
                _log.Warn("Torry: lecture des métadonnées : " + DiskErrors.Describe(ex));
            }

            try
            {
                await _engine.Engine.RemoveAsync(manager, RemoveMode.KeepAllData).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("Torry: retrait du gestionnaire de métadonnées : " + DiskErrors.SafeMessage(ex.Message));
            }

            rt.Manager = null;
            string error = "Métadonnées reçues mais illisibles.";
            if (bytes != null && TryApplyMetadata(rt, bytes, out error))
            {
                _log.Info("Torry: métadonnées reçues pour « " + rt.Record.Name + " » (" + rt.Record.Files.Count + " fichier(s)).");
                await AfterMetadataAsync(rt).ConfigureAwait(false);
            }
            else
            {
                SetError(rt, error);
            }

            SaveNow();
            RebuildStatus();
        }

        private bool TryApplyMetadata(TaskRuntime rt, byte[] data, out string error)
        {
            error = null;
            Torrent torrent;
            try
            {
                torrent = TorrentInspector.LoadAndValidate(data);
            }
            catch (TorryUserException ex)
            {
                error = ex.Message;
                return false;
            }

            var r = rt.Record;
            if (!string.Equals(TorrentInspector.GetInfoHashHex(torrent.InfoHashes), r.InfoHash, StringComparison.Ordinal))
            {
                error = "Les métadonnées reçues ne correspondent pas au lien magnet.";
                return false;
            }

            try
            {
                _store.SaveTorrent(r.Id, data);
            }
            catch (Exception ex)
            {
                error = "Enregistrement des métadonnées impossible : " + DiskErrors.Describe(ex);
                return false;
            }

            r.HasMetadata = true;
            r.Name = string.IsNullOrWhiteSpace(torrent.Name) ? r.Name : torrent.Name;
            r.IsPrivate = torrent.IsPrivate;
            r.Files = TorrentInspector.BuildEntries(torrent);
            r.TotalBytes = r.Files.Sum(f => f.Length);
            r.SelectedBytes = r.TotalBytes;
            r.State = TaskState.AwaitingSelection;
            r.ResumeState = TaskState.AwaitingSelection;
            rt.Torrent = torrent;
            return true;
        }

        private async Task AfterMetadataAsync(TaskRuntime rt)
        {
            if (_options.AutoStartAfterMetadata)
            {
                ApplySelection(rt.Record, new HashSet<int>(rt.Record.Files.Select(f => f.Index)));
                rt.Record.DestinationId = MediaTypes.SuggestDestination(
                    _options.Destinations,
                    rt.Record.Files.Select(f => new KeyValuePair<string, long>(f.Path, f.Length)))?.Id;
                rt.Record.State = TaskState.Queued;
                rt.Record.ResumeState = TaskState.Queued;
                await ProcessQueueAsync().ConfigureAwait(false);
            }
        }

        private static void ApplySelection(TaskRecord r, HashSet<int> selection)
        {
            foreach (var f in r.Files)
            {
                f.Selected = selection.Contains(f.Index);
            }

            r.SelectedBytes = r.Files.Where(f => f.Selected).Sum(f => f.Length);
            r.SelectionConfirmed = true;
        }

        // ------------------------------------------------------------------ moteur

        /// <summary>Restaure une tâche après redémarrage d'Emby.</summary>
        private async Task RestoreAsync(TaskRuntime rt)
        {
            var r = rt.Record;
            try
            {
                switch (r.State)
                {
                    case TaskState.FetchingMetadata:
                        var bytes = _store.LoadTorrent(r.Id);
                        if (bytes != null && TryApplyMetadata(rt, bytes, out _))
                        {
                            await AfterMetadataAsync(rt).ConfigureAwait(false);
                        }
                        else
                        {
                            await StartMetadataFetchAsync(rt).ConfigureAwait(false);
                        }

                        break;
                    case TaskState.Checking:
                    case TaskState.Downloading:
                        r.State = TaskState.Queued;
                        break;
                    case TaskState.Finalizing:
                        await ActivateAsync(rt, TaskState.Downloading).ConfigureAwait(false);
                        break;
                    case TaskState.Seeding:
                        await ActivateAsync(rt, TaskState.Seeding).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                SetError(rt, "Reprise impossible : " + DiskErrors.Describe(ex));
            }
        }

        /// <summary>Ajoute la tâche au moteur si nécessaire puis la démarre.</summary>
        private async Task ActivateAsync(TaskRuntime rt, TaskState targetState)
        {
            var r = rt.Record;
            try
            {
                if (targetState == TaskState.Downloading && r.FinalDirectory == null)
                {
                    var remaining = (long)(r.SelectedBytes * (1 - (r.Progress / 100.0)));
                    var free = FileManager.GetAvailableFreeSpace(r.WorkDirectory != null && Directory.Exists(r.WorkDirectory) ? r.WorkDirectory : _files.IncompleteRoot);
                    if (free.HasValue && free.Value < remaining + (64L * 1024 * 1024))
                    {
                        SetError(rt, "Espace disque insuffisant : " + FormatBytes(remaining) + " nécessaires, " + FormatBytes(free.Value) + " disponibles.");
                        return;
                    }
                }

                await AttachManagerAsync(rt).ConfigureAwait(false);
                if (rt.Manager.State == TorrentState.Stopped || rt.Manager.State == TorrentState.Error)
                {
                    if (rt.Manager.State == TorrentState.Error)
                    {
                        await rt.Manager.StopAsync(StopTimeout).ConfigureAwait(false);
                    }

                    await rt.Manager.StartAsync().ConfigureAwait(false);
                }

                r.State = targetState;
                r.ResumeState = targetState == TaskState.Seeding ? TaskState.Seeding : TaskState.Queued;
                r.ErrorMessage = null;
                rt.LastTickUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                SetError(rt, DiskErrors.Describe(ex));
            }
        }

        private async Task AttachManagerAsync(TaskRuntime rt)
        {
            if (rt.Manager != null)
            {
                return;
            }

            var r = rt.Record;
            if (rt.Torrent == null)
            {
                var bytes = _store.LoadTorrent(r.Id) ?? throw new TorryUserException("Métadonnées du torrent introuvables.");
                rt.Torrent = TorrentInspector.LoadAndValidate(bytes);
            }

            if (string.IsNullOrEmpty(r.WorkDirectory))
            {
                r.IncompleteRoot = _files.IncompleteRoot;
                r.WorkDirectory = _files.GetTaskWorkDirectory(r.Id);
            }

            if (!PathGuard.IsInside(r.IncompleteRoot, r.WorkDirectory))
            {
                throw new UnsafePathException("Dossier de travail hors du dossier autorisé.");
            }

            // Chaque fichier doit atterrir dans le dossier de travail (double contrôle après MonoTorrent).
            foreach (var entry in r.Files)
            {
                PathGuard.CombineInside(r.WorkDirectory, entry.Path);
            }

            Directory.CreateDirectory(r.WorkDirectory);
            var manager = await _engine.Engine.AddAsync(rt.Torrent, r.WorkDirectory, TorrentEngineHost.BuildTorrentSettings(_options)).ConfigureAwait(false);
            try
            {
                for (var i = 0; i < manager.Files.Count && i < r.Files.Count; i++)
                {
                    var entry = r.Files[i];
                    var file = manager.Files[i];
                    if (!PathGuard.IsInside(r.WorkDirectory, file.FullPath))
                    {
                        throw new UnsafePathException("Chemin de fichier refusé (sortie du dossier autorisé).");
                    }

                    await manager.SetFilePriorityAsync(file, entry.Selected ? Priority.Normal : Priority.DoNotDownload).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(entry.FinalPath))
                    {
                        // Fichier déjà finalisé : on indique simplement son emplacement au moteur (pas de copie).
                        if (string.IsNullOrEmpty(r.FinalDirectory) || !PathGuard.IsInside(r.CompletedRoot, r.FinalDirectory)
                            || !PathGuard.IsInside(r.FinalDirectory, entry.FinalPath))
                        {
                            throw new UnsafePathException("Fichier final hors du dossier autorisé.");
                        }

                        if (file.FullPath != entry.FinalPath)
                        {
                            await manager.MoveFileAsync(file, entry.FinalPath).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch
            {
                await _engine.Engine.RemoveAsync(manager, RemoveMode.KeepAllData).ConfigureAwait(false);
                throw;
            }

            rt.Manager = manager;
            rt.LastUploadCounter = 0;
            rt.LastDownloadCounter = 0;
        }

        private async Task StopManagerAsync(TaskRuntime rt)
        {
            if (rt.Manager == null)
            {
                return;
            }

            UpdateCounters(rt);
            try
            {
                if (rt.Manager.State != TorrentState.Stopped)
                {
                    await rt.Manager.StopAsync(StopTimeout).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Torry: arrêt de « " + rt.Record.Name + " » : " + DiskErrors.SafeMessage(ex.Message));
            }
        }

        /// <summary>Arrête et retire la tâche du moteur. Les fichiers téléchargés ne sont jamais supprimés ici.</summary>
        private async Task DetachManagerAsync(TaskRuntime rt)
        {
            if (rt.Manager == null)
            {
                return;
            }

            await StopManagerAsync(rt).ConfigureAwait(false);
            try
            {
                await _engine.Engine.RemoveAsync(rt.Manager, RemoveMode.CacheDataOnly).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("Torry: retrait du moteur de « " + rt.Record.Name + " » : " + DiskErrors.SafeMessage(ex.Message));
            }

            rt.Manager = null;
        }

        private static bool AllSelectedComplete(TaskRuntime rt)
        {
            var m = rt.Manager;
            if (m == null || !m.HasMetadata || m.State == TorrentState.Hashing || m.State == TorrentState.Starting
                || m.State == TorrentState.Stopped || m.State == TorrentState.Stopping || m.State == TorrentState.Metadata)
            {
                return false;
            }

            var any = false;
            foreach (var entry in rt.Record.Files.Where(f => f.Selected))
            {
                any = true;
                if (entry.Index >= m.Files.Count || !m.Files[entry.Index].BitField.AllTrue)
                {
                    return false;
                }
            }

            return any;
        }

        // ------------------------------------------------------------------ finalisation et partage

        /// <summary>
        /// Finalisation : chaque fichier sélectionné est déplacé par le moteur (MoveFileAsync) de
        /// &lt;en cours&gt;/&lt;id&gt;/… vers &lt;terminés&gt;/&lt;Nom unique&gt;/… en conservant la structure du torrent.
        /// Sur un même système de fichiers c'est un simple renommage (aucune copie) ; le moteur continue ensuite
        /// à partager depuis le nouvel emplacement.
        /// </summary>
        private async Task FinalizeAsync(TaskRuntime rt)
        {
            var r = rt.Record;
            var m = rt.Manager;
            r.State = TaskState.Finalizing;
            r.ResumeState = TaskState.Finalizing;
            r.Progress = 100;
            try
            {
                if (string.IsNullOrEmpty(r.FinalDirectory))
                {
                    var destination = _options.FindDestination(r.DestinationId);
                    if (r.DestinationId != null && destination == null)
                    {
                        _log.Warn("Torry: destination de « " + r.Name + " » supprimée des réglages : finalisation dans « terminés ».");
                    }

                    r.CompletedRoot = destination != null ? PathGuard.NormalizeDirectory(destination.Path) : _files.CompletedRoot;
                    r.FinalDirectory = FileManager.CreateFinalDirectory(r.CompletedRoot, r.Name, "torrent-" + r.InfoHash.Substring(0, 8));
                    SaveNow();
                }
                else
                {
                    if (!PathGuard.IsInside(r.CompletedRoot, r.FinalDirectory))
                    {
                        throw new UnsafePathException("Dossier final hors du dossier autorisé.");
                    }

                    Directory.CreateDirectory(r.FinalDirectory);
                }

                foreach (var entry in r.Files.Where(f => f.Selected))
                {
                    var file = m.Files[entry.Index];
                    var target = entry.FinalPath ?? PathGuard.CombineInside(r.FinalDirectory, entry.Path);
                    if (file.FullPath == target)
                    {
                        entry.FinalPath = target;
                        continue;
                    }

                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        target = PathGuard.GetUniqueFilePath(target);
                    }

                    await m.MoveFileAsync(file, target).ConfigureAwait(false);
                    entry.FinalPath = target;
                    SaveNow();
                }

                // Il peut rester des fragments de fichiers non sélectionnés (pièces partagées) : ils restent dans
                // le dossier « en cours », hors bibliothèque. On ne supprime que les dossiers vides.
                FileManager.RemoveEmptyDirectories(r.WorkDirectory, removeRoot: true);

                r.CompletedUtc = DateTime.UtcNow;
                _log.Info("Torry: « " + r.Name + " » terminé et déplacé dans « " + Path.GetFileName(r.FinalDirectory) + " ».");

                if (_options.RefreshLibraryOnComplete)
                {
                    try
                    {
                        r.LibraryNote = _library.NotifyNewContent(r.FinalDirectory);
                    }
                    catch (Exception ex)
                    {
                        r.LibraryNote = "Actualisation de la bibliothèque impossible : " + DiskErrors.SafeMessage(ex.Message);
                    }
                }

                if (SeedLimitReached(r))
                {
                    await FinishSeedingAsync(rt, null).ConfigureAwait(false);
                }
                else
                {
                    r.State = TaskState.Seeding;
                    r.ResumeState = TaskState.Seeding;
                }
            }
            catch (Exception ex)
            {
                SetError(rt, "Finalisation impossible : " + DiskErrors.Describe(ex));
                r.ResumeState = TaskState.Finalizing;
            }

            SaveNow();
        }

        private bool SeedLimitReached(TaskRecord r)
        {
            if (!_options.SeedAfterDownload)
            {
                return true;
            }

            if (_options.SeedRatioLimit > 0 && Ratio(r) >= _options.SeedRatioLimit)
            {
                return true;
            }

            return _options.SeedTimeLimitMinutes > 0 && r.SeedingSeconds >= _options.SeedTimeLimitMinutes * 60.0;
        }

        private async Task FinishSeedingAsync(TaskRuntime rt, string note)
        {
            await DetachManagerAsync(rt).ConfigureAwait(false);
            rt.Record.State = TaskState.Completed;
            rt.Record.ResumeState = TaskState.Completed;
            if (note != null)
            {
                _log.Info("Torry: « " + rt.Record.Name + " » : " + note);
            }

            SaveNow();
        }

        // ------------------------------------------------------------------ utilitaires

        private void SetError(TaskRuntime rt, string message)
        {
            var r = rt.Record;
            if (r.State != TaskState.Error)
            {
                r.ResumeState = r.State == TaskState.Downloading || r.State == TaskState.Checking ? TaskState.Queued : r.State;
            }

            r.State = TaskState.Error;
            r.ErrorMessage = message;
            _dirty = true;
            _log.Warn("Torry: « " + r.Name + " » en erreur : " + message);
            if (rt.Manager != null && rt.Manager.State != TorrentState.Error)
            {
                // Libère les connexions ; la tâche pourra être reprise.
                _ = rt.Manager.StopAsync(StopTimeout).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }

        private void UpdateCounters(TaskRuntime rt)
        {
            var m = rt.Manager;
            if (m == null)
            {
                return;
            }

            var up = m.Monitor.DataBytesSent;
            var down = m.Monitor.DataBytesReceived;
            var deltaUp = up >= rt.LastUploadCounter ? up - rt.LastUploadCounter : up;
            var deltaDown = down >= rt.LastDownloadCounter ? down - rt.LastDownloadCounter : down;
            rt.LastUploadCounter = up;
            rt.LastDownloadCounter = down;
            if (deltaUp > 0 || deltaDown > 0)
            {
                rt.Record.UploadedBytes += deltaUp;
                rt.Record.DownloadedBytes += deltaDown;
                _dirty = true;
            }
        }

        private static double Ratio(TaskRecord r) => r.SelectedBytes <= 0 ? 0 : (double)r.UploadedBytes / r.SelectedBytes;

        private static bool IsActive(TaskState s) =>
            s == TaskState.FetchingMetadata || s == TaskState.Queued || s == TaskState.Checking
            || s == TaskState.Downloading || s == TaskState.Finalizing || s == TaskState.Seeding;

        private TaskRecord NewRecord(string hash, string source)
        {
            var id = Guid.NewGuid().ToString("N");
            return new TaskRecord
            {
                Id = id,
                InfoHash = hash,
                Source = source,
                AddedUtc = DateTime.UtcNow,
                IncompleteRoot = _files.IncompleteRoot,
                WorkDirectory = _files.GetTaskWorkDirectory(id)
            };
        }

        private void EnsureReady()
        {
            if (!_started || _engine == null)
            {
                throw new TorryUserException("Le moteur Torry n'est pas démarré.");
            }

            if (_startupError != null)
            {
                throw new TorryUserException(_startupError);
            }
        }

        private void EnsureNotDuplicate(string hash)
        {
            if (_tasks.Any(t => string.Equals(t.Record.InfoHash, hash, StringComparison.Ordinal)))
            {
                throw new TorryUserException("Ce torrent est déjà dans la liste.");
            }
        }

        private TaskRuntime Find(string id)
        {
            var rt = _tasks.FirstOrDefault(t => string.Equals(t.Record.Id, id, StringComparison.Ordinal));
            return rt ?? throw new TorryUserException("Tâche introuvable.");
        }

        private void CheckFolders()
        {
            _warnings.RemoveAll(w => w.StartsWith("Dossier", StringComparison.Ordinal));
            var a = FileManager.CheckWritable(_files.IncompleteRoot);
            if (a != null)
            {
                _warnings.Add("Dossier « en cours » : " + a);
            }

            var b = FileManager.CheckWritable(_files.CompletedRoot);
            if (b != null)
            {
                _warnings.Add("Dossier « terminés » : " + b);
            }
        }

        private void SaveNow()
        {
            try
            {
                _store.Save(_tasks.Select(t => t.Record));
                _lastSaveUtc = DateTime.UtcNow;
                _dirty = false;
            }
            catch (Exception ex)
            {
                _log.Error("Torry: enregistrement de l'état impossible : " + DiskErrors.Describe(ex));
            }
        }

        private async Task WithGateAsync(Func<Task> action)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await action().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private void RebuildStatus()
        {
            var status = new StatusView();
            status.Engine.Running = _started && _startupError == null;
            status.Engine.StartupError = _startupError;
            status.Engine.ListenPort = _options.ListenPort;
            status.Engine.Listening = _engine?.Engine?.IsRunning == true;
            status.Engine.DhtEnabled = _options.EnableDht;
            status.Engine.IncompleteDirectory = _options.IncompleteDirectory;
            status.Engine.CompletedDirectory = _options.CompletedDirectory;
            try
            {
                status.Engine.DhtNodes = _engine?.Engine?.Dht?.NodeCount ?? 0;
            }
            catch
            {
                status.Engine.DhtNodes = 0;
            }

            if (_files != null)
            {
                status.Engine.IncompleteFreeBytes = FileManager.GetAvailableFreeSpace(_files.IncompleteRoot);
                status.Engine.CompletedFreeBytes = FileManager.GetAvailableFreeSpace(_files.CompletedRoot);
                if (DateTime.UtcNow - _libraryCheckUtc > TimeSpan.FromSeconds(30))
                {
                    _libraryCheckUtc = DateTime.UtcNow;
                    try
                    {
                        _completedInLibrary = _library.IsInLibrary(_files.CompletedRoot);
                    }
                    catch
                    {
                        _completedInLibrary = false;
                    }
                }

                status.Engine.CompletedInLibrary = _completedInLibrary;
            }

            status.Engine.Warnings.AddRange(_warnings);
            if (_files != null && !status.Engine.CompletedInLibrary && !(_library is NullLibraryNotifier))
            {
                status.Engine.Warnings.Add("Aucune bibliothèque Emby ne contient le dossier des téléchargements terminés : les vidéos n'apparaîtront pas automatiquement.");
            }

            foreach (var root in GetBrowseRoots())
            {
                var exists = Directory.Exists(root.Path);
                status.Roots.Add(new RootView
                {
                    Id = root.Id,
                    Name = root.Name,
                    ContentType = root.ContentType,
                    ContentTypeLabel = root.ContentType == null ? null : MediaTypes.Label(root.ContentType),
                    Path = root.Path,
                    Available = exists,
                    FreeBytes = exists ? FileManager.GetAvailableFreeSpace(root.Path) : null
                });
            }

            foreach (var rt in _tasks.OrderByDescending(t => t.Record.AddedUtc))
            {
                var view = BuildView(rt);
                status.Tasks.Add(view);
                status.Engine.DownloadRate += view.DownloadRate;
                status.Engine.UploadRate += view.UploadRate;
            }

            _status = status;
        }

        private TaskView BuildView(TaskRuntime rt)
        {
            var r = rt.Record;
            var m = rt.Manager;
            var view = new TaskView
            {
                Id = r.Id,
                Name = r.Name,
                State = r.State.ToString(),
                StateLabel = Label(r),
                TotalBytes = r.TotalBytes,
                SelectedBytes = r.SelectionConfirmed ? r.SelectedBytes : r.TotalBytes,
                FileCount = r.Files.Count,
                SelectedFileCount = r.SelectionConfirmed ? r.Files.Count(f => f.Selected) : r.Files.Count,
                IsPrivate = r.IsPrivate,
                ErrorMessage = r.State == TaskState.Error ? r.ErrorMessage : null,
                LibraryNote = r.LibraryNote,
                FinalFolder = r.FinalDirectory == null ? null : Path.GetFileName(r.FinalDirectory),
                DestinationName = _options.FindDestination(r.DestinationId)?.Name,
                AddedUtc = r.AddedUtc,
                CompletedUtc = r.CompletedUtc,
                UploadedBytes = r.UploadedBytes,
                Ratio = Math.Round(Ratio(r), 2),
                SeedingSeconds = Math.Round(r.SeedingSeconds),
                Progress = r.State == TaskState.Seeding || r.State == TaskState.Completed ? 100 : r.Progress,
                NeedsSelection = r.State == TaskState.AwaitingSelection
            };

            if (m != null && (r.State == TaskState.FetchingMetadata || r.State == TaskState.Downloading || r.State == TaskState.Checking || r.State == TaskState.Seeding || r.State == TaskState.Finalizing))
            {
                view.DownloadRate = m.Monitor.DownloadRate;
                view.UploadRate = m.Monitor.UploadRate;
                view.Peers = m.OpenConnections;
                view.Seeds = m.Peers.Seeds;
            }

            view.DoneBytes = (long)(view.SelectedBytes * (view.Progress / 100.0));
            if (r.State == TaskState.Downloading && view.DownloadRate > 512)
            {
                var remaining = Math.Max(0, view.SelectedBytes - view.DoneBytes);
                view.EtaSeconds = remaining / view.DownloadRate;
            }

            view.CanPause = r.State == TaskState.FetchingMetadata || r.State == TaskState.Queued || r.State == TaskState.Checking
                || r.State == TaskState.Downloading || r.State == TaskState.Seeding;
            view.CanResume = r.State == TaskState.Paused || r.State == TaskState.Cancelled || r.State == TaskState.Error;
            view.CanStopSeeding = r.State == TaskState.Seeding || (r.State == TaskState.Paused && r.ResumeState == TaskState.Seeding);
            view.CanCancel = !view.CanStopSeeding && r.State != TaskState.Completed && r.State != TaskState.Cancelled
                && r.State != TaskState.Finalizing && r.FinalDirectory == null;
            return view;
        }

        private string Label(TaskRecord r)
        {
            switch (r.State)
            {
                case TaskState.FetchingMetadata: return "Recherche des métadonnées";
                case TaskState.AwaitingSelection: return "Choix des fichiers";
                case TaskState.Queued: return "En file d'attente";
                case TaskState.Checking: return "Vérification";
                case TaskState.Downloading: return "Téléchargement";
                case TaskState.Paused: return "En pause";
                case TaskState.Finalizing: return "Finalisation";
                case TaskState.Seeding: return "Disponible · partage";
                case TaskState.Completed: return "Disponible";
                case TaskState.Cancelled: return "Annulé";
                case TaskState.Error: return "Erreur";
                default: return r.State.ToString();
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "o", "Kio", "Mio", "Gio", "Tio" };
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return value.ToString(unit == 0 ? "0" : "0.0", System.Globalization.CultureInfo.GetCultureInfo("fr-FR")) + " " + units[unit];
        }

        private sealed class TaskRuntime
        {
            public TaskRuntime(TaskRecord record)
            {
                Record = record;
                LastTickUtc = DateTime.UtcNow;
            }

            public TaskRecord Record { get; }

            public TorrentManager Manager { get; set; }

            public Torrent Torrent { get; set; }

            public long LastUploadCounter { get; set; }

            public long LastDownloadCounter { get; set; }

            public DateTime LastTickUtc { get; set; }
        }
    }
}
