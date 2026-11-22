using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PetitOurs.Torry.Core;

namespace PetitOurs.Torry.Files
{
    /// <summary>Racine navigable : dossier des téléchargements terminés ou dossier de destination.</summary>
    public sealed class BrowseRoot
    {
        public const string CompletedId = "complete";

        public string Id { get; set; }

        public string Name { get; set; }

        public string Path { get; set; }

        public string ContentType { get; set; }
    }

    /// <summary>Erreur d'opération de fichiers destinée à l'utilisateur.</summary>
    public sealed class FileOperationException : Exception
    {
        public FileOperationException(string message)
            : base(message)
        {
        }
    }

    /// <summary>Déplacement validé, prêt à être exécuté.</summary>
    public sealed class MovePlan
    {
        public string Source { get; set; }

        /// <summary>Dossier qui recevra l'élément.</summary>
        public string TargetDirectory { get; set; }

        /// <summary>Nom souhaité dans le dossier cible (un suffixe « (2) » est ajouté en cas de collision).</summary>
        public string TargetName { get; set; }

        public BrowseRoot SourceRoot { get; set; }

        public BrowseRoot TargetRoot { get; set; }

        public bool IsDirectory { get; set; }

        public string SourceDisplay { get; set; }

        public string TargetDisplay { get; set; }
    }

    /// <summary>
    /// Mini explorateur de fichiers borné aux racines autorisées. Toutes les entrées sont des chemins relatifs
    /// à une racine ; les liens symboliques ne sont ni suivis ni manipulés.
    /// </summary>
    public static class FileBrowser
    {
        public const int MaxEntries = 2000;

        private static readonly char[] Separators = { '/', '\\' };

        /// <summary>Chemin absolu correspondant à un chemin relatif dans une racine (aucune sortie possible).</summary>
        public static string Resolve(BrowseRoot root, string relative)
        {
            if (root == null || string.IsNullOrWhiteSpace(root.Path))
            {
                throw new FileOperationException("Dossier racine introuvable.");
            }

            var rootPath = PathGuard.NormalizeDirectory(root.Path);
            var cleaned = (relative ?? string.Empty).Trim().Trim(Separators);
            if (cleaned.Length == 0)
            {
                return rootPath;
            }

            var full = PathGuard.CombineInside(rootPath, cleaned);
            EnsureNoSymlink(rootPath, full);
            return full;
        }

        /// <summary>Chemin relatif « a/b/c » d'un chemin absolu situé dans la racine.</summary>
        public static string ToRelative(BrowseRoot root, string full)
        {
            var rootPath = PathGuard.NormalizeDirectory(root.Path);
            var normalized = Path.GetFullPath(full).TrimEnd(Separators);
            if (string.Equals(normalized, rootPath, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return normalized.Substring(rootPath.Length).TrimStart(Separators).Replace('\\', '/');
        }

        public static DirectoryListingView List(BrowseRoot root, string relative)
        {
            var full = Resolve(root, relative);
            var directory = new DirectoryInfo(full);
            if (!directory.Exists)
            {
                if (File.Exists(full))
                {
                    throw new FileOperationException("Ce chemin est un fichier, pas un dossier.");
                }

                if (string.IsNullOrEmpty((relative ?? string.Empty).Trim(Separators)))
                {
                    throw new FileOperationException("Le dossier « " + root.Name + " » n'existe pas ou n'est pas monté dans le conteneur.");
                }

                throw new FileOperationException("Dossier introuvable.");
            }

            var view = new DirectoryListingView
            {
                RootId = root.Id,
                RootName = root.Name,
                Path = ToRelative(root, full)
            };

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                throw new FileOperationException(DiskErrors.Describe(ex));
            }

            var list = new List<FileEntryView>();
            foreach (var entry in entries)
            {
                try
                {
                    if (entry.LinkTarget != null || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue; // liens symboliques ignorés
                    }

                    var isDirectory = entry is DirectoryInfo;
                    var category = isDirectory ? "folder" : MediaTypes.GetCategory(entry.Name).ToString().ToLowerInvariant();
                    list.Add(new FileEntryView
                    {
                        Name = entry.Name,
                        Path = ToRelative(root, entry.FullName),
                        IsDirectory = isDirectory,
                        Size = isDirectory ? (long?)null : ((FileInfo)entry).Length,
                        ModifiedUtc = entry.LastWriteTimeUtc,
                        Category = category,
                        CompatibleContentTypes = isDirectory
                            ? new List<string>()
                            : MediaTypes.CompatibleContentTypes(MediaTypes.GetCategory(entry.Name)).ToList()
                    });
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    // Entrée illisible : ignorée.
                }

                if (list.Count >= MaxEntries)
                {
                    view.Truncated = true;
                    break;
                }
            }

            view.Entries = list
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return view;
        }

        /// <summary>Valide un nom de fichier ou de dossier saisi par l'utilisateur (un seul composant).</summary>
        public static string ValidateName(string name)
        {
            var trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                throw new FileOperationException("Le nom ne peut pas être vide.");
            }

            if (trimmed.IndexOfAny(Separators) >= 0)
            {
                throw new FileOperationException("Le nom ne peut pas contenir « / » ni « \\ ».");
            }

            if (!PathGuard.TryValidateRelativePath(trimmed, out var reason))
            {
                throw new FileOperationException("Nom refusé (" + reason + ").");
            }

            return trimmed;
        }

