using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PetitOurs.Torry.Core;
using PetitOurs.Torry.Files;
using Xunit;

namespace PetitOurs.Torry.Tests
{
    public class MediaTypesTests
    {
        private static KeyValuePair<string, long> F(string path, long size = 1000) => new KeyValuePair<string, long>(path, size);

        [Theory]
        [InlineData("film.MKV", FileCategory.Video)]
        [InlineData("a/b/chanson.flac", FileCategory.Audio)]
        [InlineData("photo.jpeg", FileCategory.Image)]
        [InlineData("roman.epub", FileCategory.Book)]
        [InlineData("film.fr.srt", FileCategory.Subtitle)]
        [InlineData("notes.txt", FileCategory.Other)]
        public void Detects_categories(string path, FileCategory expected) => Assert.Equal(expected, MediaTypes.GetCategory(path));

        [Fact]
        public void Suggests_content_type_from_files()
        {
            Assert.Equal(MediaTypes.Movies, MediaTypes.SuggestContentType(new[] { F("Mon Film (2024)/Mon Film.mkv", 5000), F("Mon Film (2024)/Mon Film.srt", 10) }));
            Assert.Equal(MediaTypes.TvShows, MediaTypes.SuggestContentType(new[] { F("Serie/S01E01.mkv"), F("Serie/S01E02.mkv") }));
            Assert.Equal(MediaTypes.TvShows, MediaTypes.SuggestContentType(new[] { F("Serie/Saison 1/episode 1.mkv"), F("Serie/Saison 1/episode 2.mkv") }));
            Assert.Equal(MediaTypes.Music, MediaTypes.SuggestContentType(new[] { F("Album/01.flac"), F("Album/cover.jpg", 10) }));
            Assert.Equal(MediaTypes.AudioBooks, MediaTypes.SuggestContentType(new[] { F("Livre/livre.m4b") }));
            Assert.Equal(MediaTypes.Books, MediaTypes.SuggestContentType(new[] { F("roman.epub") }));
            Assert.Null(MediaTypes.SuggestContentType(new[] { F("archive.zip") }));
        }

        [Fact]
        public void Suggests_matching_destination()
        {
            var destinations = new List<DestinationFolder>
            {
                new DestinationFolder { Id = "films", Name = "Films", ContentType = MediaTypes.Movies, Path = "/media/films" },
                new DestinationFolder { Id = "series", Name = "Séries", ContentType = MediaTypes.TvShows, Path = "/media/series" },
                new DestinationFolder { Id = "mix", Name = "Divers", ContentType = MediaTypes.Mixed, Path = "/media/divers" }
            };
            Assert.Equal("series", MediaTypes.SuggestDestination(destinations, new[] { F("S02E05.mkv") }).Id);
            Assert.Equal("films", MediaTypes.SuggestDestination(destinations, new[] { F("film.mkv") }).Id);
            Assert.Equal("mix", MediaTypes.SuggestDestination(destinations, new[] { F("album/01.mp3") }).Id);
            Assert.Null(MediaTypes.SuggestDestination(destinations, new[] { F("archive.zip") }));
            Assert.Null(MediaTypes.SuggestDestination(new List<DestinationFolder>(), new[] { F("film.mkv") }));
        }

        [Fact]
        public void Destination_validation()
        {
            var options = new TorryOptions { IncompleteDirectory = "/downloads/incomplete", CompletedDirectory = "/downloads/complete" };
            options.Destinations.Add(new DestinationFolder { Id = "a1", Name = "Films", ContentType = "movies", Path = "/media/films" });
            Assert.Empty(options.Validate());

            options.Destinations.Add(new DestinationFolder { Id = "a2", Name = "films", ContentType = "movies", Path = "/media/autre" });
            Assert.Contains(options.Validate(), e => e.Contains("Deux dossiers"));
            options.Destinations.RemoveAt(1);

            options.Destinations.Add(new DestinationFolder { Id = "a3", Name = "Piège", ContentType = "movies", Path = "/downloads/incomplete/x" });
            Assert.Contains(options.Validate(), e => e.Contains("en cours"));
            options.Destinations.RemoveAt(1);

            options.Destinations.Add(new DestinationFolder { Id = "a4", Name = "Inconnu", ContentType = "jeux", Path = "/media/jeux" });
            Assert.Contains(options.Validate(), e => e.Contains("type de contenu"));
            options.Destinations.RemoveAt(1);

            options.Destinations.Add(new DestinationFolder { Id = "../x", Name = "Id", ContentType = "movies", Path = "/media/id" });
            Assert.Contains(options.Validate(), e => e.Contains("identifiant"));
        }
    }

    public class FileBrowserTests : IDisposable
    {
        private readonly string _dir;
        private readonly BrowseRoot _complete;
        private readonly BrowseRoot _films;

        public FileBrowserTests()
        {
            _dir = TestSupport.NewTempDir("torry-fb");
            _complete = new BrowseRoot { Id = "complete", Name = "Terminés", Path = Path.Combine(_dir, "complete") };
            _films = new BrowseRoot { Id = "films", Name = "Films", Path = Path.Combine(_dir, "films"), ContentType = "movies" };
            Directory.CreateDirectory(_complete.Path);
            Directory.CreateDirectory(_films.Path);
            TestSupport.WriteRandomFile(Path.Combine(_complete.Path, "Mon Film (2024)", "Mon Film.mkv"), 2048, 1);
            TestSupport.WriteRandomFile(Path.Combine(_complete.Path, "Mon Film (2024)", "Extras", "bonus.mkv"), 512, 2);
        }

        public void Dispose() => TestSupport.TryDelete(_dir);

