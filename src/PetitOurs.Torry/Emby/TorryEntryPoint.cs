using System;
using System.IO;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using PetitOurs.Torry.Engine;
using PetitOurs.Torry.Persistence;

namespace PetitOurs.Torry.Emby
{
    /// <summary>
    /// Démarre le moteur en arrière-plan (sans bloquer le démarrage d'Emby) et l'arrête proprement
    /// à l'arrêt du serveur (fast resume + état des tâches enregistrés).
    /// </summary>
    public sealed class TorryEntryPoint : IServerEntryPoint
    {
        private readonly ILogger _logger;
        private readonly IApplicationPaths _paths;
        private readonly ILibraryManager _libraryManager;
        private readonly ILibraryMonitor _libraryMonitor;
        private TorrentTaskManager _manager;

        public TorryEntryPoint(ILogManager logManager, IApplicationPaths paths, ILibraryManager libraryManager, ILibraryMonitor libraryMonitor)
        {
            _logger = logManager.GetLogger("Torry");
            _paths = paths;
            _libraryManager = libraryManager;
            _libraryMonitor = libraryMonitor;
        }

        /// <summary>Instance unique utilisée par l'API.</summary>
        public static TorrentTaskManager Manager { get; private set; }

        public void Run()
        {
            var options = (Plugin.Instance?.Configuration ?? new PluginConfiguration()).ToOptions();
            var store = new TaskStore(Path.Combine(_paths.DataPath, "torry"));
            _manager = new TorrentTaskManager(store, options, new EmbyLog(_logger), new EmbyLibraryNotifier(_libraryManager, _libraryMonitor));
            Manager = _manager;

            // Démarrage asynchrone : la restauration des tâches et le moteur ne retardent pas Emby.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _manager.StartAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Torry: démarrage impossible", ex);
                }
            });
        }

        public void Dispose()
        {
            var manager = _manager;
            _manager = null;
            Manager = null;
            if (manager == null)
            {
                return;
            }

            try
            {
                if (!Task.Run(manager.StopAsync).Wait(TimeSpan.FromSeconds(20)))
                {
                    _logger.Warn("Torry: arrêt du moteur interrompu après 20 s.");
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Torry: erreur à l'arrêt", ex);
            }
        }
    }
}
