using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PetitOurs.Torry.Files;

namespace PetitOurs.Torry.Core
{
    /// <summary>Réglages utilisés par le moteur (indépendants d'Emby).</summary>
    public sealed class TorryOptions
    {
        public string IncompleteDirectory { get; set; } = "/downloads/incomplete";

        public string CompletedDirectory { get; set; } = "/downloads/complete";

        /// <summary>Port d'écoute BitTorrent (TCP pour les pairs, UDP pour le DHT).</summary>
        public int ListenPort { get; set; } = 6881;

        public bool EnableDht { get; set; } = true;

        /// <summary>UPnP / NAT-PMP. Inutile en réseau Docker « bridge » : désactivé par défaut.</summary>
        public bool EnablePortForwarding { get; set; }

        /// <summary>0 = illimité.</summary>
        public int MaxDownloadRateKiB { get; set; }

        /// <summary>0 = illimité.</summary>
        public int MaxUploadRateKiB { get; set; }

        public int MaxActiveDownloads { get; set; } = 3;

        public int MaxConnections { get; set; } = 150;

        public bool SeedAfterDownload { get; set; } = true;

        /// <summary>Arrêt du partage quand envoyé / taille atteint ce ratio. 0 = pas de limite de ratio.</summary>
        public double SeedRatioLimit { get; set; } = 1.0;

        /// <summary>Arrêt du partage après cette durée cumulée. 0 = pas de limite de durée.</summary>
        public int SeedTimeLimitMinutes { get; set; } = 1440;

        /// <summary>Démarre automatiquement avec tous les fichiers sélectionnés dès que les métadonnées sont connues.</summary>
        public bool AutoStartAfterMetadata { get; set; }

        public bool RefreshLibraryOnComplete { get; set; } = true;

        /// <summary>Durée de conservation des éléments mis à la corbeille (jours). 0 = jamais vidée automatiquement.</summary>
        public int TrashRetentionDays { get; set; } = 30;

        /// <summary>Dossiers de destination par type de contenu (bibliothèques Emby). Vide = tout va dans « terminés ».</summary>
        public List<DestinationFolder> Destinations { get; set; } = new List<DestinationFolder>();

        public const int MaxDestinations = 30;

        public TorryOptions Clone()
        {
            var copy = (TorryOptions)MemberwiseClone();
            copy.Destinations = (Destinations ?? new List<DestinationFolder>()).Select(d => d.Clone()).ToList();
            return copy;
        }

        public DestinationFolder FindDestination(string id) =>
            string.IsNullOrEmpty(id) ? null : (Destinations ?? new List<DestinationFolder>()).FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));

        public List<string> Validate()
        {
            var errors = new List<string>(PathGuard.ValidateFolderConfiguration(IncompleteDirectory, CompletedDirectory));
            if (ListenPort < 1024 || ListenPort > 65535)
            {
                errors.Add("Le port BitTorrent doit être compris entre 1024 et 65535.");
            }

            if (MaxDownloadRateKiB < 0 || MaxUploadRateKiB < 0)
            {
                errors.Add("Les limites de débit ne peuvent pas être négatives (0 = illimité).");
            }

            if (MaxDownloadRateKiB > 2_000_000 || MaxUploadRateKiB > 2_000_000)
            {
                errors.Add("Limite de débit trop élevée (2 000 000 Kio/s maximum).");
            }

            if (MaxActiveDownloads < 1 || MaxActiveDownloads > 20)
            {
                errors.Add("Le nombre de téléchargements simultanés doit être compris entre 1 et 20.");
            }

            if (MaxConnections < 10 || MaxConnections > 1000)
            {
                errors.Add("Le nombre maximal de connexions doit être compris entre 10 et 1000.");
            }

            if (SeedRatioLimit < 0 || SeedRatioLimit > 100)
            {
                errors.Add("Le ratio de partage doit être compris entre 0 et 100 (0 = sans limite).");
            }

            if (SeedTimeLimitMinutes < 0 || SeedTimeLimitMinutes > 525600)
            {
                errors.Add("La durée de partage doit être comprise entre 0 et 525 600 minutes (0 = sans limite).");
            }

            if (TrashRetentionDays < 0 || TrashRetentionDays > 3650)
            {
                errors.Add("La durée de conservation de la corbeille doit être comprise entre 0 et 3650 jours (0 = jamais vidée automatiquement).");
            }

            errors.AddRange(ValidateDestinations());
            return errors;
        }
            private IEnumerable<string> ValidateDestinations()
        {
            var list = Destinations ?? new List<DestinationFolder>();
            if (list.Count > MaxDestinations)
            {
                yield return "Trop de dossiers de destination (" + MaxDestinations + " maximum).";
                yield break;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in list)
            {
                var label = string.IsNullOrWhiteSpace(d?.Name) ? "(sans nom)" : d.Name.Trim();
                if (d == null || string.IsNullOrWhiteSpace(d.Name))
                {
                    yield return "Chaque dossier de destination doit avoir un nom.";
                    continue;
                }

                if (d.Name.Trim().Length > 60)
                {
                    yield return "Destination « " + label + " » : nom trop long (60 caractères maximum).";
                }

                if (!names.Add(d.Name.Trim()))
                {
                    yield return "Deux dossiers de destination portent le nom « " + label + " ».";
                }

                if (string.IsNullOrEmpty(d.Id) || d.Id.Length > 32 || !d.Id.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) || !ids.Add(d.Id))
                {
                    yield return "Destination « " + label + " » : identifiant invalide.";
                }

                if (!MediaTypes.IsKnownContentType(d.ContentType))
                {
                    yield return "Destination « " + label + " » : type de contenu inconnu.";
                }

                if (string.IsNullOrWhiteSpace(d.Path) || !System.IO.Path.IsPathFullyQualified(d.Path))
                {
                    yield return "Destination « " + label + " » : le chemin doit être absolu.";
                    continue;
                }

                var path = PathGuard.NormalizeDirectory(d.Path);
                if (System.IO.Path.GetPathRoot(path) == path)
                {
                    yield return "Destination « " + label + " » : la racine du système de fichiers est interdite.";
                }

                if (!string.IsNullOrWhiteSpace(IncompleteDirectory) && System.IO.Path.IsPathFullyQualified(IncompleteDirectory)
                    && (PathGuard.IsSameOrInside(path, IncompleteDirectory) || PathGuard.IsSameOrInside(IncompleteDirectory, path)))
                {
                    yield return "Destination « " + label + " » : elle ne doit ni contenir le dossier « en cours », ni s'y trouver.";
                }
            }
        }
    }
}
