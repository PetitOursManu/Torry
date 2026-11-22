using System;
using System.Linq;
using MediaBrowser.Model.Plugins;
using PetitOurs.Torry.Core;

namespace PetitOurs.Torry
{
    /// <summary>Configuration persistée par Emby (plugins/configurations/PetitOurs.Torry.xml).</summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        private static readonly TorryOptions Defaults = new TorryOptions();

        public string IncompleteDirectory { get; set; } = Defaults.IncompleteDirectory;

        public string CompletedDirectory { get; set; } = Defaults.CompletedDirectory;

        public int ListenPort { get; set; } = Defaults.ListenPort;

        public bool EnableDht { get; set; } = Defaults.EnableDht;

        public bool EnablePortForwarding { get; set; } = Defaults.EnablePortForwarding;

        public int MaxDownloadRateKiB { get; set; } = Defaults.MaxDownloadRateKiB;

        public int MaxUploadRateKiB { get; set; } = Defaults.MaxUploadRateKiB;

        public int MaxActiveDownloads { get; set; } = Defaults.MaxActiveDownloads;

        public int MaxConnections { get; set; } = Defaults.MaxConnections;

        public bool SeedAfterDownload { get; set; } = Defaults.SeedAfterDownload;

        public double SeedRatioLimit { get; set; } = Defaults.SeedRatioLimit;

        public int SeedTimeLimitMinutes { get; set; } = Defaults.SeedTimeLimitMinutes;

        public bool AutoStartAfterMetadata { get; set; } = Defaults.AutoStartAfterMetadata;

        public bool RefreshLibraryOnComplete { get; set; } = Defaults.RefreshLibraryOnComplete;

        public int TrashRetentionDays { get; set; } = Defaults.TrashRetentionDays;

        /// <summary>Dossiers de destination par type de contenu Emby.</summary>
        public DestinationFolder[] Destinations { get; set; } = Array.Empty<DestinationFolder>();

        public TorryOptions ToOptions()
        {
            return new TorryOptions
            {
                IncompleteDirectory = IncompleteDirectory,
                CompletedDirectory = CompletedDirectory,
                ListenPort = ListenPort,
                EnableDht = EnableDht,
                EnablePortForwarding = EnablePortForwarding,
                MaxDownloadRateKiB = MaxDownloadRateKiB,
                MaxUploadRateKiB = MaxUploadRateKiB,
                MaxActiveDownloads = MaxActiveDownloads,
                MaxConnections = MaxConnections,
                SeedAfterDownload = SeedAfterDownload,
                SeedRatioLimit = SeedRatioLimit,
                SeedTimeLimitMinutes = SeedTimeLimitMinutes,
                AutoStartAfterMetadata = AutoStartAfterMetadata,
                RefreshLibraryOnComplete = RefreshLibraryOnComplete,
                TrashRetentionDays = TrashRetentionDays,
                Destinations = (Destinations ?? Array.Empty<DestinationFolder>()).Where(d => d != null).Select(d => d.Clone()).ToList()
            };
        }

        public void CopyFrom(TorryOptions o)
        {
            IncompleteDirectory = o.IncompleteDirectory;
            CompletedDirectory = o.CompletedDirectory;
            ListenPort = o.ListenPort;
            EnableDht = o.EnableDht;
            EnablePortForwarding = o.EnablePortForwarding;
            MaxDownloadRateKiB = o.MaxDownloadRateKiB;
            MaxUploadRateKiB = o.MaxUploadRateKiB;
            MaxActiveDownloads = o.MaxActiveDownloads;
            MaxConnections = o.MaxConnections;
            SeedAfterDownload = o.SeedAfterDownload;
            SeedRatioLimit = o.SeedRatioLimit;
            SeedTimeLimitMinutes = o.SeedTimeLimitMinutes;
            AutoStartAfterMetadata = o.AutoStartAfterMetadata;
            RefreshLibraryOnComplete = o.RefreshLibraryOnComplete;
            TrashRetentionDays = o.TrashRetentionDays;
            Destinations = (o.Destinations ?? new System.Collections.Generic.List<DestinationFolder>()).Select(d => d.Clone()).ToArray();
        }
    }
}
