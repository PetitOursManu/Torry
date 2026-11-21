using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PetitOurs.Torry.Files;
using PetitOurs.Torry.Persistence;
using Xunit;

namespace PetitOurs.Torry.Tests
{
    public class TaskStoreTests : IDisposable
    {
        private readonly string _dir;

        public TaskStoreTests()
        {
            _dir = TestSupport.NewTempDir("torry-store");
        }

        public void Dispose() => TestSupport.TryDelete(_dir);

        [Fact]
        public void Round_trips_tasks_and_leaves_no_temp_file()
        {
            var store = new TaskStore(_dir);
            var record = new TaskRecord
            {
                Id = "abc123",
                InfoHash = "00ff",
                Name = "Film",
                MagnetUri = "magnet:?xt=urn:btih:00ff&tr=https://tracker.example/announce?passkey=SECRET",
                State = TaskState.Seeding,
                ResumeState = TaskState.Seeding,
                SelectionConfirmed = true,
                Files = new List<TorrentFileEntry> { new TorrentFileEntry { Index = 0, Path = "film.mkv", Length = 42, Selected = true, FinalPath = "/x/film.mkv" } },
                UploadedBytes = 1234,
                SeedingSeconds = 99.5,
                AddedUtc = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc)
            };

            store.Save(new[] { record });
            Assert.False(File.Exists(store.StateFile + ".tmp"));

            var loaded = new TaskStore(_dir).Load(out var warning);
            Assert.Null(warning);
            var copy = Assert.Single(loaded);
            Assert.Equal(TaskState.Seeding, copy.State);
            Assert.Equal(1234, copy.UploadedBytes);
            Assert.Equal(99.5, copy.SeedingSeconds);
            Assert.Equal("/x/film.mkv", copy.Files[0].FinalPath);
            Assert.Equal(record.MagnetUri, copy.MagnetUri);
            Assert.Contains("\"Seeding\"", File.ReadAllText(store.StateFile));
        }

        [Fact]
        public void Corrupt_state_file_is_set_aside_without_blocking()
        {
            var store = new TaskStore(_dir);
            store.EnsureDirectories();
            File.WriteAllText(store.StateFile, "{ ceci n'est pas du json");
            var loaded = store.Load(out var warning);
            Assert.Empty(loaded);
            Assert.NotNull(warning);
            Assert.Single(Directory.GetFiles(_dir, "tasks.json.corrupt-*"));
        }

        [Fact]
        public void State_and_torrent_files_are_private_on_unix()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            var store = new TaskStore(_dir);
            store.Save(new[] { new TaskRecord { Id = "a1" } });
            store.SaveTorrent("a1", new byte[] { 1, 2, 3 });
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.StateFile));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.GetTorrentPath("a1")));
        }

        [Theory]
        [InlineData("../evil")]
        [InlineData("a/b")]
        [InlineData("a.b")]
        public void Torrent_path_rejects_unsafe_ids(string id)
        {
            Assert.Throws<UnsafePathException>(() => new TaskStore(_dir).GetTorrentPath(id));
        }
    }

    public class FileManagerDeletionTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _incomplete;
        private readonly string _completed;

        public FileManagerDeletionTests()
        {
            _dir = TestSupport.NewTempDir("torry-fm");
            _incomplete = Path.Combine(_dir, "incomplete");
            _completed = Path.Combine(_dir, "complete");
            Directory.CreateDirectory(_incomplete);
            Directory.CreateDirectory(_completed);
        }

        public void Dispose() => TestSupport.TryDelete(_dir);

        private TaskRecord Record(string id)
        {
            var finalDir = Path.Combine(_completed, "Film");
            Directory.CreateDirectory(finalDir);
            var finalFile = Path.Combine(finalDir, "film.mkv");
            File.WriteAllText(finalFile, "video");
            var work = Path.Combine(_incomplete, id);
            Directory.CreateDirectory(Path.Combine(work, "sub"));
            File.WriteAllText(Path.Combine(work, "sub", "partial.bin"), "x");
            return new TaskRecord
            {
                Id = id,
                IncompleteRoot = _incomplete,
                CompletedRoot = _completed,
                WorkDirectory = work,
                FinalDirectory = finalDir,
                Files = new List<TorrentFileEntry> { new TorrentFileEntry { Index = 0, Path = "film.mkv", Selected = true, FinalPath = finalFile } }
            };
        }

        [Fact]
        public void Deletes_only_task_files()
        {
            var bystander = Path.Combine(_completed, "Film", "ajout-manuel.nfo");
            var record = Record("t1");
            File.WriteAllText(bystander, "à garder");

            var report = FileManager.DeleteTaskFiles(record);

            Assert.Empty(report.Errors);
            Assert.False(Directory.Exists(record.WorkDirectory));
            Assert.False(File.Exists(record.Files[0].FinalPath));
            Assert.True(File.Exists(bystander), "un fichier ajouté manuellement ne doit pas être supprimé");
        }

        [Fact]
        public void Refuses_to_delete_outside_allowed_roots()
        {
            var outside = Path.Combine(_dir, "outside");
            Directory.CreateDirectory(outside);
            var victim = Path.Combine(outside, "important.txt");
            File.WriteAllText(victim, "ne pas toucher");

            var record = Record("t2");
            record.WorkDirectory = outside; // falsifié
            record.Files[0].FinalPath = victim; // falsifié

            var report = FileManager.DeleteTaskFiles(record);

            Assert.True(File.Exists(victim));
            Assert.NotEmpty(report.Skipped);
        }

        [Fact]
        public void Does_not_follow_symlinks_out_of_work_directory()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            var outside = Path.Combine(_dir, "outside2");
            Directory.CreateDirectory(outside);
            var victim = Path.Combine(outside, "important.txt");
            File.WriteAllText(victim, "ne pas toucher");

            var record = Record("t3");
            Directory.CreateSymbolicLink(Path.Combine(record.WorkDirectory, "lien"), outside);

            FileManager.DeleteTaskFiles(record);

            Assert.True(File.Exists(victim));
            Assert.False(Directory.Exists(record.WorkDirectory));
        }

        [Fact]
        public void Final_directory_is_unique_and_reserved()
        {
            var fm = new FileManager(_incomplete, _completed);
            Directory.CreateDirectory(Path.Combine(_completed, "Film"));
            var first = fm.CreateFinalDirectory("Film", "fallback");
            var second = fm.CreateFinalDirectory("Film", "fallback");
            Assert.Equal(Path.Combine(_completed, "Film (2)"), first);
            Assert.Equal(Path.Combine(_completed, "Film (3)"), second);
            Assert.True(Directory.Exists(first));
        }

        [Fact]
        public void Write_check_reports_missing_permissions()
        {
            if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            {
                return; // root ignore les permissions POSIX.
            }

            var locked = Path.Combine(_dir, "locked");
            Directory.CreateDirectory(locked);
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                Assert.NotNull(FileManager.CheckWritable(locked));
            }
            finally
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        [Fact]
        public void Disk_errors_are_described_in_french_without_urls()
        {
            Assert.Contains("Disque plein", DiskErrors.Describe(new IOException("No space left on device")));
            Assert.Contains("Permissions", DiskErrors.Describe(new UnauthorizedAccessException("denied")));
            var safe = DiskErrors.SafeMessage("Tracker https://private.example/announce?passkey=SECRET failed");
            Assert.DoesNotContain("SECRET", safe);
            Assert.DoesNotContain("private.example", safe);
        }
    }
}
