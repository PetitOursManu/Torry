using System;
using System.Linq;
using System.Reflection;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using PetitOurs.Torry.Api;
using Xunit;

namespace PetitOurs.Torry.Tests
{
    /// <summary>
    /// Vérifie statiquement que toutes les routes Torry exigent l'authentification Emby avec le rôle admin.
    /// (Le comportement réel 401/403/200 est vérifié contre un serveur Emby dans eng/verify-emby.sh.)
    /// </summary>
    public class ApiSecurityTests
    {
        private static readonly Type[] RequestTypes = typeof(TorryService).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes<RouteAttribute>().Any())
            .ToArray();

        [Fact]
        public void Every_route_requires_admin_authentication()
        {
            Assert.NotEmpty(RequestTypes);
            foreach (var type in RequestTypes)
            {
                var auth = type.GetCustomAttribute<AuthenticatedAttribute>();
                Assert.True(auth != null, type.Name + " n'a pas d'attribut [Authenticated]");
                Assert.Contains("admin", auth.GetRoles(), StringComparer.OrdinalIgnoreCase);
                Assert.False(auth.AllowLocal, type.Name + " ne doit pas autoriser l'accès local anonyme");
                Assert.False(auth.AllowLocalOnly, type.Name);
                Assert.False(auth.AllowBeforeStartupWizard, type.Name);
            }
        }

        [Fact]
        public void Every_route_is_handled_by_the_service()
        {
            var handled = typeof(TorryService).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "Get" || m.Name == "Post")
                .Select(m => m.GetParameters().Single().ParameterType)
                .ToHashSet();
            foreach (var type in RequestTypes)
            {
                Assert.Contains(type, handled);
            }
        }

        [Fact]
        public void Magnet_and_torrent_are_never_sent_in_url()
        {
            // Emby journalise l'URL des requêtes : le magnet et le .torrent doivent passer dans le corps (POST).
            foreach (var type in new[] { typeof(AddTorryMagnet), typeof(AddTorryTorrentFile) })
            {
                var route = type.GetCustomAttribute<RouteAttribute>();
                Assert.Equal("POST", route.Verbs);
                Assert.DoesNotContain("{", route.Path);
            }
        }
    }
}