        public static string CreateFolder(BrowseRoot root, string parentRelative, string name)
        {
            var parent = Resolve(root, parentRelative);
            if (!Directory.Exists(parent))
            {
                throw new FileOperationException("Dossier parent introuvable.");
            }

            var target = PathGuard.CombineInside(parent, ValidateName(name));
            if (Directory.Exists(target) || File.Exists(target))
            {
                throw new FileOperationException("Un élément porte déjà ce nom.");
            }

            try
            {
                Directory.CreateDirectory(target);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                throw new FileOperationException(DiskErrors.Describe(ex));
            }

            return ToRelative(root, target);
        }

        /// <summary>Valide un déplacement (ou un renommage : même dossier, autre nom).</summary>
        public static MovePlan PlanMove(BrowseRoot sourceRoot, string sourceRelative, BrowseRoot targetRoot, string targetDirectoryRelative, string newName)
        {
            var source = Resolve(sourceRoot, sourceRelative);
            if (string.Equals(source, PathGuard.NormalizeDirectory(sourceRoot.Path), StringComparison.Ordinal))
            {
                throw new FileOperationException("Impossible de déplacer un dossier racine.");
            }

            var isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source))
            {
                throw new FileOperationException("Élément introuvable.");
            }

            var targetDirectory = Resolve(targetRoot, targetDirectoryRelative);
            if (!Directory.Exists(targetDirectory))
            {
                throw new FileOperationException("Dossier de destination introuvable.");
            }

            if (isDirectory && PathGuard.IsSameOrInside(source, targetDirectory))
            {
                throw new FileOperationException("Impossible de déplacer un dossier dans lui-même.");
            }

            var name = string.IsNullOrWhiteSpace(newName) ? Path.GetFileName(source) : ValidateName(newName);
            var target = PathGuard.CombineInside(targetDirectory, name);
            if (string.Equals(target, source, StringComparison.Ordinal))
            {
                throw new FileOperationException("L'élément est déjà à cet emplacement, sous ce nom.");
            }

