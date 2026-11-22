using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PetitOurs.Torry.Core;
using PetitOurs.Torry.Files;

namespace PetitOurs.Torry.Engine
{
    /// <summary>
    /// Explorateur de fichiers de Torry : navigation, création de dossiers et déplacements/renommages.
    /// Les déplacements sont exécutés un par un en arrière-plan (une copie entre volumes peut être longue),
    /// avec mise à jour des tâches concernées et actualisation des bibliothèques Emby.
    /// </summary>
    public sealed class FileOperationsService
    {
        private const int KeepOperations = 20;

        private readonly TorrentTaskManager _manager;
        private readonly ILibraryNotifier _library;
        private readonly ITorryLog _log;
        private readonly SemaphoreSlim _worker = new SemaphoreSlim(1, 1);
        private readonly object _opsLock = new object();
        private readonly List<MoveOperationView> _operations = new List<MoveOperationView>();

        public FileOperationsService(TorrentTaskManager manager, ILibraryNotifier library, ITorryLog log)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _library = library ?? new NullLibraryNotifier();
            _log = log ?? new NullLog();
            _trashTimer = new Timer(_ => _ = PurgeExpiredTrashAsync(), null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(1));
        }

        // ------------------------------------------------------------------ corbeille

        private readonly Timer _trashTimer;
        private readonly object _trashLock = new object();
        private List<TrashRecord> _trash;

        private sealed class TrashRecord
        {
            public string Id { get; set; }

            public string Name { get; set; }

            public bool IsDirectory { get; set; }

            public string RootId { get; set; }

            public string RootPath { get; set; }

            public string OriginalPath { get; set; }

            public int FileCount { get; set; }

            public long Bytes { get; set; }

            public DateTime DeletedUtc { get; set; }
        }

        private string TrashIndexPath => Path.Combine(_manager.DataDirectory, "trash.json");

        private List<TrashRecord> LoadTrash()
        {
            lock (_trashLock)
            {
                if (_trash != null)
                {
                    return _trash;
                }

                try
                {
                    _trash = File.Exists(TrashIndexPath)
                        ? System.Text.Json.JsonSerializer.Deserialize<List<TrashRecord>>(File.ReadAllText(TrashIndexPath)) ?? new List<TrashRecord>()
                        : new List<TrashRecord>();
                }
                catch (Exception ex)
                {
                    _log.Warn("Torry: index de la corbeille illisible : " + DiskErrors.SafeMessage(ex.Message));
                    _trash = new List<TrashRecord>();
                }

                return _trash;
            }
        }

        private void SaveTrash()
        {
            lock (_trashLock)
            {
                try
                {
                    Directory.CreateDirectory(_manager.DataDirectory);
                    var temp = TrashIndexPath + ".tmp";
                    File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(_trash ?? new List<TrashRecord>()));
                    File.Move(temp, TrashIndexPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    _log.Error("Torry: enregistrement de la corbeille impossible : " + DiskErrors.Describe(ex));
                }
            }
        }

        public TrashView GetTrash()
        {
            var retention = _manager.Options.TrashRetentionDays;
            var roots = _manager.GetBrowseRoots();
            var view = new TrashView { RetentionDays = retention };
            List<TrashRecord> records;
            lock (_trashLock)
            {
                var list = LoadTrash();
                // Éléments disparus du disque (supprimés à la main) : on les oublie.
                var missing = list.Where(r => !SafeExists(r)).ToList();
                if (missing.Count > 0)
                {
                    missing.ForEach(r => list.Remove(r));
                    SaveTrash();
                }

                records = list.ToList();
            }

            foreach (var r in records.OrderByDescending(r => r.DeletedUtc))
            {
                var root = roots.FirstOrDefault(x => x.Id == r.RootId && string.Equals(x.Path, r.RootPath, StringComparison.Ordinal));
                view.Entries.Add(new TrashEntryView
                {
                    Id = r.Id,
                    Name = r.Name,
                    IsDirectory = r.IsDirectory,
                    RootId = r.RootId,
                    RootName = root?.Name ?? "(dossier retiré des réglages)",
                    OriginalPath = r.OriginalPath,
                    FileCount = r.FileCount,
                    Bytes = r.Bytes,
                    DeletedUtc = r.DeletedUtc,
                    ExpiresUtc = retention > 0 ? r.DeletedUtc.AddDays(retention) : (DateTime?)null,
                    CanRestore = root != null
                });
                view.TotalBytes += r.Bytes;
            }

            return view;
        }

