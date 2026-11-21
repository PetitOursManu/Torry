using System;
using System.IO;
using PetitOurs.Torry.Files;
using Xunit;

namespace PetitOurs.Torry.Tests
{
    public class PathGuardTests : IDisposable
    {
        private readonly string _root;

        public PathGuardTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "torry-pg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
            }
        }

        [Theory]
        [InlineData("../evil.mkv")]
        [InlineData("..")]
        [InlineData(".")]
        [InlineData("a/../../evil")]
        [InlineData("a/..")]
        [InlineData("a/./b")]
        [InlineData("..\\evil.mkv")]
        [InlineData("a\\..\\..\\evil")]
        [InlineData("/etc/passwd")]
        [InlineData("\\server\\share")]
        [InlineData("C:\\Windows\\evil")]
        [InlineData("C:evil")]
        [InlineData("a//b")]
        [InlineData("")]
        [InlineData("a/\0b")]
        [InlineData("a/\u0007b")]
        [InlineData("...")]
        [InlineData("a/ . ")]
        public void Rejects_malicious_relative_paths(string path)
        {
            Assert.False(PathGuard.TryValidateRelativePath(path, out var reason));
            Assert.False(string.IsNullOrEmpty(reason));
            Assert.Throws<UnsafePathException>(() => PathGuard.CombineInside(_root, path));
        }

        [Theory]
        [InlineData("film.mkv")]
        [InlineData("Saison 1/Episode 01.mkv")]
        [InlineData("Dossier/Sous dossier/fichier..mkv")]
        [InlineData("..film.mkv")]
        [InlineData("Film (2024)/Film: Le retour.mkv")]
        public void Accepts_normal_relative_paths(string path)
        {
            if (OperatingSystem.IsWindows() && path.Contains(':'))
            {
                return;
            }

            Assert.True(PathGuard.TryValidateRelativePath(path, out var reason), reason);
            var combined = PathGuard.CombineInside(_root, path);
            Assert.True(PathGuard.IsInside(_root, combined));
        }

        [Fact]
        public void IsInside_rejects_sibling_prefix()
        {
            var root = Path.Combine(_root, "complete");
            var sibling = Path.Combine(_root, "complete-evil", "x.mkv");
            Assert.False(PathGuard.IsInside(root, sibling));
            Assert.False(PathGuard.IsInside(root, root));
            Assert.True(PathGuard.IsInside(root, Path.Combine(root, "a", "b.mkv")));
            Assert.False(PathGuard.IsInside(root, Path.Combine(root, "..", "x")));
        }

        [Theory]
        [InlineData("Film/../../etc", "Film_.._.._etc")]
        [InlineData("..", "fallback")]
        [InlineData("   ", "fallback")]
        [InlineData("a<b>c|d?e*f", "a_b_c_d_e_f")]
        [InlineData("Nom normal", "Nom normal")]
        public void Sanitizes_folder_names(string input, string expected)
        {
            Assert.Equal(expected, PathGuard.SanitizeFolderName(input, "fallback"));
        }

        [Fact]
        public void Sanitized_folder_name_is_length_limited()
        {
            var name = PathGuard.SanitizeFolderName(new string('x', 500), "fallback");
            Assert.True(name.Length <= PathGuard.MaxFolderNameLength);
        }

        [Fact]
        public void Unique_directory_never_reuses_existing_folder_or_file()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Film"));
            File.WriteAllText(Path.Combine(_root, "Film (2)"), "un fichier, pas un dossier");
            var unique = PathGuard.GetUniqueDirectory(_root, "Film");
            Assert.Equal(Path.Combine(_root, "Film (3)"), unique);
        }

        [Fact]
        public void Unique_file_path_adds_suffix_before_extension()
        {
            var path = Path.Combine(_root, "film.mkv");
            Assert.Equal(path, PathGuard.GetUniqueFilePath(path));
            File.WriteAllText(path, "x");
            Assert.Equal(Path.Combine(_root, "film (2).mkv"), PathGuard.GetUniqueFilePath(path));
        }

        [Fact]
        public void Folder_configuration_rejects_incomplete_inside_completed()
        {
            var completed = Path.Combine(_root, "media");
            var incomplete = Path.Combine(completed, "incomplete");
            Assert.NotEmpty(PathGuard.ValidateFolderConfiguration(incomplete, completed));
            Assert.NotEmpty(PathGuard.ValidateFolderConfiguration(completed, completed));
            Assert.NotEmpty(PathGuard.ValidateFolderConfiguration("relative/path", completed));
            Assert.Empty(PathGuard.ValidateFolderConfiguration(Path.Combine(_root, "inc"), completed));
        }
    }
}
