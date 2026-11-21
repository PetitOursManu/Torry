using System;
using System.Linq;
using MediaBrowser.Controller.Library;
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

        public EmbyLibraryNotifier(ILibraryManager libraryManager, ILibraryMonitor libraryMonitor)
        {
            _libraryManager = libraryManager;
            _libraryMonitor = libraryMonitor;
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