        private static bool SafeExists(TrashRecord r)
        {
            try
            {
                var directory = FileBrowser.TrashItemDirectory(r.RootPath, r.Id);
                var item = Path.Combine(directory, r.Name);
                return Directory.Exists(item) || File.Exists(item);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Met un élément à la corbeille de sa racine (renommage instantané, restaurable).</summary>
        public MoveOperationView StartTrash(string rootId, string path)
        {
            var root = _manager.FindRoot(rootId);
            var plan = FileBrowser.PlanDelete(root, path);
            var op = NewOperation("trash", plan.SourceDisplay, "Corbeille");
            _ = Task.Run(() => RunExclusiveAsync(op, async () =>
            {
                var id = Guid.NewGuid().ToString("N");
                var trashDirectory = FileBrowser.TrashItemDirectory(root.Path, id);
                var name = Path.GetFileName(plan.Source);
                var measure = FileBrowser.Analyze(root, path);
                var note = await _manager.BeforeMoveAsync(plan.Source).ConfigureAwait(false);
                Directory.CreateDirectory(trashDirectory);
                try
                {
                    FileBrowser.Execute(plan, Path.Combine(trashDirectory, name), null);
                }
                catch
                {
                    FileManager.RemoveEmptyDirectories(trashDirectory, removeRoot: true);
                    throw;
                }

                await _manager.AfterDeleteAsync(plan.Source).ConfigureAwait(false);
                lock (_trashLock)
                {
                    LoadTrash().Add(new TrashRecord
                    {
                        Id = id,
                        Name = name,
                        IsDirectory = plan.IsDirectory,
                        RootId = root.Id,
                        RootPath = root.Path,
                        OriginalPath = FileBrowser.ToRelative(root, plan.Source),
                        FileCount = measure.FileCount,
                        Bytes = measure.TotalBytes,
                        DeletedUtc = DateTime.UtcNow
                    });
                    SaveTrash();
                }

                var refreshed = TryRefresh(Path.GetDirectoryName(plan.Source));
                _log.Info("Torry: mis à la corbeille : " + plan.SourceDisplay);
                return "Mis à la corbeille." + (refreshed ? " Bibliothèque actualisée." : string.Empty) + (note == null ? string.Empty : " " + note);
            }));
            return Copy(op);
        }

        /// <summary>Restaure un élément à son emplacement d'origine (« Nom (2) » si le nom est repris entre-temps).</summary>
        public MoveOperationView StartRestore(string id)
        {
            var record = FindTrash(id);
            var root = _manager.GetBrowseRoots().FirstOrDefault(r => r.Id == record.RootId && string.Equals(r.Path, record.RootPath, StringComparison.Ordinal))
                ?? throw new FileOperationException("Le dossier d'origine n'est plus configuré dans Torry : restauration impossible.");
            var op = NewOperation("restore", record.Name, FileBrowser.Display(root, PathGuard.CombineInside(root.Path, record.OriginalPath)));
            _ = Task.Run(() => RunExclusiveAsync(op, () =>
            {
                var trashDirectory = FileBrowser.TrashItemDirectory(root.Path, record.Id);
                var source = Path.Combine(trashDirectory, record.Name);
                var original = PathGuard.CombineInside(root.Path, record.OriginalPath);
                var parent = Path.GetDirectoryName(original);
                Directory.CreateDirectory(parent);
                var plan = new MovePlan
                {
                    Source = source,
                    TargetDirectory = parent,
                    TargetName = Path.GetFileName(original),
                    SourceRoot = root,
                    TargetRoot = root,
                    IsDirectory = Directory.Exists(source)
                };
                var target = FileBrowser.ReserveTarget(plan);
                FileBrowser.Execute(plan, target, null);
                FileManager.RemoveEmptyDirectories(trashDirectory, removeRoot: true);
                lock (_trashLock)
                {
                    LoadTrash().RemoveAll(r => r.Id == record.Id);
                    SaveTrash();
                }

                var refreshed = TryRefresh(target);
                Update(op, o => o.Target = FileBrowser.Display(root, target));
                _log.Info("Torry: restauré depuis la corbeille : " + FileBrowser.Display(root, target));
                return Task.FromResult("Restauré." + (refreshed ? " Bibliothèque actualisée." : string.Empty));
            }));
            return Copy(op);
        }

        /// <summary>Suppression définitive d'un élément de la corbeille (confirmation « SUPPRIMER »).</summary>
        public MoveOperationView StartPurge(string id, string confirmation)
        {
            RequireConfirmation(confirmation);
            var record = FindTrash(id);
            var op = NewOperation("delete", record.Name + " (corbeille)", "Suppression définitive");
            _ = Task.Run(() => RunExclusiveAsync(op, () => Task.FromResult("Supprimé (" + Purge(record) + " fichier(s)).")));
            return Copy(op);
        }

        /// <summary>Vide toute la corbeille (confirmation « SUPPRIMER »).</summary>
        public MoveOperationView StartEmptyTrash(string confirmation)
        {
            RequireConfirmation(confirmation);
            var op = NewOperation("delete", "Corbeille", "Vidage de la corbeille");
            _ = Task.Run(() => RunExclusiveAsync(op, () =>
            {
                List<TrashRecord> all;
                lock (_trashLock)
                {
                    all = LoadTrash().ToList();
                }

                var files = all.Sum(Purge);
                return Task.FromResult("Corbeille vidée (" + all.Count + " élément(s), " + files + " fichier(s)).");
            }));
            return Copy(op);
        }

        /// <summary>Vidage automatique des éléments plus anciens que la durée de conservation.</summary>
        public async Task<int> PurgeExpiredTrashAsync()
        {
            var retention = _manager.Options.TrashRetentionDays;
            if (retention <= 0)
            {
                return 0;
            }

            List<TrashRecord> expired;
            lock (_trashLock)
            {
                expired = LoadTrash().Where(r => r.DeletedUtc.AddDays(retention) <= DateTime.UtcNow).ToList();
            }

            if (expired.Count == 0)
            {
                return 0;
            }

            await _worker.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var record in expired)
                {
                    try
                    {
                        Purge(record);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("Torry: vidage automatique de la corbeille (" + record.Name + ") : " + DiskErrors.Describe(ex));
                    }
                }

                _log.Info("Torry: corbeille : " + expired.Count + " élément(s) de plus de " + retention + " jour(s) supprimé(s).");
                return expired.Count;
            }
            finally
            {
                _worker.Release();
            }
        }

