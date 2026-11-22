using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using PetitOurs.Torry.Core;
using PetitOurs.Torry.Files;

namespace PetitOurs.Torry.Emby
{
    internal sealed class EmbyLog : ITorryLog
    {
        private readonly ILogger _logger;

        public EmbyLog(ILogger logger)
        {
            _logger = logger;
        }

        // Les messages sont passés comme arguments ({0}) pour que les accolades éventuelles d'un nom de torrent
        // ne soient pas interprétées comme un format.
        public void Info(string message) => _logger.Info("{0}", message);

        public void Warn(string message) => _logger.Warn("{0}", message);

        public void Error(string message, Exception exception = null)
        {
            if (exception == null)
            {
                _logger.Error("{0}", message);
            }
            else
            {
                _logger.ErrorException("{0}", exception, message);
            }
        }
    }

    /// <summary>
    /// Demande à Emby d'actualiser uniquement le dossier ajouté (ILibraryMonitor.ReportFileSystemChanged),
    /// ce qui déclenche l'analyse de la bibliothèque qui le contient, sans relancer une analyse complète.
    /// </summary>
    internal sealed class EmbyLibraryNotifier : ILibraryNotifier
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILibraryMonitor _libraryMonitor;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;

        public EmbyLibraryNotifier(ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, IProviderManager providerManager, IFileSystem fileSystem)
        {
            _libraryManager = libraryManager;
            _libraryMonitor = libraryMonitor;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
        }

        /// <summary>
        /// Actualisation immédiate : on retrouve l'élément de bibliothèque le plus proche du dossier (le dossier
        /// lui-même ou, à défaut, son parent connu d'Emby) et on demande son analyse récursive en priorité haute.
        /// C'est ce que fait la surveillance d'Emby après son délai (90 s par défaut), sans relancer une analyse complète.
        /// </summary>
        public LibraryRefreshResult RefreshNow(string directory)
        {
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => (f.Locations ?? Array.Empty<string>()).Any(location => PathGuard.IsSameOrInside(location, directory)))
                .ToList();

            if (libraries.Count == 0)
            {
                return new LibraryRefreshResult
                {
                    Refreshed = false,
                    Message = "Aucune bibliothèque Emby ne contient le dossier des téléchargements terminés : ajoutez-le à une bibliothèque."
                };
            }

            var queued = new HashSet<long>();
            foreach (var library in libraries)
            {
                foreach (var location in library.Locations.Where(location => PathGuard.IsSameOrInside(location, directory)))
                {
                    var item = FindNearestItem(directory, location);
                    if (item != null && queued.Add(item.InternalId))
                    {
                        _providerManager.QueueRefresh(item.InternalId, new MetadataRefreshOptions(_fileSystem) { Recursive = true }, RefreshPriority.High);
                    }
                }
            }

            var names = string.Join(", ", libraries.Select(l => l.Name));
            if (queued.Count == 0)
            {
                // Dossier encore inconnu d'Emby (bibliothèque jamais analysée) : analyse complète.
                _libraryManager.QueueLibraryScan();
                return new LibraryRefreshResult { Refreshed = true, Message = "Analyse des bibliothèques lancée (" + names + ")." };
            }

            return new LibraryRefreshResult { Refreshed = true, Message = "Actualisation lancée : " + names + ". Les nouveaux fichiers apparaîtront d'ici quelques instants." };
        }

        private BaseItem FindNearestItem(string directory, string libraryLocation)
        {
            var path = directory;
            while (!string.IsNullOrEmpty(path) && PathGuard.IsSameOrInside(libraryLocation, path))
            {
                var item = _libraryManager.FindByPath(path, true);
                if (item != null && !(item is AggregateFolder))
                {
                    return item;
                }

                path = Path.GetDirectoryName(path);
            }

            return null;
        }

        public IReadOnlyList<LibraryInfo> GetLibraries()
        {
            return _libraryManager.GetVirtualFolders()
                .Select(f => new LibraryInfo
                {
                    Name = f.Name,
                    ContentType = string.IsNullOrEmpty(f.CollectionType) ? "mixed" : f.CollectionType,
                    Locations = (f.Locations ?? Array.Empty<string>()).ToList()
                })
                .ToList();
        }

        public bool IsInLibrary(string path)
        {
            return _libraryManager.GetVirtualFolders()
                .SelectMany(f => f.Locations ?? Array.Empty<string>())
                .Any(location => PathGuard.IsSameOrInside(location, path));
        }

        public string NotifyNewContent(string directory)
        {
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => (f.Locations ?? Array.Empty<string>()).Any(location => PathGuard.IsSameOrInside(location, directory)))
                .Select(f => f.Name)
                .ToList();

            if (libraries.Count == 0)
            {
                return "Aucune bibliothèque Emby ne contient ce dossier : ajoutez le dossier des téléchargements terminés à une bibliothèque.";
            }

            _libraryMonitor.ReportFileSystemChanged(directory);
            return "Actualisation demandée : " + string.Join(", ", libraries) + ".";
        }
    }
}
