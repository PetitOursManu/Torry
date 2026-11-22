using System;
using System.Collections.Generic;

namespace PetitOurs.Torry.Core
{
    /// <summary>Journalisation. Ne jamais y passer de lien magnet ni d'URL de tracker.</summary>
    public interface ITorryLog
    {
        void Info(string message);

        void Warn(string message);

        void Error(string message, Exception exception = null);
    }

    /// <summary>Pont vers la bibliothèque Emby (implémenté dans la couche Emby).</summary>
    public interface ILibraryNotifier
    {
        /// <summary>Vrai si le dossier est couvert par au moins une bibliothèque Emby.</summary>
        bool IsInLibrary(string path);

        /// <summary>Demande l'actualisation de la bibliothèque contenant ce dossier. Renvoie une note lisible.</summary>
        string NotifyNewContent(string directory);

        /// <summary>
        /// Actualisation immédiate (sans le délai de surveillance d'Emby) de la ou des bibliothèques contenant ce dossier.
        /// </summary>
        LibraryRefreshResult RefreshNow(string directory);

        /// <summary>Bibliothèques Emby (nom, type de contenu, emplacements), pour préremplir les destinations.</summary>
        IReadOnlyList<LibraryInfo> GetLibraries();
    }

    public sealed class LibraryRefreshResult
    {
        public bool Refreshed { get; set; }

        public string Message { get; set; }
    }

    public sealed class NullLibraryNotifier : ILibraryNotifier
    {
        public bool IsInLibrary(string path) => false;

        public string NotifyNewContent(string directory) => null;

        public LibraryRefreshResult RefreshNow(string directory) =>
            new LibraryRefreshResult { Refreshed = false, Message = "Bibliothèque Emby indisponible." };

        public IReadOnlyList<LibraryInfo> GetLibraries() => Array.Empty<LibraryInfo>();
    }

    public sealed class NullLog : ITorryLog
    {
        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message, Exception exception = null)
        {
        }
    }
}
