using System;
using System.IO;
using System.Threading.Tasks;
using PetitOurs.Torry.Engine;
using Xunit;

namespace PetitOurs.Torry.Tests
{
    public class TorrentInspectorTests : IDisposable
    {
        private readonly string _dir;

        public TorrentInspectorTests()
        {
            _dir = TestSupport.NewTempDir("torry-ti");
        }

        public void Dispose() => TestSupport.TryDelete(_dir);

        private async Task<byte[]> MultiFileTorrentAsync()
        {
            var source = Path.Combine(_dir, "Serie");
            TestSupport.WriteRandomFile(Path.Combine(source, "S01", "e01.mkv"), 40_000, 1);
            TestSupport.WriteRandomFile(Path.Combine(source, "S01", "e02.mkv"), 30_000, 2);
            return await TestSupport.CreateTorrentAsync(source);
        }

        [Fact]
        public async Task Accepts_valid_multi_file_torrent_and_preserves_structure()
        {
            var torrent = TorrentInspector.LoadAndValidate(await MultiFileTorrentAsync());
            var entries = TorrentInspector.BuildEntries(torrent);
            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, e => e.Path == Path.Combine("S01", "e01.mkv"));
            Assert.All(entries, e => Assert.True(e.Selected));
        }

        [Theory]
        [InlineData("..", "evil.mkv")]
        [InlineData("S01", "..", "..", "evil.mkv")]
        [InlineData("S01", "..")]
        [InlineData("..\\..\\evil.mkv")]
        [InlineData("a/../../../evil.mkv")]
        [InlineData("/etc/cron.d/evil")]
        [InlineData("C:\\evil.mkv")]
        [InlineData(".")]
        public async Task Rejects_torrent_with_traversal_in_file_paths(params string[] components)
        {
            var bytes = TestSupport.ReplaceFilePaths(await MultiFileTorrentAsync(), components);
            var ex = Assert.Throws<TorryUserException>(() => TorrentInspector.LoadAndValidate(bytes));
            Assert.Contains("dangereux", ex.Message);
        }

        [Fact]
        public async Task Rejects_duplicate_paths()
        {
            var bytes = TestSupport.ReplaceFilePaths(await MultiFileTorrentAsync(), new[] { "a.mkv" }, new[] { "a.mkv" });
            Assert.Throws<TorryUserException>(() => TorrentInspector.LoadAndValidate(bytes));
        }

        [Fact]
        public void Rejects_garbage_and_empty_input()
        {
            Assert.Throws<TorryUserException>(() => TorrentInspector.LoadAndValidate(Array.Empty<byte>()));
            Assert.Throws<TorryUserException>(() => TorrentInspector.LoadAndValidate(new byte[] { 1, 2, 3, 4 }));
            Assert.Throws<TorryUserException>(() => TorrentInspector.LoadAndValidate(new byte[TorrentInspector.MaxTorrentFileBytes + 1]));
        }

        [Fact]
        public async Task Malicious_torrent_name_does_not_escape_completed_folder()
        {
            // Le nom du torrent sert de nom de dossier final : il est assaini.
            var bytes = TestSupport.ReplaceName(await MultiFileTorrentAsync(), "../../etc");
            var torrent = TorrentInspector.LoadAndValidate(bytes);
            var folder = PetitOurs.Torry.Files.PathGuard.SanitizeFolderName(torrent.Name, "fallback");
            Assert.DoesNotContain("/", folder);
            Assert.DoesNotContain("\\", folder);
            Assert.NotEqual("..", folder);
        }
    }
}
