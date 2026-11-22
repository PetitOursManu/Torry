using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PetitOurs.Torry.Core
{
    /// <summary>Catégorie d'un fichier, d'après son extension.</summary>
    public enum FileCategory
    {
        Other = 0,
        Video = 1,
        Audio = 2,
        Image = 3,
        Book = 4,
        Subtitle = 5
    }

    /// <summary>
    /// Types de contenu des bibliothèques Emby (valeurs « CollectionType » d'Emby) et formats pris en charge.
    /// </summary>
    public static class MediaTypes
    {
        public const string Movies = "movies";
        public const string TvShows = "tvshows";
        public const string Music = "music";
        public const string MusicVideos = "musicvideos";
        public const string HomeVideos = "homevideos";
        public const string Photos = "photos";
        public const string Books = "books";
        public const string AudioBooks = "audiobooks";
        public const string Mixed = "mixed";

        /// <summary>Types de contenu proposés, dans l'ordre d'affichage, avec leur libellé.</summary>
        public static readonly IReadOnlyList<KeyValuePair<string, string>> ContentTypes = new[]
        {
            new KeyValuePair<string, string>(Movies, "Films"),
            new KeyValuePair<string, string>(TvShows, "Séries"),
            new KeyValuePair<string, string>(Music, "Musique"),
            new KeyValuePair<string, string>(MusicVideos, "Clips musicaux"),
            new KeyValuePair<string, string>(HomeVideos, "Vidéos personnelles et photos"),
            new KeyValuePair<string, string>(Photos, "Photos"),
            new KeyValuePair<string, string>(Books, "Livres"),
            new KeyValuePair<string, string>(AudioBooks, "Livres audio"),
            new KeyValuePair<string, string>(Mixed, "Contenu mixte")
        };

        private static readonly HashSet<string> VideoExtensions = Set(
            ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".webm", ".flv",
            ".ogv", ".3gp", ".vob", ".iso", ".divx", ".xvid", ".rmvb", ".asf", ".f4v");

        private static readonly HashSet<string> AudioExtensions = Set(
            ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wav", ".wma", ".alac", ".ape", ".aiff", ".aif",
            ".dsf", ".dff", ".m4b", ".mka", ".wv");

        private static readonly HashSet<string> ImageExtensions = Set(
            ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".heic", ".heif");

        private static readonly HashSet<string> BookExtensions = Set(
            ".epub", ".pdf", ".mobi", ".azw", ".azw3", ".cbz", ".cbr", ".cb7", ".cbt", ".fb2");

        private static readonly HashSet<string> SubtitleExtensions = Set(
            ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup", ".smi");

        private static readonly Regex EpisodePattern = new Regex(
            @"(\bS\d{1,2}[ ._-]?E\d{1,3}\b)|(\b\d{1,2}x\d{2,3}\b)|(\b(Saison|Season|Staffel|Temporada)[ ._-]?\d{1,2}\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static FileCategory GetCategory(string path)
        {
            var extension = Path.GetExtension(path ?? string.Empty);
            if (VideoExtensions.Contains(extension))
            {
                return FileCategory.Video;
            }

            if (AudioExtensions.Contains(extension))
            {
                return FileCategory.Audio;
            }

            if (ImageExtensions.Contains(extension))
            {
                return FileCategory.Image;
            }

            if (BookExtensions.Contains(extension))
            {
                return FileCategory.Book;
            }

            return SubtitleExtensions.Contains(extension) ? FileCategory.Subtitle : FileCategory.Other;
        }

        public static bool IsKnownContentType(string contentType) =>
            ContentTypes.Any(t => string.Equals(t.Key, contentType, StringComparison.Ordinal));

        public static string Label(string contentType) =>
            ContentTypes.FirstOrDefault(t => string.Equals(t.Key, contentType, StringComparison.Ordinal)).Value ?? "Contenu mixte";

        /// <summary>Types de bibliothèque Emby capables d'afficher cette catégorie de fichier.</summary>
        public static IReadOnlyList<string> CompatibleContentTypes(FileCategory category)
        {
            switch (category)
            {
                case FileCategory.Video:
                case FileCategory.Subtitle:
                    return new[] { Movies, TvShows, MusicVideos, HomeVideos, Mixed };
                case FileCategory.Audio:
                    return new[] { Music, AudioBooks, Mixed };
                case FileCategory.Image:
                    return new[] { Photos, HomeVideos, Mixed };
                case FileCategory.Book:
                    return new[] { Books, Mixed };
                default:
                    return new[] { Mixed };
            }
        }

        /// <summary>
        /// Type de contenu le plus probable pour un ensemble de fichiers (pondéré par la taille) :
        /// vidéos avec SxxEyy / « Saison » → séries ; autres vidéos → films ; .m4b → livres audio ; etc.
        /// </summary>
        public static string SuggestContentType(IEnumerable<KeyValuePair<string, long>> files)
        {
            var list = files.Where(f => !string.IsNullOrEmpty(f.Key)).ToList();
            if (list.Count == 0)
            {
                return null;
            }

            var dominant = list
                .GroupBy(f => GetCategory(f.Key))
                .Where(g => g.Key != FileCategory.Other && g.Key != FileCategory.Subtitle)
                .OrderByDescending(g => g.Sum(f => Math.Max(1, f.Value)))
                .Select(g => (FileCategory?)g.Key)
                .FirstOrDefault();

            switch (dominant)
            {
                case FileCategory.Video:
                    var videos = list.Where(f => GetCategory(f.Key) == FileCategory.Video).ToList();
                    return videos.Count(f => EpisodePattern.IsMatch(f.Key)) * 2 >= videos.Count || videos.Count >= 3 && videos.Any(f => EpisodePattern.IsMatch(f.Key))
                        ? TvShows
                        : Movies;
                case FileCategory.Audio:
                    return list.Any(f => string.Equals(Path.GetExtension(f.Key), ".m4b", StringComparison.OrdinalIgnoreCase)) ? AudioBooks : Music;
                case FileCategory.Image:
                    return Photos;
                case FileCategory.Book:
                    return Books;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Destination conseillée : d'abord une destination du type suggéré, sinon une destination compatible
        /// avec la catégorie dominante, sinon aucune (dossier des téléchargements terminés).
        /// </summary>
        public static DestinationFolder SuggestDestination(IEnumerable<DestinationFolder> destinations, IEnumerable<KeyValuePair<string, long>> files)
        {
            var list = (destinations ?? Enumerable.Empty<DestinationFolder>()).ToList();
            var fileList = files.ToList();
            var suggested = SuggestContentType(fileList);
            if (suggested == null || list.Count == 0)
            {
                return null;
            }

            var exact = list.FirstOrDefault(d => d.ContentType == suggested);
            if (exact != null)
            {
                return exact;
            }

            var category = fileList.Select(f => GetCategory(f.Key)).FirstOrDefault(c => c != FileCategory.Other && c != FileCategory.Subtitle);
            var compatible = CompatibleContentTypes(category);
            return list.FirstOrDefault(d => compatible.Contains(d.ContentType) && d.ContentType != Mixed)
                ?? list.FirstOrDefault(d => d.ContentType == Mixed);
        }

        private static HashSet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Dossier de destination (généralement l'emplacement d'une bibliothèque Emby).</summary>
    public sealed class DestinationFolder
    {
        /// <summary>Identifiant court et stable (utilisé par l'interface et les tâches).</summary>
        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>Type de contenu Emby : movies, tvshows, music…</summary>
        public string ContentType { get; set; }

        public string Path { get; set; }

        public DestinationFolder Clone() => (DestinationFolder)MemberwiseClone();
    }
}
