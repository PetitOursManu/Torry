using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PetitOurs.Torry.Persistence;

namespace PetitOurs.Torry.Files
{
    /// <summary>Opérations disque de Torry, toujours bornées aux dossiers autorisés.</summary>
    public sealed class FileManager
    {
        public FileManager(string incompleteRoot, string completedRoot)
        {
            IncompleteRoot = PathGuard.NormalizeDirectory(incompleteRoot);
            CompletedRoot = PathGuard.NormalizeDirectory(completedRoot);
        }

        public string IncompleteRoot { get; }

        public string CompletedRoot { get; }

        public static bool IsValidTaskId(string taskId)
        {
            return !string.IsNullOrEmpty(taskId) && taskId.Length <= 64 && taskId.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'));
        }

        /// <summary>Dossier de travail d'une tâche : &lt;en cours&gt;/&lt;id&gt;.</summary>
        public static string GetTaskWorkDirectory(string incompleteRoot, string taskId)
        {
            if (!IsValidTaskId(taskId))
            {
                throw new UnsafePathException("Identifiant de tâche invalide.");
            }

            return Path.Combine(PathGuard.NormalizeDirectory(incompleteRoot), taskId);
        }

        public string GetTaskWorkDirectory(string taskId) => GetTaskWorkDirectory(IncompleteRoot, taskId);

        /// <summary>Vérifie qu'un dossier existe (le crée sinon) et qu'il est accessible en écriture. Renvoie null si tout va bien.</summary>
        public static string CheckWritable(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, ".torry-write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return null;
            }
            catch (Exception ex)
            {
                return DiskErrors.Describe(ex);
            }
        }

        /// <summary>Espace libre en octets sur le volume contenant le dossier (null si inconnu).</summary>
        public static long? GetAvailableFreeSpace(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return null;
                }

                return new DriveInfo(Path.GetFullPath(directory)).AvailableFreeSpace;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Prépare le dossier de destination final : &lt;terminés&gt;/&lt;nom unique&gt;.
        /// Le dossier est créé immédiatement pour réserver le nom (aucun écrasement possible).
        /// </summary>
        public string CreateFinalDirectory(string torrentName, string fallbackName) => CreateFinalDirectory(CompletedRoot, torrentName, fallbackName);

        /// <summary>Même chose dans une racine quelconque (dossier de destination).</summary>
        public static string CreateFinalDirectory(string root, string torrentName, string fallbackName)
        {
            root = PathGuard.NormalizeDirectory(root);
            Directory.CreateDirectory(root);
            var folderName = PathGuard.SanitizeFolderName(torrentName, fallbackName);
            var path = PathGuard.GetUniqueDirectory(root, folderName);
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// Supprime les fichiers d'une tâche. Ne touche qu'à :
        ///  - son dossier de travail, s'il vaut exactement &lt;racine en cours&gt;/&lt;id&gt; ;
        ///  - ses fichiers finalisés, s'ils sont dans son dossier final, lui-même dans la racine « terminés ».
        /// Les liens symboliques ne sont jamais suivis.
        /// </summary>
        public static DeletionReport DeleteTaskFiles(TaskRecord record)
        {
            var report = new DeletionReport();

            if (!string.IsNullOrEmpty(record.WorkDirectory) && !string.IsNullOrEmpty(record.IncompleteRoot))
            {
                var expected = GetTaskWorkDirectory(record.IncompleteRoot, record.Id);
                if (string.Equals(PathGuard.NormalizeDirectory(record.WorkDirectory), expected, StringComparison.Ordinal)
                    && PathGuard.IsInside(record.IncompleteRoot, expected))
                {
                    if (Directory.Exists(expected))
                    {
                        DeleteDirectoryNoFollow(expected, report);
                    }
                }
                else
                {
                    report.Skipped.Add("dossier de travail hors du dossier autorisé");
                }
            }

            var finalDirectoryAllowed = !string.IsNullOrEmpty(record.FinalDirectory)
                && !string.IsNullOrEmpty(record.CompletedRoot)
                && PathGuard.IsInside(record.CompletedRoot, record.FinalDirectory);

            foreach (var entry in record.Files.Where(f => !string.IsNullOrEmpty(f.FinalPath)))
            {
                if (!finalDirectoryAllowed || !PathGuard.IsInside(record.FinalDirectory, entry.FinalPath))
                {
                    report.Skipped.Add("fichier hors du dossier autorisé");
                    continue;
                }

                try
                {
                    var info = new FileInfo(entry.FinalPath);
                    if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        info.Delete();
                        report.DeletedFiles++;
                    }
                }
                catch (Exception ex)
                {
                    report.Errors.Add(DiskErrors.Describe(ex));
                }
            }

            if (finalDirectoryAllowed)
            {
                RemoveEmptyDirectories(record.FinalDirectory, removeRoot: true);
            }

            return report;
        }

        /// <summary>Supprime récursivement les sous-dossiers vides (sans suivre les liens symboliques).</summary>
        public static void RemoveEmptyDirectories(string directory, bool removeRoot)
        {
            try
            {
                var info = new DirectoryInfo(directory);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }

                foreach (var sub in info.GetDirectories())
                {
                    RemoveEmptyDirectories(sub.FullName, removeRoot: true);
                }

                if (removeRoot && info.GetFileSystemInfos().Length == 0)
                {
                    info.Delete();
                }
            }
            catch
            {
                // Nettoyage opportuniste : une erreur ici n'est pas bloquante.
            }
        }

        private static void DeleteDirectoryNoFollow(string directory, DeletionReport report)
        {
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Lien symbolique : on supprime le lien, jamais sa cible.
                info.Delete();
                return;
            }

            foreach (var entry in info.GetFileSystemInfos())
            {
                try
                {
                    if (entry is DirectoryInfo sub)
                    {
                        DeleteDirectoryNoFollow(sub.FullName, report);
                    }
                    else
                    {
                        entry.Delete();
                        report.DeletedFiles++;
                    }
                }
                catch (Exception ex)
                {
                    report.Errors.Add(DiskErrors.Describe(ex));
                }
            }

            try
            {
                info.Delete();
            }
            catch (Exception ex)
            {
                report.Errors.Add(DiskErrors.Describe(ex));
            }
        }
    }

    public sealed class DeletionReport
    {
        public int DeletedFiles { get; set; }

        public List<string> Skipped { get; } = new List<string>();

        public List<string> Errors { get; } = new List<string>();
    }
}
