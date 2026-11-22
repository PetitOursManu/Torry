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
        }

        public DirectoryListingView List(string rootId, string path) => FileBrowser.List(_manager.FindRoot(rootId), path);

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
