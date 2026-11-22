using System;
using System.Collections.Generic;

namespace PetitOurs.Torry.Persistence
{
    public enum TaskState
    {
        /// <summary>Lien magnet ajouté : recherche des métadonnées auprès des pairs.</summary>
        FetchingMetadata = 0,

        /// <summary>Métadonnées connues : en attente de la sélection des fichiers.</summary>
        AwaitingSelection = 1,

        /// <summary>En file d'attente (limite de téléchargements simultanés atteinte).</summary>
        Queued = 2,

        /// <summary>Vérification des données déjà présentes.</summary>
        Checking = 3,

        Downloading = 4,

        Paused = 5,

        /// <summary>Déplacement des fichiers terminés vers le dossier final.</summary>
        Finalizing = 6,

        /// <summary>Fichiers disponibles, partage en cours.</summary>
        Seeding = 7,

        /// <summary>Fichiers disponibles, partage terminé.</summary>
        Completed = 8,

        /// <summary>Téléchargement annulé par l'utilisateur (fichiers conservés).</summary>
        Cancelled = 9,

        Error = 10
    }

    public sealed class TorrentFileEntry
    {
        public int Index { get; set; }

        /// <summary>Chemin relatif dans le torrent (validé par PathGuard).</summary>
        public string Path { get; set; }

        public long Length { get; set; }

        public bool Selected { get; set; }

        /// <summary>Emplacement définitif après finalisation (null avant).</summary>
        public string FinalPath { get; set; }
    }

    /// <summary>Tâche persistée. Le lien magnet est stocké (nécessaire à la reprise) mais n'est jamais journalisé.</summary>
    public sealed class TaskRecord
    {
        public string Id { get; set; }

        public string InfoHash { get; set; }

        public string Name { get; set; }

        /// <summary>"magnet" ou "torrent".</summary>
        public string Source { get; set; }

        public string MagnetUri { get; set; }

        public bool HasMetadata { get; set; }

        public bool IsPrivate { get; set; }

        public TaskState State { get; set; }

        /// <summary>État à rétablir lors d'une reprise (après pause, erreur ou redémarrage).</summary>
        public TaskState ResumeState { get; set; }

        public bool SelectionConfirmed { get; set; }

        /// <summary>Identifiant du dossier de destination choisi (null = téléchargements terminés).</summary>
        public string DestinationId { get; set; }

        public List<TorrentFileEntry> Files { get; set; } = new List<TorrentFileEntry>();

        public long TotalBytes { get; set; }

        public long SelectedBytes { get; set; }

        /// <summary>Racine « en cours » autorisée au moment de la création de la tâche.</summary>
        public string IncompleteRoot { get; set; }

        /// <summary>Racine « terminés » autorisée au moment de la finalisation.</summary>
        public string CompletedRoot { get; set; }

        /// <summary>Dossier de travail &lt;en cours&gt;/&lt;id&gt;.</summary>
        public string WorkDirectory { get; set; }

        /// <summary>Dossier final dans &lt;terminés&gt; (défini à la finalisation).</summary>
        public string FinalDirectory { get; set; }

        /// <summary>Progression (0-100) des fichiers sélectionnés, mémorisée pour l'affichage hors moteur.</summary>
        public double Progress { get; set; }

        public long UploadedBytes { get; set; }

        public long DownloadedBytes { get; set; }

        public DateTime AddedUtc { get; set; }

        public DateTime? CompletedUtc { get; set; }

        /// <summary>Durée de partage cumulée (secondes), conservée après redémarrage.</summary>
        public double SeedingSeconds { get; set; }

        public string ErrorMessage { get; set; }

        public string LibraryNote { get; set; }
    }

    public sealed class StoreDocument
    {
        public int Version { get; set; } = 1;

        public List<TaskRecord> Tasks { get; set; } = new List<TaskRecord>();
    }
}
