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
    }

    public sealed class RemoveResult
    {
        public int DeletedFiles { get; set; }

        public List<string> Messages { get; set; } = new List<string>();
    }
}