            return new MovePlan
            {
                Source = source,
                TargetDirectory = targetDirectory,
                TargetName = name,
                SourceRoot = sourceRoot,
                TargetRoot = targetRoot,
                IsDirectory = isDirectory,
                SourceDisplay = Display(sourceRoot, source),
                TargetDisplay = Display(targetRoot, target)
            };
        }

        /// <summary>Chemin final libre (« Nom (2) » en cas de collision ; rien n'est jamais écrasé).</summary>
        public static string ReserveTarget(MovePlan plan)
        {
            var desired = Path.Combine(plan.TargetDirectory, plan.TargetName);
            var target = plan.IsDirectory
                ? PathGuard.GetUniqueDirectory(plan.TargetDirectory, plan.TargetName)
                : PathGuard.GetUniqueFilePath(desired);
            if (!PathGuard.IsInside(plan.TargetRoot.Path, target) && !string.Equals(target, PathGuard.NormalizeDirectory(plan.TargetRoot.Path), StringComparison.Ordinal))
            {
                throw new UnsafePathException("Destination refusée.");
            }

            return target;
        }

        public const int MaxAnalyzedFiles = 5000;

        /// <summary>
        /// Analyse un fichier ou un dossier (récursivement, liens ignorés, 5000 fichiers au plus) pour en déduire
        /// le type de contenu Emby : un album FLAC → Musique, des épisodes SxxEyy → Séries, etc.
        /// </summary>
        public static ItemAnalysisView Analyze(BrowseRoot root, string relative)
        {
            var full = Resolve(root, relative);
            var isDirectory = Directory.Exists(full);
            if (!isDirectory && !File.Exists(full))
            {
                throw new FileOperationException("Élément introuvable.");
            }

            var view = new ItemAnalysisView { Name = Path.GetFileName(full), IsDirectory = isDirectory };
            var files = new List<KeyValuePair<string, long>>();
            if (!isDirectory)
            {
                files.Add(new KeyValuePair<string, long>(view.Name, new FileInfo(full).Length));
            }
            else
            {
                var stack = new Stack<DirectoryInfo>();
                stack.Push(new DirectoryInfo(full));
                while (stack.Count > 0 && !view.Truncated)
                {
                    IEnumerable<FileSystemInfo> entries;
                    var dir = stack.Pop();
                    try
                    {
                        entries = dir.EnumerateFileSystemInfos().ToList();
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                    {
                        continue;
                    }

                    foreach (var entry in entries)
                    {
                        if (entry.LinkTarget != null)
                        {
                            continue;
                        }

                        if (entry is DirectoryInfo sub)
                        {
                            stack.Push(sub);
                            continue;
                        }

                        // Chemin relatif à l'élément analysé : « Saison 1/S01E01.mkv » aide à reconnaître une série.
                        var rel = entry.FullName.Substring(full.Length).TrimStart(Separators);
                        files.Add(new KeyValuePair<string, long>(view.Name + "/" + rel, ((FileInfo)entry).Length));
                        if (files.Count >= MaxAnalyzedFiles)
                        {
                            view.Truncated = true;
                            break;
                        }
                    }
                }
            }

            view.FileCount = files.Count;
            view.TotalBytes = files.Sum(f => f.Value);
            foreach (var group in files.GroupBy(f => MediaTypes.GetCategory(f.Key)))
            {
                view.Categories[group.Key.ToString().ToLowerInvariant()] = group.Count();
            }

            var dominant = files
                .GroupBy(f => MediaTypes.GetCategory(f.Key))
                .Where(g => g.Key != FileCategory.Other && g.Key != FileCategory.Subtitle)
                .OrderByDescending(g => g.Sum(f => Math.Max(1, f.Value)))
                .Select(g => (FileCategory?)g.Key)
                .FirstOrDefault() ?? FileCategory.Other;
            view.DominantCategory = dominant.ToString().ToLowerInvariant();
            view.SuggestedContentType = MediaTypes.SuggestContentType(files);
            view.SuggestedContentTypeLabel = view.SuggestedContentType == null ? null : MediaTypes.Label(view.SuggestedContentType);
            view.CompatibleContentTypes = MediaTypes.CompatibleContentTypes(dominant).ToList();
            view.Files = files;
            return view;
        }

        /// <summary>Taille totale (octets) d'un fichier ou d'un dossier, sans suivre les liens.</summary>
        public static long MeasureSize(string path)
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }

            long total = 0;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(path));
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                foreach (var entry in dir.EnumerateFileSystemInfos())
                {
                    if (entry.LinkTarget != null)
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo sub)
                    {
                        stack.Push(sub);
                    }
                    else
                    {
                        total += ((FileInfo)entry).Length;
                    }
                }
            }

            return total;
        }

        /// <summary>
        /// Exécute le déplacement vers <paramref name="target"/> (qui ne doit pas exister).
        /// Même système de fichiers : simple renommage, instantané. Sinon (autre volume Docker) : copie avec
        /// progression puis suppression de la source ; en cas d'échec, la copie partielle est supprimée et la
        /// source reste intacte. Renvoie vrai si une copie a été nécessaire.
        /// </summary>
        public static bool Execute(MovePlan plan, string target, Action<long> onProgress)
        {
            if (plan.IsDirectory)
            {
                try
                {
                    Directory.Move(plan.Source, target);
                    return false;
                }
                catch (IOException) when (Directory.Exists(plan.Source) && !Directory.Exists(target) && !File.Exists(target))
                {
                    // Volumes différents (EXDEV) : copie puis suppression.
                }

                try
                {
                    long done = 0;
                    CopyDirectory(new DirectoryInfo(plan.Source), target, ref done, onProgress);
                    DeleteTree(plan.Source);
                    return true;
                }
                catch
                {
                    TryDeleteTree(target);
                    throw;
                }
            }

            try
            {
                // .NET fait lui-même la copie + suppression si les volumes diffèrent.
                File.Move(plan.Source, target, overwrite: false);
                onProgress?.Invoke(new FileInfo(target).Length);
                return false;
            }
            catch
            {
                if (File.Exists(plan.Source) && File.Exists(target))
                {
                    TryDeleteFile(target); // copie partielle
                }

                throw;
            }
        }

        public static string Display(BrowseRoot root, string full)
        {
            var relative = ToRelative(root, full);
            return root.Name + (relative.Length == 0 ? string.Empty : " › " + relative.Replace("/", " › "));
        }

        private static void EnsureNoSymlink(string rootPath, string full)
        {
            var relative = full.Substring(rootPath.Length).TrimStart(Separators);
            var current = rootPath;
            foreach (var part in relative.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : (FileSystemInfo)new FileInfo(current);
                if (info.Exists && info.LinkTarget != null)
                {
                    throw new UnsafePathException("Les liens symboliques ne sont pas pris en charge.");
                }
            }
        }

        private static void CopyDirectory(DirectoryInfo source, string target, ref long done, Action<long> onProgress)
        {
            Directory.CreateDirectory(target);
            foreach (var entry in source.EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget != null)
                {
                    continue;
                }

                var destination = Path.Combine(target, entry.Name);
                if (entry is DirectoryInfo sub)
                {
                    CopyDirectory(sub, destination, ref done, onProgress);
                    Directory.SetLastWriteTimeUtc(destination, sub.LastWriteTimeUtc);
                }
                else
                {
                    var file = (FileInfo)entry;
                    using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                    {
                        var buffer = new byte[1 << 20];
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, read);
                            done += read;
                            onProgress?.Invoke(done);
                        }
                    }

                    File.SetLastWriteTimeUtc(destination, file.LastWriteTimeUtc);
                }
            }
        }

        private static void DeleteTree(string path)
        {
            var info = new DirectoryInfo(path);
            foreach (var entry in info.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo sub && sub.LinkTarget == null)
                {
                    DeleteTree(sub.FullName);
                }
                else
                {
                    entry.Delete();
                }
            }

            info.Delete();
        }

        private static void TryDeleteTree(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    DeleteTree(path);
                }
            }
            catch
            {
                // Nettoyage opportuniste.
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Nettoyage opportuniste.
            }
        }
    }
}
