using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.BEncoding;
using PetitOurs.Torry.Core;

namespace PetitOurs.Torry.Tests
{
    internal static class TestSupport
    {
        public static string NewTempDir(string prefix)
        {
            var dir = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void TryDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
            }
        }

        public static void WriteRandomFile(string path, int length, int seed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            File.WriteAllBytes(path, data);
        }

        public static string Sha256(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                return Convert.ToHexString(SHA256.HashData(stream));
            }
        }

        /// <summary>Crée un .torrent V1 (sans tracker) à partir d'un fichier ou dossier.</summary>
        public static async Task<byte[]> CreateTorrentAsync(string sourcePath, int pieceLength = 16 * 1024)
        {
            var creator = new TorrentCreator(TorrentType.V1Only) { PieceLength = pieceLength };
            var dict = await creator.CreateAsync(new TorrentFileSource(sourcePath));
            return dict.Encode();
        }

        /// <summary>Remplace les chemins des fichiers d'un torrent multi-fichiers (pour fabriquer des torrents malveillants).</summary>
        public static byte[] ReplaceFilePaths(byte[] torrent, params string[][] newPaths)
        {
            var dict = (BEncodedDictionary)BEncodedValue.Decode(torrent);
            var info = (BEncodedDictionary)dict["info"];
            var files = (BEncodedList)info["files"];
            for (var i = 0; i < files.Count && i < newPaths.Length; i++)
            {
                var file = (BEncodedDictionary)files[i];
                var list = new BEncodedList();
                foreach (var component in newPaths[i])
                {
                    list.Add(new BEncodedString(component));
                }

                file["path"] = list;
            }

            return dict.Encode();
        }

        public static byte[] ReplaceName(byte[] torrent, string name)
        {
            var dict = (BEncodedDictionary)BEncodedValue.Decode(torrent);
            var info = (BEncodedDictionary)dict["info"];
            info["name"] = new BEncodedString(name);
            return dict.Encode();
        }

        public static int FreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("Délai dépassé : " + what);
                }

                await Task.Delay(100);
            }
        }
    }

    internal sealed class RecordingNotifier : ILibraryNotifier
    {
        public List<string> Notified { get; } = new List<string>();

        public bool InLibrary { get; set; } = true;

        public bool IsInLibrary(string path) => InLibrary;

        public string NotifyNewContent(string directory)
        {
            lock (Notified)
            {
                Notified.Add(directory);
            }

            return "Actualisation demandée : Films.";
        }
    }

    internal sealed class ListLog : ITorryLog
    {
        public List<string> Lines { get; } = new List<string>();

        public void Info(string message)
        {
            lock (Lines)
            {
                Lines.Add("INFO " + message);
            }
        }

        public void Warn(string message)
        {
            lock (Lines)
            {
                Lines.Add("WARN " + message);
            }
        }

        public void Error(string message, Exception exception = null)
        {
            lock (Lines)
            {
                Lines.Add("ERROR " + message + " " + exception);
            }
        }
    }
}
