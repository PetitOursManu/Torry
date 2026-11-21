using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

// Usage : LocalSeeder <dossier-contenu> <dossier-sortie> <hôte-torry> <port-torry>
// Crée <sortie>/test.torrent et <sortie>/test.magnet, partage le contenu, et se connecte
// périodiquement au port BitTorrent de Torry (connexion ENTRANTE pour Torry).
if (args.Length < 4)
{
    Console.Error.WriteLine("usage: LocalSeeder <content> <out> <host> <port>");
    return 2;
}

var content = Path.GetFullPath(args[0]);
var outDir = Path.GetFullPath(args[1]);
var host = args[2];
var port = int.Parse(args[3]);
Directory.CreateDirectory(outDir);

var creator = new TorrentCreator(TorrentType.V1Only) { PieceLength = 64 * 1024 };
var bytes = (await creator.CreateAsync(new TorrentFileSource(content))).Encode();
var torrent = Torrent.Load(bytes);
File.WriteAllBytes(Path.Combine(outDir, "test.torrent"), bytes);
File.WriteAllText(Path.Combine(outDir, "test.magnet"), new MagnetLink(torrent.InfoHashes, torrent.Name).ToV1String());
Console.WriteLine($"torrent: {torrent.Name} {torrent.InfoHashes.V1.ToHex()} files={torrent.Files.Count} size={torrent.Size}");

var engine = new ClientEngine(new EngineSettingsBuilder
{
    CacheDirectory = Path.Combine(outDir, "seeder-cache"),
    AllowPortForwarding = false,
    AllowLocalPeerDiscovery = false,
    DhtEndPoint = null,
    ListenEndPoints = new Dictionary<string, IPEndPoint> { { "ipv4", new IPEndPoint(IPAddress.Any, 51001) } }
}.ToSettings());

var manager = await engine.AddAsync(torrent, Path.GetDirectoryName(content));
await manager.HashCheckAsync(autoStart: true);

while (true)
{
    if (manager.OpenConnections == 0)
    {
        try
        {
            var ip = (await Dns.GetHostAddressesAsync(host)).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            await manager.AddPeerAsync(new PeerInfo(new Uri($"ipv4://{ip}:{port}")));
        }
        catch (Exception ex)
        {
            Console.WriteLine("peer: " + ex.Message);
        }
    }

    Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} state={manager.State} progress={manager.Progress:0} peers={manager.OpenConnections} up={manager.Monitor.DataBytesSent}");
    await Task.Delay(3000);
}
