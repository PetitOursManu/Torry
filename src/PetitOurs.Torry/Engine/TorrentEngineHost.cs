using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using MonoTorrent.Client;
using PetitOurs.Torry.Core;

namespace PetitOurs.Torry.Engine
{
    /// <summary>
    /// Propriétaire du ClientEngine MonoTorrent. Traduit les réglages Torry en réglages moteur.
    /// Le moteur est 100 % managé (aucune dépendance native) : vérifié dans l'image emby/embyserver 4.10.2.
    /// </summary>
    internal sealed class TorrentEngineHost : IDisposable
    {
        private readonly string _cacheDirectory;
        private EngineSettings _current;

        public TorrentEngineHost(string cacheDirectory, TorryOptions options)
        {
            _cacheDirectory = cacheDirectory;
            _current = Build(null, cacheDirectory, options);
            Engine = new ClientEngine(_current);
            ListenPort = options.ListenPort;
        }

        public ClientEngine Engine { get; }

        public int ListenPort { get; private set; }

        public static EngineSettings Build(EngineSettings previous, string cacheDirectory, TorryOptions options)
        {
            var builder = previous == null ? new EngineSettingsBuilder() : new EngineSettingsBuilder(previous);
            builder.CacheDirectory = cacheDirectory;
            builder.AllowLocalPeerDiscovery = false;
            builder.AllowPortForwarding = options.EnablePortForwarding;
            builder.AutoSaveLoadDhtCache = true;
            builder.AutoSaveLoadFastResume = true;
            builder.AutoSaveLoadMagnetLinkMetadata = true;
            builder.FastResumeMode = FastResumeMode.BestEffort;

            // Les fichiers en cours sont isolés dans le dossier « en cours », hors bibliothèque.
            // Le suffixe .!mt de MonoTorrent est désactivé : dans MonoTorrent 3.0.2, MoveFileAsync
            // renommerait un fichier terminé avec ce suffixe (test « isComplete » toujours faux).
            builder.UsePartialFiles = false;

            builder.MaximumConnections = options.MaxConnections;
            builder.MaximumDownloadRate = checked(options.MaxDownloadRateKiB * 1024);
            builder.MaximumUploadRate = checked(options.MaxUploadRateKiB * 1024);

            // On ne recrée les points d'écoute que s'ils changent : MonoTorrent compare les IPEndPoint par référence
            // et redémarrerait le DHT à chaque mise à jour.
            var previousListen = previous != null && previous.ListenEndPoints.TryGetValue("ipv4", out var ep) ? ep : null;
            var listen = previousListen != null && previousListen.Port == options.ListenPort
                ? previousListen
                : new IPEndPoint(IPAddress.Any, options.ListenPort);
            builder.ListenEndPoints = new Dictionary<string, IPEndPoint> { { "ipv4", listen } };

            if (options.EnableDht)
            {
                var previousDht = previous?.DhtEndPoint;
                builder.DhtEndPoint = previousDht != null && previousDht.Port == options.ListenPort
                    ? previousDht
                    : new IPEndPoint(IPAddress.Any, options.ListenPort);
            }
            else
            {
                builder.DhtEndPoint = null;
            }

            return builder.ToSettings();
        }

        public static TorrentSettings BuildTorrentSettings(TorryOptions options)
        {
            return new TorrentSettingsBuilder
            {
                // Les fichiers sont posés directement dans <en cours>/<id>/ : la structure interne du torrent est conservée.
                CreateContainingDirectory = false,
                AllowDht = options.EnableDht,
                AllowPeerExchange = true
            }.ToSettings();
        }

        public async Task ApplyAsync(TorryOptions options)
        {
            var next = Build(_current, _cacheDirectory, options);
            await Engine.UpdateSettingsAsync(next).ConfigureAwait(false);
            _current = next;
            ListenPort = options.ListenPort;
        }

        public void Dispose()
        {
            Engine.Dispose();
        }
    }
}