        private int Purge(TrashRecord record)
        {
            var count = FileBrowser.DeleteDirectory(FileBrowser.TrashItemDirectory(record.RootPath, record.Id));
            lock (_trashLock)
            {
                LoadTrash().RemoveAll(r => r.Id == record.Id);
                SaveTrash();
            }

            return count;
        }

        private TrashRecord FindTrash(string id)
        {
            lock (_trashLock)
            {
                return LoadTrash().FirstOrDefault(r => r.Id == id) ?? throw new FileOperationException("Élément introuvable dans la corbeille.");
            }
        }

        private static void RequireConfirmation(string confirmation)
        {
            if (!string.Equals((confirmation ?? string.Empty).Trim(), FileBrowser.DeleteConfirmation, StringComparison.Ordinal))
            {
                throw new FileOperationException("Confirmation explicite requise : saisissez « " + FileBrowser.DeleteConfirmation + " ».");
            }
        }

        private bool TryRefresh(string directory)
        {
            try
            {
                return _library.RefreshNow(directory)?.Refreshed == true;
            }
            catch (Exception ex)
            {
                _log.Warn("Torry: actualisation de la bibliothèque : " + DiskErrors.SafeMessage(ex.Message));
                return false;
            }
        }

        private MoveOperationView NewOperation(string kind, string source, string target)
        {
            var op = new MoveOperationView
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = kind,
                State = "Pending",
                Source = source,
                Target = target,
                StartedUtc = DateTime.UtcNow
            };
            AddOperation(op);
            return op;
        }

        /// <summary>Exécute une opération dans la file (une à la fois) et met à jour son état.</summary>
        private async Task RunExclusiveAsync(MoveOperationView op, Func<Task<string>> work)
        {
            await _worker.WaitAsync().ConfigureAwait(false);
            try
            {
                Update(op, o => o.State = "Running");
                var message = await work().ConfigureAwait(false);
                Update(op, o =>
                {
                    o.State = "Done";
                    o.Message = message;
                    o.FinishedUtc = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                var message = ex is FileOperationException || ex is UnsafePathException ? ex.Message : DiskErrors.Describe(ex);
                Update(op, o =>
                {
                    o.State = "Error";
                    o.Message = message;
                    o.FinishedUtc = DateTime.UtcNow;
                });
                _log.Warn("Torry: opération « " + op.Kind + " » impossible (" + op.Source + ") : " + message);
            }
            finally
            {
                _worker.Release();
            }
        }

        public DirectoryListingView List(string rootId, string path) => FileBrowser.List(_manager.FindRoot(rootId), path);

        /// <summary>Type de contenu d'un élément et destination conseillée (pour la fenêtre de déplacement).</summary>
        public ItemAnalysisView Analyze(string rootId, string path)
        {
            var view = FileBrowser.Analyze(_manager.FindRoot(rootId), path);
            var suggested = MediaTypes.SuggestDestination(_manager.Options.Destinations, view.Files);
            view.SuggestedRootId = suggested?.Id;
            view.Files = null;
            return view;
        }

        public string CreateFolder(string rootId, string parentPath, string name)
        {
            var root = _manager.FindRoot(rootId);
            var created = FileBrowser.CreateFolder(root, parentPath, name);
            _log.Info("Torry: dossier créé dans « " + root.Name + " » : " + created);
            return created;
        }

        public IReadOnlyList<MoveOperationView> GetOperations()
        {
            lock (_opsLock)
            {
                return _operations.Select(Copy).ToList();
            }
        }

        /// <summary>Valide puis met en file un déplacement (ou un renommage si le dossier cible est le même).</summary>
        public MoveOperationView StartMove(string sourceRootId, string sourcePath, string targetRootId, string targetDirectory, string newName)
        {
            var plan = FileBrowser.PlanMove(_manager.FindRoot(sourceRootId), sourcePath, _manager.FindRoot(targetRootId), targetDirectory, newName);
            var op = new MoveOperationView
            {
                Id = Guid.NewGuid().ToString("N"),
                State = "Pending",
                Source = plan.SourceDisplay,
                Target = plan.TargetDisplay,
                StartedUtc = DateTime.UtcNow
            };

            lock (_opsLock)
            {
                _operations.Insert(0, op);
                while (_operations.Count > KeepOperations)
                {
                    var last = _operations.FindLastIndex(o => o.State == "Done" || o.State == "Error");
                    if (last < 0)
                    {
                        break;
                    }

                    _operations.RemoveAt(last);
                }
            }

            _ = Task.Run(() => RunAsync(plan, op));
            return Copy(op);
        }

        /// <summary>
        /// Met en file une suppression définitive. <paramref name="confirmation"/> doit valoir « SUPPRIMER »
        /// (confirmation explicite, vérifiée côté serveur).
        /// </summary>
        public MoveOperationView StartDelete(string rootId, string path, string confirmation)
        {
            if (!string.Equals((confirmation ?? string.Empty).Trim(), FileBrowser.DeleteConfirmation, StringComparison.Ordinal))
            {
                throw new FileOperationException("Confirmation explicite requise : saisissez « " + FileBrowser.DeleteConfirmation + " ».");
            }

            var plan = FileBrowser.PlanDelete(_manager.FindRoot(rootId), path);
            var op = new MoveOperationView
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = "delete",
                State = "Pending",
                Source = plan.SourceDisplay,
                Target = "Suppression définitive",
                StartedUtc = DateTime.UtcNow
            };

            AddOperation(op);
            _ = Task.Run(() => RunDeleteAsync(plan, op));
            return Copy(op);
        }

        private async Task RunDeleteAsync(MovePlan plan, MoveOperationView op)
        {
            await _worker.WaitAsync().ConfigureAwait(false);
            try
            {
                Update(op, o => o.State = "Running");
                var note = await _manager.BeforeMoveAsync(plan.Source).ConfigureAwait(false);
                var count = await Task.Run(() => FileBrowser.Delete(plan)).ConfigureAwait(false);
                await _manager.AfterDeleteAsync(plan.Source).ConfigureAwait(false);

                var refreshed = false;
                try
                {
                    refreshed = _library.RefreshNow(Path.GetDirectoryName(plan.Source))?.Refreshed == true;
                }
                catch (Exception ex)
                {
                    _log.Warn("Torry: actualisation après suppression : " + DiskErrors.SafeMessage(ex.Message));
                }

                var message = "Supprimé (" + count + " fichier(s))." + (refreshed ? " Bibliothèque actualisée." : string.Empty)
                    + (note == null ? string.Empty : " " + note);
                Update(op, o =>
                {
                    o.State = "Done";
                    o.Message = message;
                    o.FinishedUtc = DateTime.UtcNow;
                });
                _log.Info("Torry: supprimé depuis l'explorateur : " + op.Source + " (" + count + " fichier(s)).");
            }
            catch (Exception ex)
            {
                var message = ex is FileOperationException || ex is UnsafePathException ? ex.Message : DiskErrors.Describe(ex);
                Update(op, o =>
                {
                    o.State = "Error";
                    o.Message = message + " Une partie des fichiers a pu être supprimée.";
                    o.FinishedUtc = DateTime.UtcNow;
                });
                _log.Warn("Torry: suppression impossible (" + op.Source + ") : " + message);
            }
            finally
            {
                _worker.Release();
            }
        }

        private void AddOperation(MoveOperationView op)
        {
            lock (_opsLock)
            {
                _operations.Insert(0, op);
                while (_operations.Count > KeepOperations)
                {
                    var last = _operations.FindLastIndex(o => o.State == "Done" || o.State == "Error");
                    if (last < 0)
                    {
                        break;
                    }

                    _operations.RemoveAt(last);
                }
            }
        }

        /// <summary>Exécution synchrone (tests).</summary>
        internal Task RunForTestsAsync(MovePlan plan, MoveOperationView op) => RunAsync(plan, op);

        private async Task RunAsync(MovePlan plan, MoveOperationView op)
        {
            await _worker.WaitAsync().ConfigureAwait(false);
            try
            {
                Update(op, o => o.State = "Running");
                var note = await _manager.BeforeMoveAsync(plan.Source).ConfigureAwait(false);

                var target = FileBrowser.ReserveTarget(plan);
                var total = FileBrowser.MeasureSize(plan.Source);
                Update(op, o =>
                {
                    o.BytesTotal = total;
                    o.Target = FileBrowser.Display(plan.TargetRoot, target);
                });

                var copied = await Task.Run(() => FileBrowser.Execute(plan, target, done => Update(op, o =>
                {
                    o.BytesDone = done;
                    o.Copying = true;
                }))).ConfigureAwait(false);

                await _manager.AfterMoveAsync(plan.Source, target, plan.TargetRoot.Path).ConfigureAwait(false);

                // Emby : l'élément disparaît de l'ancienne bibliothèque et apparaît dans la nouvelle.
                var refreshed = new List<string>();
                foreach (var directory in new[] { Path.GetDirectoryName(plan.Source), target }.Distinct())
                {
                    try
                    {
                        var result = _library.RefreshNow(directory);
                        if (result != null && result.Refreshed)
                        {
                            refreshed.Add(directory);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("Torry: actualisation après déplacement : " + DiskErrors.SafeMessage(ex.Message));
                    }
                }

                var message = (copied ? "Déplacé (copie entre volumes puis suppression de l'original)." : "Déplacé.")
                    + (refreshed.Count > 0 ? " Bibliothèque actualisée." : string.Empty)
                    + (note == null ? string.Empty : " " + note);
                Update(op, o =>
                {
                    o.State = "Done";
                    o.BytesDone = o.BytesTotal;
                    o.Message = message;
                    o.FinishedUtc = DateTime.UtcNow;
                });
                _log.Info("Torry: déplacement terminé : " + op.Source + " → " + op.Target);
            }
            catch (Exception ex)
            {
                var message = ex is FileOperationException || ex is UnsafePathException ? ex.Message : DiskErrors.Describe(ex);
                Update(op, o =>
                {
                    o.State = "Error";
                    o.Message = message;
                    o.FinishedUtc = DateTime.UtcNow;
                });
                _log.Warn("Torry: déplacement impossible (" + op.Source + ") : " + message);
            }
            finally
            {
                _worker.Release();
            }
        }

        private void Update(MoveOperationView op, Action<MoveOperationView> change)
        {
            lock (_opsLock)
            {
                change(op);
            }
        }

        private static MoveOperationView Copy(MoveOperationView op) => new MoveOperationView
        {
            Id = op.Id,
            Kind = op.Kind,
            State = op.State,
            Source = op.Source,
            Target = op.Target,
            BytesTotal = op.BytesTotal,
            BytesDone = op.BytesDone,
            Copying = op.Copying,
            Message = op.Message,
            StartedUtc = op.StartedUtc,
            FinishedUtc = op.FinishedUtc
        };
    }
}
