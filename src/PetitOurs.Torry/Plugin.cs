using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace PetitOurs.Torry
{
    /// <summary>
    /// Torry for Emby — extension communautaire indépendante, sans affiliation officielle avec Emby.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public static readonly Guid PluginGuid = new Guid("ae967ccc-2927-494e-aae6-3db68f8913cf");

        public const string PageName = "torry";
        public const string ScriptName = "torryjs";

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin Instance { get; private set; }

        public override string Name => "Torry for Emby";

        public override string Description => "Téléchargement BitTorrent intégré au serveur Emby. Extension communautaire indépendante, sans affiliation avec Emby.";

        public override Guid Id => PluginGuid;

        /// <summary>
        /// Page web unique (navigateur uniquement) : visible dans le menu d'administration du serveur
        /// et via le bouton « Paramètres » du plugin.
        /// </summary>
        public IEnumerable<PluginPageInfo> GetPages()
        {
            var ns = GetType().Namespace;
            return new[]
            {
                new PluginPageInfo
                {
                    Name = PageName,
                    DisplayName = "Torry",
                    EmbeddedResourcePath = ns + ".Web.torry.html",
                    EnableInMainMenu = true,
                    MenuSection = "server",
                    MenuIcon = "download",
                    IsMainConfigPage = true
                },
                new PluginPageInfo
                {
                    Name = ScriptName,
                    EmbeddedResourcePath = ns + ".Web.torry.js"
                }
            };
        }
    }
}
