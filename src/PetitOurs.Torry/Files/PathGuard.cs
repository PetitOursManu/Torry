using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PetitOurs.Torry.Files
{
    /// <summary>
    /// Règles de sécurité sur les chemins : aucun fichier ne doit sortir des dossiers autorisés,
    /// y compris à travers les noms contenus dans un torrent.
    /// </summary>
    public static class PathGuard
    {
        private static readonly char[] Separators = { '/', '\\' };

        // Noms réservés Windows : refusés partout pour que les dossiers restent portables.
        private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        public const int MaxFolderNameLength = 120;

        private static StringComparison PathComparison =>
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>Chemin absolu normalisé, sans séparateur final.</summary>
        public static string NormalizeDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Chemin vide.");
            }

            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (full.Length > root.Length)
            {
                full = full.TrimEnd(Separators);
            }

            return full;
        }

        /// <summary>Vrai si <paramref name="candidate"/> est strictement à l'intérieur de <paramref name="root"/>.</summary>
        public static bool IsInside(string root, string candidate)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            var normalizedRoot = NormalizeDirectory(root);
            var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Separators);
            var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;

            return normalizedCandidate.StartsWith(prefix, PathComparison) && normalizedCandidate.Length > prefix.Length;
        }

        public static bool IsSameOrInside(string root, string candidate)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            return string.Equals(NormalizeDirectory(root), NormalizeDirectory(candidate), PathComparison) || IsInside(root, candidate);
        }

        /// <summary>
        /// Valide un chemin relatif provenant d'un torrent (MonoTorrent assemble les composants avec le séparateur
        /// du système). Refuse : chemin vide, absolu, lecteur Windows, composants "." / "..", caractères de contrôle,
        /// noms réservés, composants vides.
        /// </summary>
        public static bool TryValidateRelativePath(string relativePath, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(relativePath))
            {
                reason = "chemin vide";
                return false;
            }

            if (relativePath.IndexOf('\0') >= 0)
            {
                reason = "caractère nul";
                return false;
            }

            if (Path.IsPathRooted(relativePath) || relativePath.StartsWith("/", StringComparison.Ordinal) || relativePath.StartsWith("\\", StringComparison.Ordinal)
                || (relativePath.Length >= 2 && relativePath[1] == ':'))
            {
                reason = "chemin absolu";
                return false;
            }

            var components = relativePath.Split(Separators);
            foreach (var component in components)
            {
                if (component.Length == 0)
                {
                    reason = "composant vide";
                    return false;
                }

                if (component == "." || component == "..")
                {
                    reason = "remontée de dossier (..)";
                    return false;
                }

                if (component.Trim().Length == 0 || component.Trim('.', ' ').Length == 0)
                {
                    reason = "nom composé uniquement de points ou d'espaces";
                    return false;
                }

                if (component.Any(char.IsControl))
                {
                    reason = "caractère de contrôle";
                    return false;
                }

                if (OperatingSystem.IsWindows())
                {
                    // Sous Windows, « : » désigne un lecteur ou un flux alternatif et certains noms sont réservés.
                    if (component.IndexOf(':') >= 0 || ReservedNames.Contains(component.Split('.')[0]))
                    {
                        reason = "nom interdit sous Windows";
                        return false;
                    }
                }

                if (Encoding.UTF8.GetByteCount(component) > 255)
                {
                    reason = "nom de fichier trop long";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Combine un chemin relatif validé avec une racine et vérifie que le résultat reste dans la racine.
        /// </summary>
        public static string CombineInside(string root, string relativePath)
        {
            if (!TryValidateRelativePath(relativePath, out var reason))
            {
                throw new UnsafePathException("Chemin refusé (" + reason + ").");
            }

            var normalized = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var combined = Path.GetFullPath(Path.Combine(NormalizeDirectory(root), normalized));
            if (!IsInside(root, combined))
            {
                throw new UnsafePathException("Chemin refusé (sortie du dossier autorisé).");
            }

            return combined;
        }

        /// <summary>Nom de dossier sûr dérivé du nom d'un torrent.</summary>
        public static string SanitizeFolderName(string name, string fallback)
        {
            var source = string.IsNullOrWhiteSpace(name) ? fallback : name;
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
            var builder = new StringBuilder(source.Length);
            foreach (var c in source)
            {
                builder.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
            }

            var result = builder.ToString().Trim().Trim('.', ' ');
            if (result.Length > MaxFolderNameLength)
            {
                result = result.Substring(0, MaxFolderNameLength).Trim().Trim('.', ' ');
            }

            if (result.Length == 0 || ReservedNames.Contains(result.Split('.')[0]))
            {
                result = SanitizeFolderName(fallback, "torrent");
            }

            return result;
        }

        /// <summary>
        /// Renvoie un chemin de dossier qui n'existe pas encore : "Nom", puis "Nom (2)", "Nom (3)"…
        /// Ne réutilise jamais un dossier existant afin de ne rien écraser.
        /// </summary>
        public static string GetUniqueDirectory(string parent, string folderName)
        {
            for (var i = 1; i < 10000; i++)
            {
                var candidateName = i == 1 ? folderName : folderName + " (" + i + ")";
                var candidate = Path.Combine(parent, candidateName);
                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                {
                    if (!IsInside(parent, candidate))
                    {
                        throw new UnsafePathException("Nom de dossier refusé.");
                    }

                    return candidate;
                }
            }

            throw new IOException("Impossible de trouver un nom de dossier libre pour « " + folderName + " ».");
        }

        /// <summary>Renvoie un chemin de fichier libre : "film.mkv", puis "film (2).mkv"…</summary>
        public static string GetUniqueFilePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return path;
            }

            var directory = Path.GetDirectoryName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            for (var i = 2; i < 10000; i++)
            {
                var candidate = Path.Combine(directory, stem + " (" + i + ")" + extension);
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new IOException("Impossible de trouver un nom de fichier libre.");
        }

        /// <summary>
        /// Contrôle la configuration des dossiers : chemins absolus, distincts, et le dossier temporaire ne doit
        /// jamais se trouver dans le dossier des téléchargements terminés (sinon les fichiers incomplets
        /// apparaîtraient dans la bibliothèque).
        /// </summary>
        public static IReadOnlyList<string> ValidateFolderConfiguration(string incomplete, string completed)
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(incomplete) || !Path.IsPathFullyQualified(incomplete))
            {
                errors.Add("Le dossier des téléchargements en cours doit être un chemin absolu.");
            }

            if (string.IsNullOrWhiteSpace(completed) || !Path.IsPathFullyQualified(completed))
            {
                errors.Add("Le dossier des téléchargements terminés doit être un chemin absolu.");
            }

            if (errors.Count > 0)
            {
                return errors;
            }

            var a = NormalizeDirectory(incomplete);
            var b = NormalizeDirectory(completed);
            if (string.Equals(a, b, PathComparison))
            {
                errors.Add("Les dossiers « en cours » et « terminés » doivent être différents.");
            }
            else if (IsInside(b, a))
            {
                errors.Add("Le dossier « en cours » ne doit pas être placé dans le dossier « terminés » (les fichiers incomplets seraient visibles dans la bibliothèque).");
            }
            else if (IsInside(a, b))
            {
                errors.Add("Le dossier « terminés » ne doit pas être placé dans le dossier « en cours ».");
            }

            if (Path.GetPathRoot(a) == a || Path.GetPathRoot(b) == b)
            {
                errors.Add("La racine du système de fichiers ne peut pas être utilisée comme dossier de téléchargement.");
            }

            return errors;
        }
    }

    public sealed class UnsafePathException : Exception
    {
        public UnsafePathException(string message)
            : base(message)
        {
        }
    }
}
