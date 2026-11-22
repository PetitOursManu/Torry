using System;
using System.Collections.Generic;

namespace PetitOurs.Torry.Core
{
    /// <summary>Vue d'une tâche renvoyée à l'interface (aucun lien magnet, aucune URL de tracker).</summary>
    public sealed class TaskView
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string State { get; set; }

        public string StateLabel { get; set; }

        /// <summary>Progression 0-100 des fichiers sélectionnés.</summary>
        public double Progress { get; set; }

        public long DoneBytes { get; set; }

        public long SelectedBytes { get; set; }

        public long TotalBytes { get; set; }

        public long DownloadRate { get; set; }

        public long UploadRate { get; set; }

        /// <summary>Temps restant estimé en secondes (null si inconnu).</summary>
        public long? EtaSeconds { get; set; }

        public double Ratio { get; set; }

        public long UploadedBytes { get; set; }

        public int Peers { get; set; }

        public int Seeds { get; set; }

        public int FileCount { get; set; }

        public int SelectedFileCount { get; set; }

        public bool IsPrivate { get; set; }

        public string ErrorMessage { get; set; }

        public string LibraryNote { get; set; }

        public string FinalFolder { get; set; }

        /// <summary>Destination choisie (nom), null = téléchargements terminés.</summary>
        public string DestinationName { get; set; }

        public DateTime AddedUtc { get; set; }

        public DateTime? CompletedUtc { get; set; }

        public double SeedingSeconds { get; set; }

        public bool NeedsSelection { get; set; }

        public bool CanPause { get; set; }

        public bool CanResume { get; set; }

        public bool CanCancel { get; set; }

        public bool CanStopSeeding { get; set; }
    }

    public sealed class FileView
    {
        public int Index { get; set; }

        public string Path { get; set; }

        public long Length { get; set; }

        public bool Selected { get; set; }

        public double Progress { get; set; }

        public bool IsVideo { get; set; }
    }

    public sealed class EngineView
    {
        public bool Running { get; set; }

        public string StartupError { get; set; }

        public int ListenPort { get; set; }

        /// <summary>
        /// Vrai si le moteur écoute sur le port BitTorrent. MonoTorrent n'ouvre ses ports qu'au démarrage du premier
        /// torrent actif et les referme quand plus aucun ne l'est : un test de port échoue donc si la liste est vide.
        /// </summary>
        public bool Listening { get; set; }

        public bool DhtEnabled { get; set; }

        public int DhtNodes { get; set; }

        public string IncompleteDirectory { get; set; }

        public string CompletedDirectory { get; set; }

        public long? IncompleteFreeBytes { get; set; }

        public long? CompletedFreeBytes { get; set; }

        public bool CompletedInLibrary { get; set; }

        public long DownloadRate { get; set; }

        public long UploadRate { get; set; }

        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class StatusView
    {
        public EngineView Engine { get; set; } = new EngineView();

        public List<TaskView> Tasks { get; set; } = new List<TaskView>();

        /// <summary>Racines navigables : « Téléchargements terminés » puis les destinations configurées.</summary>
        public List<RootView> Roots { get; set; } = new List<RootView>();
    }

    /// <summary>Racine navigable dans l'explorateur de fichiers et utilisable comme destination.</summary>
    public sealed class RootView
    {
        /// <summary>« complete » pour les téléchargements terminés, sinon l'identifiant de la destination.</summary>
        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>Type de contenu Emby (null pour « Téléchargements terminés »).</summary>
        public string ContentType { get; set; }

        public string ContentTypeLabel { get; set; }

        public string Path { get; set; }

        public bool Available { get; set; }

        public long? FreeBytes { get; set; }
    }

    public sealed class TaskFilesView
    {
        public List<FileView> Files { get; set; } = new List<FileView>();

        /// <summary>Destination conseillée d'après le type des fichiers (null = téléchargements terminés).</summary>
        public string SuggestedDestinationId { get; set; }

        public string SuggestedContentType { get; set; }
    }

    public sealed class FileEntryView
    {
        public string Name { get; set; }

        /// <summary>Chemin relatif à la racine, séparé par « / ».</summary>
        public string Path { get; set; }

        public bool IsDirectory { get; set; }

        public long? Size { get; set; }

        public DateTime ModifiedUtc { get; set; }

        /// <summary>video, audio, image, book, subtitle, other, folder.</summary>
        public string Category { get; set; }

        /// <summary>Types de bibliothèque Emby compatibles (pour suggérer une destination).</summary>
        public List<string> CompatibleContentTypes { get; set; } = new List<string>();
    }

    public sealed class DirectoryListingView
    {
        public string RootId { get; set; }

        public string RootName { get; set; }

        /// <summary>Chemin relatif courant (« » pour la racine).</summary>
        public string Path { get; set; }

        public List<FileEntryView> Entries { get; set; } = new List<FileEntryView>();

        public bool Truncated { get; set; }
    }

    public sealed class MoveOperationView
    {
        public string Id { get; set; }

        /// <summary>Pending, Running, Done, Error.</summary>
        public string State { get; set; }

        public string Source { get; set; }

        public string Target { get; set; }

        public long BytesTotal { get; set; }

        public long BytesDone { get; set; }

        /// <summary>Vrai si le déplacement nécessite une copie (volumes différents).</summary>
        public bool Copying { get; set; }

        public string Message { get; set; }

        public DateTime StartedUtc { get; set; }

        public DateTime? FinishedUtc { get; set; }
    }

    /// <summary>Analyse d'un fichier ou d'un dossier avant déplacement : type de contenu et destination conseillée.</summary>
    public sealed class ItemAnalysisView
    {
        public string Name { get; set; }

        public bool IsDirectory { get; set; }

        public int FileCount { get; set; }

        public long TotalBytes { get; set; }

        /// <summary>Nombre de fichiers par catégorie (video, audio, image, book…).</summary>
        public Dictionary<string, int> Categories { get; set; } = new Dictionary<string, int>();

        /// <summary>Catégorie dominante (en taille) : video, audio, image, book, other.</summary>
        public string DominantCategory { get; set; }

        /// <summary>Type de bibliothèque Emby probable (movies, tvshows, music…), null si inconnu.</summary>
        public string SuggestedContentType { get; set; }

        public string SuggestedContentTypeLabel { get; set; }

        /// <summary>Types de bibliothèque capables d'afficher ce contenu.</summary>
        public List<string> CompatibleContentTypes { get; set; } = new List<string>();

        /// <summary>Racine conseillée (identifiant), null si aucune destination ne convient.</summary>
        public string SuggestedRootId { get; set; }

        /// <summary>Vrai si l'analyse s'est arrêtée avant la fin (dossier très volumineux).</summary>
        public bool Truncated { get; set; }

        /// <summary>Fichiers analysés (chemin, taille) : usage interne, jamais sérialisé.</summary>
        internal List<KeyValuePair<string, long>> Files { get; set; }
    }

    public sealed class LibraryInfo
    {
        public string Name { get; set; }

        public string ContentType { get; set; }

        public List<string> Locations { get; set; } = new List<string>();
    }

    public sealed class RemoveResult
    {
        public int DeletedFiles { get; set; }

        public List<string> Messages { get; set; } = new List<string>();
    }
}
