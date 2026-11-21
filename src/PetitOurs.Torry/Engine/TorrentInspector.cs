using System;
using System.Collections.Generic;
using System.Linq;
using MonoTorrent;
using PetitOurs.Torry.Core;
using PetitOurs.Torry.Files;
using PetitOurs.Torry.Persistence;

namespace PetitOurs.Torry.Engine
{
    /// <summary>Lecture et validation des métadonnées d'un torrent avant tout accès disque.</summary>
    public static class TorrentInspector
    {
        public const int MaxTorrentFileBytes = 10 * 1024 * 1024;
        public const int MaxFileCount = 20000;

        private static readonly HashSet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts", ".webm", ".flv", ".ogv", ".3gp", ".vob", ".iso"
        };

        public static bool IsVideo(string path) => VideoExtensions.Contains(System.IO.Path.GetExtension(path ?? string.Empty));

        /// <summary>Charge et valide un torrent. Lève <see cref="TorryUserException"/> si le contenu est invalide ou dangereux.</summary>
        public static Torrent LoadAndValidate(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
            {
                throw new TorryUserException("Fichier .torrent vide.");
            }

            if (data.Length > MaxTorrentFileBytes)
            {
                throw new TorryUserException("Fichier .torrent trop volumineux (10 Mio maximum).");
            }

            Torrent torrent;
            try
            {
                torrent = Torrent.Load(data);
            }
            catch (ArgumentException ex) when (ex.Message.IndexOf("path", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // PathValidator de MonoTorrent : chemin absolu ou remontée de dossier.
                throw new TorryUserException("Torrent refusé : il contient un chemin de fichier dangereux.");
            }
            catch (Exception)
            {
                throw new TorryUserException("Fichier .torrent invalide ou illisible.");
            }

            Validate(torrent);
            return torrent;
        }

        public static void Validate(Torrent torrent)
        {
            if (torrent.Files == null || torrent.Files.Count == 0)
            {
                throw new TorryUserException("Le torrent ne contient aucun fichier.");
            }

            if (torrent.Files.Count > MaxFileCount)
            {
                throw new TorryUserException("Le torrent contient trop de fichiers (" + torrent.Files.Count + ").");
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in torrent.Files)
            {
                if (!PathGuard.TryValidateRelativePath(file.Path, out var reason))
                {
                    throw new TorryUserException("Torrent refusé : chemin de fichier dangereux (" + reason + ").");
                }

                if (file.Length < 0)
                {
                    throw new TorryUserException("Torrent refusé : taille de fichier invalide.");
                }

                if (!seen.Add(file.Path))
                {
                    throw new TorryUserException("Torrent refusé : deux fichiers portent le même chemin.");
                }
            }
        }

        public static List<TorrentFileEntry> BuildEntries(Torrent torrent)
        {
            return torrent.Files.Select((f, i) => new TorrentFileEntry
            {
                Index = i,
                Path = f.Path,
                Length = f.Length,
                Selected = true
            }).ToList();
        }

        public static string GetInfoHashHex(InfoHashes hashes)
        {
            var hash = hashes.V1OrV2;
            return hash.ToHex().ToLowerInvariant();
        }
    }

    /// <summary>Erreur destinée à l'utilisateur (message en français, sans donnée sensible).</summary>
    public sealed class TorryUserException : Exception
    {
        public TorryUserException(string message)
            : base(message)
        {
        }
    }
}
