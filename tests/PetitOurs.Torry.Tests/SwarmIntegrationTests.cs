using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;
using PetitOurs.Torry.Core;
using PetitOurs.Torry.Engine;
using PetitOurs.Torry.Files;
using PetitOurs.Torry.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace PetitOurs.Torry.Tests
{
    /// <summary>
    /// Essaim BitTorrent local réel : un moteur MonoTorrent « source » partage des fichiers sur 127.0.0.1,
    /// le gestionnaire Torry les télécharge, les finalise, partage, redémarre et supprime.
    /// Aucun accès Internet, aucun tracker.
    /// </summary>
    public sealed class SwarmIntegrationTests : IAsyncLifetime
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

        private readonly ITestOutputHelper _output;
        private string _root;
        private string _seedRoot;
        private string _incomplete;
        private string _completed;
        private string _state;
        private ClientEngine _seeder;
        private int _seederPort;
        private byte[] _torrentBytes;
        private RecordingNotifier _notifier;
        private ListLog _log;

        public SwarmIntegrationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        public async Task InitializeAsync()
        {
            _root = TestSupport.NewTempDir("torry-swarm");
            _seedRoot = Path.Combine(_root, "seed");
            _incomplete = Path.Combine(_root, "downloads", "incomplete");
            _completed = Path.Combine(_root, "downloads", "complete");
            _state = Path.Combine(_root, "config", "torry");

            // Contenu : un film, un bonus dans un sous-dossier, un fichier texte non sélectionné.
            var content = Path.Combine(_seedRoot, "Mon Film (2024)");
            TestSupport.WriteRandomFile(Path.Combine(content, "Mon Film (2024).mkv"), 3 * 1024 * 1024 + 123, 11);
            TestSupport.WriteRandomFile(Path.Combine(content, "Extras", "Bonus.mkv"), 700 * 1024 + 7, 12);
            TestSupport.WriteRandomFile(Path.Combine(content, "notes.txt"), 50 * 1024 + 3, 13);
            _torrentBytes = await TestSupport.CreateTorrentAsync(content, 32 * 1024);

            _seederPort = TestSupport.FreeTcpPort();
            var settings = new EngineSettingsBuilder
            {
                CacheDirectory = Path.Combine(_root, "seeder-cache"),
                AllowPortForwarding = false,
                AllowLocalPeerDiscovery = false,
                DhtEndPoint = null,
                ListenEndPoints = new Dictionary<string, IPEndPoint> { { "ipv4", new IPEndPoint(IPAddress.Loopback, _seederPort) } }
            }.ToSettings();
            _seeder = new ClientEngine(settings);
            var manager = await _seeder.AddAsync(Torrent.Load(_torrentBytes), _seedRoot);
            await manager.HashCheckAsync(autoStart: true);
            await TestSupport.WaitUntilAsync(() => manager.State == TorrentState.Seeding, Timeout, "source prête");

            _notifier = new RecordingNotifier();
            _log = new ListLog();
        }

        public async Task DisposeAsync()
        {
            try
            {
                await _seeder.StopAllAsync();
                _seeder.Dispose();
            }
            catch
            {
            }

            foreach (var line in _log.Lines)
            {
                _output.WriteLine(line);
            }

            TestSupport.TryDelete(_root);
        }

        private TorrentTaskManager NewManager(TorryOptions options)
        {
            return new TorrentTaskManager(new TaskStore(_state), options, _log, _notifier, TimeSpan.FromMilliseconds(200));
        }

        private TorryOptions Options(bool seed, int downKiB = 0)
        {
            return new TorryOptions
            {
                IncompleteDirectory = _incomplete,
                CompletedDirectory = _completed,
                ListenPort = TestSupport.FreeTcpPort(),
                EnableDht = false,
                EnablePortForwarding = false,
                MaxActiveDownloads = 2,
                MaxDownloadRateKiB = downKiB,
                SeedAfterDownload = seed,
                SeedRatioLimit = 0,
                SeedTimeLimitMinutes = 0
            };
        }

        private async Task ConnectToSeederAsync(TorrentTaskManager manager, string id)
        {
            await TestSupport.WaitUntilAsync(() => manager.GetManagerForTests(id) != null, Timeout, "ajout au moteur");
            await manager.GetManagerForTests(id).AddPeerAsync(new PeerInfo(new Uri("ipv4://127.0.0.1:" + _seederPort)));
        }

        private TaskView View(TorrentTaskManager manager, string id) => manager.GetStatus().Tasks.Single(t => t.Id == id);

        private string Source(params string[] parts) => Path.Combine(new[] { _seedRoot, "Mon Film (2024)" }.Concat(parts).ToArray());

        [Fact]
        public async Task Downloads_selected_files_finalizes_without_overwrite_and_resumes_seeding_after_restart()
        {
            // Un dossier du même nom existe déjà dans « terminés » : il ne doit pas être touché.
            var existing = Path.Combine(_completed, "Mon Film (2024)");
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "Mon Film (2024).mkv"), "fichier préexistant");

            var options = Options(seed: true);
            var manager = NewManager(options);
            await manager.StartAsync();
            Assert.False(manager.GetStatus().Engine.Listening, "aucun torrent actif : MonoTorrent n'écoute pas encore");

            var task = await manager.AddTorrentFileAsync(_torrentBytes);
            Assert.True(task.NeedsSelection);
            Assert.Equal("AwaitingSelection", task.State);
            Assert.False(Directory.Exists(Path.Combine(_incomplete, task.Id)), "aucune donnée avant la sélection");

            var files = (await manager.GetFilesAsync(task.Id)).Files;
            Assert.Equal(3, files.Count);
            var selected = files.Where(f => f.IsVideo).Select(f => f.Index).ToList();
            Assert.Equal(2, selected.Count);

            await manager.StartWithSelectionAsync(task.Id, selected);
            await ConnectToSeederAsync(manager, task.Id);

            await TestSupport.WaitUntilAsync(() => View(manager, task.Id).State == "Seeding", Timeout, "téléchargement + finalisation");
            await manager.TickForTestsAsync();
            Assert.True(manager.GetStatus().Engine.Listening, "torrent actif : le port doit être en écoute");
            using (var client = new System.Net.Sockets.TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, options.ListenPort);
                Assert.True(client.Connected);
            }

            var finalDir = Path.Combine(_completed, "Mon Film (2024) (2)");
            var movie = Path.Combine(finalDir, "Mon Film (2024).mkv");
            var bonus = Path.Combine(finalDir, "Extras", "Bonus.mkv");
            Assert.Equal("Mon Film (2024) (2)", View(manager, task.Id).FinalFolder);
            Assert.Equal(TestSupport.Sha256(Source("Mon Film (2024).mkv")), TestSupport.Sha256(movie));
            Assert.Equal(TestSupport.Sha256(Source("Extras", "Bonus.mkv")), TestSupport.Sha256(bonus));
            Assert.False(File.Exists(Path.Combine(finalDir, "notes.txt")), "fichier non sélectionné absent de la bibliothèque");
            Assert.Equal("fichier préexistant", File.ReadAllText(Path.Combine(existing, "Mon Film (2024).mkv")));

            // Aucun doublon : les fichiers sélectionnés ne sont plus dans « en cours ».
            var leftovers = Directory.Exists(_incomplete) ? Directory.GetFiles(_incomplete, "*.mkv", SearchOption.AllDirectories) : Array.Empty<string>();
            Assert.Empty(leftovers);
            Assert.Contains(finalDir, _notifier.Notified);

            // Redémarrage d'Emby : arrêt propre puis nouvelle instance sur le même état.
            await manager.StopAsync();
            var restarted = NewManager(options);
            await restarted.StartAsync();
            await TestSupport.WaitUntilAsync(() => View(restarted, task.Id).State == "Seeding", Timeout, "reprise du partage");
            var engineManager = restarted.GetManagerForTests(task.Id);
            Assert.NotNull(engineManager);
            await TestSupport.WaitUntilAsync(() => engineManager.State == TorrentState.Seeding, Timeout, "moteur en partage");
            Assert.Equal(movie, engineManager.Files.Single(f => f.Path.EndsWith("Mon Film (2024).mkv", StringComparison.Ordinal)).FullPath);
            Assert.Empty(Directory.GetFiles(_incomplete, "*.mkv", SearchOption.AllDirectories));

            // Arrêt du partage puis suppression de la tâche en conservant les fichiers (comportement par défaut).
            await restarted.CancelAsync(task.Id);
            Assert.Equal("Completed", View(restarted, task.Id).State);
            await Assert.ThrowsAsync<TorryUserException>(() => restarted.RemoveAsync(task.Id, deleteFiles: true, confirmation: "oui"));
            await restarted.RemoveAsync(task.Id, deleteFiles: false, confirmation: null);
            Assert.True(File.Exists(movie));
            Assert.Empty(restarted.GetStatus().Tasks);
            await restarted.StopAsync();

            var third = NewManager(options);
            await third.StartAsync();
            Assert.Empty(third.GetStatus().Tasks);
            await third.StopAsync();
        }

        [Fact]
        public async Task Interrupted_download_resumes_after_restart_then_delete_with_confirmation_removes_files()
        {
            var options = Options(seed: false, downKiB: 400);
            var manager = NewManager(options);
            await manager.StartAsync();
            var task = await manager.AddTorrentFileAsync(_torrentBytes);
            await manager.StartWithSelectionAsync(task.Id, new[] { 0, 1, 2 });
            await ConnectToSeederAsync(manager, task.Id);

            await TestSupport.WaitUntilAsync(() => View(manager, task.Id).Progress > 5, Timeout, "début du téléchargement");
            Assert.True(View(manager, task.Id).Progress < 100);

            // Les fichiers incomplets ne sont jamais dans le dossier « terminés ».
            Assert.Empty(Directory.Exists(_completed) ? Directory.GetFiles(_completed, "*", SearchOption.AllDirectories) : Array.Empty<string>());

            await manager.StopAsync();

            options.MaxDownloadRateKiB = 0;
            var restarted = NewManager(options);
            await restarted.StartAsync();
            await ConnectToSeederAsync(restarted, task.Id);
            await TestSupport.WaitUntilAsync(() => View(restarted, task.Id).State == "Completed", Timeout, "reprise et fin du téléchargement");

            var finalDir = Path.Combine(_completed, "Mon Film (2024)");
            Assert.Equal(TestSupport.Sha256(Source("Mon Film (2024).mkv")), TestSupport.Sha256(Path.Combine(finalDir, "Mon Film (2024).mkv")));
            Assert.True(File.Exists(Path.Combine(finalDir, "notes.txt")));
            Assert.Null(restarted.GetManagerForTests(task.Id));

            var result = await restarted.RemoveAsync(task.Id, deleteFiles: true, confirmation: task.Id);
            Assert.Equal(3, result.DeletedFiles);
            Assert.False(Directory.Exists(finalDir));
            Assert.False(Directory.Exists(Path.Combine(_incomplete, task.Id)));
            await restarted.StopAsync();

            // Jamais de lien magnet ni d'URL dans le journal.
            Assert.DoesNotContain(_log.Lines, l => l.Contains("magnet:", StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Magnet_metadata_is_fetched_then_nothing_is_downloaded_before_selection(bool incoming)
        {
            var options = Options(seed: false);
            var manager = NewManager(options);
            await manager.StartAsync();
            var torrent = Torrent.Load(_torrentBytes);
            var task = await manager.AddMagnetAsync(new MagnetLink(torrent.InfoHashes, "nom provisoire").ToV1String());
            Assert.Equal("FetchingMetadata", task.State);
            await TestSupport.WaitUntilAsync(() => manager.GetManagerForTests(task.Id) != null, Timeout, "gestionnaire de métadonnées");

            if (incoming)
            {
                // Le pair se connecte AU port de Torry (cas d'un serveur dont le port est ouvert).
                var seederManager = _seeder.Torrents.Single();
                await seederManager.AddPeerAsync(new PeerInfo(new Uri("ipv4://127.0.0.1:" + options.ListenPort)));
            }
            else
            {
                // Torry se connecte au pair (cas d'un pair trouvé via DHT / tracker).
                await manager.GetManagerForTests(task.Id).AddPeerAsync(new PeerInfo(new Uri("ipv4://127.0.0.1:" + _seederPort)));
            }

            await TestSupport.WaitUntilAsync(() => View(manager, task.Id).State == "AwaitingSelection", Timeout, "métadonnées du magnet");
            var view = View(manager, task.Id);
            Assert.Equal("Mon Film (2024)", view.Name);
            Assert.Equal(3, view.FileCount);
            Assert.Null(manager.GetManagerForTests(task.Id));

            // Aucun contenu téléchargé avant la sélection.
            await Task.Delay(1500);
            var work = Path.Combine(_incomplete, task.Id);
            var written = Directory.Exists(work) ? Directory.GetFiles(work, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
            Assert.Equal(0, written);

            // Sélection puis téléchargement normal.
            var movieIndex = (await manager.GetFilesAsync(task.Id)).Files.Single(f => f.Path == "Mon Film (2024).mkv").Index;
            await manager.StartWithSelectionAsync(task.Id, new[] { movieIndex });
            await ConnectToSeederAsync(manager, task.Id);
            await TestSupport.WaitUntilAsync(() => View(manager, task.Id).State == "Completed", Timeout, "téléchargement après sélection");
            Assert.Equal(TestSupport.Sha256(Source("Mon Film (2024).mkv")), TestSupport.Sha256(Path.Combine(_completed, "Mon Film (2024)", "Mon Film (2024).mkv")));
            await manager.StopAsync();
        }

        [Fact]
        public async Task Download_into_destination_then_move_while_seeding_updates_the_task()
        {
            var films = Path.Combine(_root, "media", "films");
            var archives = Path.Combine(_root, "media", "archives");
            var options = Options(seed: true);
            options.Destinations.Add(new DestinationFolder { Id = "films", Name = "Films", ContentType = MediaTypes.Movies, Path = films });
            options.Destinations.Add(new DestinationFolder { Id = "archives", Name = "Archives", ContentType = MediaTypes.Mixed, Path = archives });
            Directory.CreateDirectory(films);
            Directory.CreateDirectory(archives);

            var manager = NewManager(options);
            await manager.StartAsync();
            var service = new FileOperationsService(manager, _notifier, _log);

            var task = await manager.AddTorrentFileAsync(_torrentBytes);
            var files = await manager.GetFilesAsync(task.Id);
            Assert.Equal("films", files.SuggestedDestinationId);
            Assert.Equal(MediaTypes.Movies, files.SuggestedContentType);

            await manager.StartWithSelectionAsync(task.Id, files.Files.Where(f => f.IsVideo).Select(f => f.Index), "films");
            await ConnectToSeederAsync(manager, task.Id);
            await TestSupport.WaitUntilAsync(() => View(manager, task.Id).State == "Seeding", Timeout, "téléchargement vers la destination Films");

            var finalDir = Path.Combine(films, "Mon Film (2024)");
            Assert.True(File.Exists(Path.Combine(finalDir, "Mon Film (2024).mkv")), "finalisé dans la destination");
            Assert.Equal("Films", View(manager, task.Id).DestinationName);
            Assert.Contains(manager.GetStatus().Roots, r => r.Id == "films" && r.Available);

            // Déplacement du dossier de la tâche pendant le partage : le partage s'arrête, la tâche suit le dossier.
            var op = service.StartMove("films", "Mon Film (2024)", "archives", "", null);
            await TestSupport.WaitUntilAsync(() => service.GetOperations().Single(o => o.Id == op.Id).State is "Done" or "Error", Timeout, "déplacement");
            var done = service.GetOperations().Single(o => o.Id == op.Id);
            Assert.True(done.State == "Done", done.Message);
            Assert.Contains("Partage arrêté", done.Message);
            Assert.Equal("Completed", View(manager, task.Id).State);
            Assert.False(Directory.Exists(finalDir));
            Assert.Equal(TestSupport.Sha256(Source("Mon Film (2024).mkv")), TestSupport.Sha256(Path.Combine(archives, "Mon Film (2024)", "Mon Film (2024).mkv")));
            Assert.Contains(_notifier.RefreshedNow, p => p.EndsWith("Mon Film (2024)", StringComparison.Ordinal));

            // Analyse avant déplacement : vidéos → Films conseillé.
            var analysis = service.Analyze("archives", "Mon Film (2024)");
            Assert.Equal(MediaTypes.Movies, analysis.SuggestedContentType);
            Assert.Equal("films", analysis.SuggestedRootId);

            // Navigation dans la destination.
            var listing = service.List("archives", "Mon Film (2024)");
            Assert.Contains(listing.Entries, e => e.Name == "Extras" && e.IsDirectory);

            // La tâche a suivi : la suppression avec fichiers efface bien les fichiers au nouvel emplacement.
            var result = await manager.RemoveAsync(task.Id, deleteFiles: true, confirmation: task.Id);
            Assert.True(result.DeletedFiles >= 2, "les 2 vidéos (+ un éventuel fragment du fichier non sélectionné)");
            Assert.False(Directory.Exists(Path.Combine(archives, "Mon Film (2024)")));
            await manager.StopAsync();
        }

        [Fact]
        public async Task Manual_library_refresh_targets_the_completed_folder()
        {
            var options = Options(seed: false);
            var manager = NewManager(options);
            Assert.Throws<TorryUserException>(() => manager.RefreshLibraryNow());

            await manager.StartAsync();
            var result = manager.RefreshLibraryNow();
            Assert.True(result.Refreshed);
            Assert.Equal(PetitOurs.Torry.Files.PathGuard.NormalizeDirectory(_completed), Assert.Single(_notifier.RefreshedNow));

            _notifier.InLibrary = false;
            Assert.False(manager.RefreshLibraryNow().Refreshed);
            await manager.StopAsync();
        }

        [Fact]
        public async Task Duplicate_and_invalid_inputs_are_rejected()
        {
            var manager = NewManager(Options(seed: false));
            await manager.StartAsync();
            await manager.AddTorrentFileAsync(_torrentBytes);
            await Assert.ThrowsAsync<TorryUserException>(() => manager.AddTorrentFileAsync(_torrentBytes));
            await Assert.ThrowsAsync<TorryUserException>(() => manager.AddMagnetAsync("http://exemple.org/pas-un-magnet"));
            await Assert.ThrowsAsync<TorryUserException>(() => manager.AddMagnetAsync("magnet:?xt=urn:btih:pas-un-hash"));
            var id = manager.GetStatus().Tasks.Single().Id;
            await Assert.ThrowsAsync<TorryUserException>(() => manager.StartWithSelectionAsync(id, Array.Empty<int>()));
            await Assert.ThrowsAsync<TorryUserException>(() => manager.StartWithSelectionAsync(id, new[] { 99 }));
            await manager.StopAsync();
        }

        [Fact]
        public async Task Magnet_metadata_fetch_can_be_cancelled_and_stays_resumable()
        {
            var manager = NewManager(Options(seed: false));
            await manager.StartAsync();
            var torrent = Torrent.Load(_torrentBytes);
            var magnet = new MagnetLink(torrent.InfoHashes, torrent.Name).ToV1String();
            var task = await manager.AddMagnetAsync(magnet);
            Assert.Equal("FetchingMetadata", task.State);

            await manager.CancelAsync(task.Id);
            Assert.Equal("Cancelled", View(manager, task.Id).State);
            Assert.True(View(manager, task.Id).CanResume);
            await manager.StopAsync();

            // Le lien magnet est conservé dans l'état (nécessaire à la reprise) mais absent des journaux.
            Assert.DoesNotContain(_log.Lines, l => l.Contains("magnet:", StringComparison.OrdinalIgnoreCase));
        }
    }
}
