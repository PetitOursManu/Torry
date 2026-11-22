using System;
using System.IO;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
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
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private TorrentTaskManager _manager;

        public TorryEntryPoint(ILogManager logManager, IApplicationPaths paths, ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, IProviderManager providerManager, IFileSystem fileSystem)
        {
            _logger = logManager.GetLogger("Torry");
            _paths = paths;
            _libraryManager = libraryManager;
            _libraryMonitor = libraryMonitor;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
        }

        /// <summary>Instance unique utilisée par l'API.</summary>
        public static TorrentTaskManager Manager { get; private set; }

        /// <summary>Explorateur de fichiers (déplacements) utilisé par l'API.</summary>
        public static FileOperationsService Files { get; private set; }

        public void Run()
        {
            var options = (Plugin.Instance?.Configuration ?? new PluginConfiguration()).ToOptions();
            var store = new TaskStore(Path.Combine(_paths.DataPath, "torry"));
            var log = new EmbyLog(_logger);
            var library = new EmbyLibraryNotifier(_libraryManager, _libraryMonitor, _providerManager, _fileSystem);
            _manager = new TorrentTaskManager(store, options, log, library);
            Manager = _manager;
            Files = new FileOperationsService(_manager, library, log);

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
            Files = null;
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