        [Theory]
        [InlineData("../films")]
        [InlineData("Mon Film (2024)/../../films")]
        [InlineData("..")]
        public void Resolve_refuses_leaving_the_root(string relative)
        {
            Assert.ThrowsAny<Exception>(() => FileBrowser.Resolve(_complete, relative));
        }

        [Fact]
        public void Leading_slash_is_relative_to_the_root()
        {
            var resolved = FileBrowser.Resolve(_complete, "/etc");
            Assert.True(PathGuard.IsInside(_complete.Path, resolved));
        }

        [Fact]
        public void Lists_directories_first_with_categories()
        {
            var listing = FileBrowser.List(_complete, "Mon Film (2024)");
            Assert.Equal("Mon Film (2024)", listing.Path);
            Assert.Equal(new[] { "Extras", "Mon Film.mkv" }, listing.Entries.Select(e => e.Name).ToArray());
            Assert.True(listing.Entries[0].IsDirectory);
            Assert.Equal("video", listing.Entries[1].Category);
            Assert.Contains("movies", listing.Entries[1].CompatibleContentTypes);
            Assert.Equal("Mon Film (2024)/Mon Film.mkv", listing.Entries[1].Path);
        }

        [Fact]
        public void Symlinks_are_hidden_and_refused()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            Directory.CreateSymbolicLink(Path.Combine(_complete.Path, "lien"), "/etc");
            Assert.DoesNotContain(FileBrowser.List(_complete, "").Entries, e => e.Name == "lien");
            Assert.Throws<UnsafePathException>(() => FileBrowser.Resolve(_complete, "lien/passwd"));
            Assert.Throws<UnsafePathException>(() => FileBrowser.PlanMove(_complete, "lien", _films, "", null));
        }

        [Fact]
        public void Move_validations()
        {
            Assert.Throws<FileOperationException>(() => FileBrowser.PlanMove(_complete, "", _films, "", null));
            Assert.Throws<FileOperationException>(() => FileBrowser.PlanMove(_complete, "Mon Film (2024)", _complete, "Mon Film (2024)/Extras", null));
            Assert.Throws<FileOperationException>(() => FileBrowser.PlanMove(_complete, "Mon Film (2024)", _complete, "", null));
            Assert.Throws<FileOperationException>(() => FileBrowser.PlanMove(_complete, "absent.mkv", _films, "", null));
            Assert.Throws<FileOperationException>(() => FileBrowser.PlanMove(_complete, "Mon Film (2024)", _films, "", "a/b"));
            Assert.ThrowsAny<Exception>(() => FileBrowser.PlanMove(_complete, "Mon Film (2024)", _films, "", ".."));
        }

        [Fact]
        public void Move_folder_and_never_overwrite()
        {
            Directory.CreateDirectory(Path.Combine(_films.Path, "Mon Film (2024)"));
            var plan = FileBrowser.PlanMove(_complete, "Mon Film (2024)", _films, "", null);
            var target = FileBrowser.ReserveTarget(plan);
            Assert.Equal(Path.Combine(_films.Path, "Mon Film (2024) (2)"), target);
            FileBrowser.Execute(plan, target, null);
            Assert.True(File.Exists(Path.Combine(target, "Extras", "bonus.mkv")));
            Assert.False(Directory.Exists(Path.Combine(_complete.Path, "Mon Film (2024)")));
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_films.Path, "Mon Film (2024)")));
        }

        [Fact]
        public void Rename_file_in_place()
        {
            var plan = FileBrowser.PlanMove(_complete, "Mon Film (2024)/Mon Film.mkv", _complete, "Mon Film (2024)", "Mon Film (2024).mkv");
            var target = FileBrowser.ReserveTarget(plan);
            FileBrowser.Execute(plan, target, null);
            Assert.True(File.Exists(Path.Combine(_complete.Path, "Mon Film (2024)", "Mon Film (2024).mkv")));
        }

        [Fact]
        public void Create_folder_validates_names()
        {
            Assert.Equal("Mon Film (2024)/Sous-titres", FileBrowser.CreateFolder(_complete, "Mon Film (2024)", "Sous-titres"));
            Assert.Throws<FileOperationException>(() => FileBrowser.CreateFolder(_complete, "", "Mon Film (2024)"));
            Assert.Throws<FileOperationException>(() => FileBrowser.CreateFolder(_complete, "", "a/b"));
            Assert.ThrowsAny<Exception>(() => FileBrowser.CreateFolder(_complete, "", ".."));
        }

        [Fact]
        public void Cross_filesystem_folder_move_copies_then_deletes()
        {
            // Dans un conteneur Linux, /dev/shm (tmpfs) et /tmp sont des systèmes de fichiers distincts :
            // Directory.Move échoue (EXDEV) et Torry doit copier puis supprimer.
            if (OperatingSystem.IsWindows() || !Directory.Exists("/dev/shm"))
            {
                return;
            }

            var other = new BrowseRoot { Id = "shm", Name = "Autre volume", Path = Path.Combine("/dev/shm", "torry-" + Guid.NewGuid().ToString("N")) };
            Directory.CreateDirectory(other.Path);
            try
            {
                var source = Path.Combine(_complete.Path, "Mon Film (2024)", "Mon Film.mkv");
                var hash = TestSupport.Sha256(source);
                var plan = FileBrowser.PlanMove(_complete, "Mon Film (2024)", other, "", null);
                var target = FileBrowser.ReserveTarget(plan);
                long last = 0;
                var copied = FileBrowser.Execute(plan, target, done => last = done);
                Assert.True(copied, "copie attendue entre deux systèmes de fichiers");
                Assert.Equal(2048 + 512, last);
                Assert.Equal(hash, TestSupport.Sha256(Path.Combine(target, "Mon Film.mkv")));
                Assert.False(Directory.Exists(plan.Source));
            }
            finally
            {
                TestSupport.TryDelete(other.Path);
            }
        }
    }
}
