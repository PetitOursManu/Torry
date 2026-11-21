using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PetitOurs.Torry.Files;

namespace PetitOurs.Torry.Persistence
{
    /// <summary>
    /// Persistance des tâches dans le dossier de données d'Emby :
    ///   &lt;data&gt;/torry/tasks.json        — liste des tâches (écriture atomique)
    ///   &lt;data&gt;/torry/torrents/&lt;id&gt;.torrent — métadonnées de chaque tâche
    ///   &lt;data&gt;/torry/engine/             — cache MonoTorrent (fast resume, DHT)
    /// Sous Linux, les fichiers sont créés en mode 600 car ils peuvent contenir des passkeys de trackers privés.
    /// </summary>
    public sealed class TaskStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly object _ioLock = new object();

        public TaskStore(string rootDirectory)
        {
            RootDirectory = Path.GetFullPath(rootDirectory);
            TorrentsDirectory = Path.Combine(RootDirectory, "torrents");
            EngineCacheDirectory = Path.Combine(RootDirectory, "engine");
            StateFile = Path.Combine(RootDirectory, "tasks.json");
        }

        public string RootDirectory { get; }

        public string TorrentsDirectory { get; }

        public string EngineCacheDirectory { get; }

        public string StateFile { get; }

        public void EnsureDirectories()
        {
            CreatePrivateDirectory(RootDirectory);
            CreatePrivateDirectory(TorrentsDirectory);
            CreatePrivateDirectory(EngineCacheDirectory);
        }

        /// <summary>Charge les tâches. Un fichier corrompu est mis de côté (tasks.json.corrupt-…) au lieu de bloquer le démarrage.</summary>
        public List<TaskRecord> Load(out string warning)
        {
            warning = null;
            lock (_ioLock)
            {
                if (!File.Exists(StateFile))
                {
                    return new List<TaskRecord>();
                }

                try
                {
                    var json = File.ReadAllText(StateFile);
                    var doc = JsonSerializer.Deserialize<StoreDocument>(json, JsonOptions);
                    return doc?.Tasks?.Where(t => t != null && !string.IsNullOrEmpty(t.Id)).ToList() ?? new List<TaskRecord>();
                }
                catch (Exception ex) when (ex is JsonException || ex is NotSupportedException)
                {
                    var backup = StateFile + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                    try
                    {
                        File.Move(StateFile, backup);
                    }
                    catch
                    {
                        // Ignoré : on démarre avec une liste vide.
                    }

                    warning = "Le fichier d'état de Torry était illisible ; il a été mis de côté et la liste des tâches repart à zéro.";
                    return new List<TaskRecord>();
                }
            }
        }

        /// <summary>Écriture atomique : fichier temporaire + fsync + remplacement.</summary>
        public void Save(IEnumerable<TaskRecord> tasks)
        {
            var doc = new StoreDocument { Tasks = tasks.ToList() };
            var json = JsonSerializer.SerializeToUtf8Bytes(doc, JsonOptions);
            lock (_ioLock)
            {
                EnsureDirectories();
                var temp = StateFile + ".tmp";
                using (var stream = CreatePrivateFile(temp))
                {
                    stream.Write(json, 0, json.Length);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temp, StateFile, overwrite: true);
            }
        }

        public string GetTorrentPath(string taskId)
        {
            if (string.IsNullOrEmpty(taskId) || taskId.IndexOfAny(new[] { '/', '\\', '.' }) >= 0)
            {
                throw new UnsafePathException("Identifiant de tâche invalide.");
            }

            return Path.Combine(TorrentsDirectory, taskId + ".torrent");
        }

        public void SaveTorrent(string taskId, ReadOnlySpan<byte> data)
        {
            lock (_ioLock)
            {
                EnsureDirectories();
                var path = GetTorrentPath(taskId);
                var temp = path + ".tmp";
                using (var stream = CreatePrivateFile(temp))
                {
                    stream.Write(data);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temp, path, overwrite: true);
            }
        }

        public byte[] LoadTorrent(string taskId)
        {
            var path = GetTorrentPath(taskId);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        public void DeleteTorrent(string taskId)
        {
            var path = GetTorrentPath(taskId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static void CreatePrivateDirectory(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        private static FileStream CreatePrivateFile(string path)
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            return new FileStream(path, options);
        }
    }
}
