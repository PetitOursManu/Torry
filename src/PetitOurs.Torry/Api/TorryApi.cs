using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using PetitOurs.Torry.Core;
using PetitOurs.Torry.Emby;
using PetitOurs.Torry.Engine;

namespace PetitOurs.Torry.Api
{
    // ------------------------------------------------------------------------------------------
    // Toutes les routes exigent un jeton Emby valide ET le rôle administrateur (Roles = "admin").
    // Aucune route anonyme. Les liens magnet et les .torrent transitent uniquement dans le corps
    // JSON des requêtes POST (Emby journalise les URL, jamais les corps).
    // ------------------------------------------------------------------------------------------

    [Route("/Torry/Status", "GET", Summary = "État du moteur et liste des tâches")]
    [Authenticated(Roles = "admin")]
    public class GetTorryStatus : IReturn<StatusView>
    {
    }

    [Route("/Torry/Tasks/Magnet", "POST", Summary = "Ajoute un lien magnet")]
    [Authenticated(Roles = "admin")]
    public class AddTorryMagnet : IReturn<TaskView>
    {
        public string Magnet { get; set; }
    }

    [Route("/Torry/Tasks/TorrentFile", "POST", Summary = "Importe un fichier .torrent (contenu en base64)")]
    [Authenticated(Roles = "admin")]
    public class AddTorryTorrentFile : IReturn<TaskView>
    {
        public string Data { get; set; }
    }

    [Route("/Torry/Tasks/{Id}/Files", "GET", Summary = "Fichiers d'une tâche")]
    [Authenticated(Roles = "admin")]
    public class GetTorryTaskFiles : IReturn<List<FileView>>
    {
        public string Id { get; set; }
    }

    [Route("/Torry/Tasks/{Id}/Start", "POST", Summary = "Valide la sélection des fichiers et démarre")]
    [Authenticated(Roles = "admin")]
    public class StartTorryTask : IReturn<TaskView>
    {
        public string Id { get; set; }

        /// <summary>Indices des fichiers sélectionnés, séparés par des virgules.</summary>
        public string SelectedFiles { get; set; }
    }

    [Route("/Torry/Tasks/{Id}/Pause", "POST")]
    [Authenticated(Roles = "admin")]
    public class PauseTorryTask : IReturn<TaskView>
    {
        public string Id { get; set; }
    }

    [Route("/Torry/Tasks/{Id}/Resume", "POST")]
    [Authenticated(Roles = "admin")]
    public class ResumeTorryTask : IReturn<TaskView>
    {
        public string Id { get; set; }
    }

    [Route("/Torry/Tasks/{Id}/Cancel", "POST", Summary = "Annule le téléchargement (fichiers conservés) ou arrête le partage")]
    [Authenticated(Roles = "admin")]
    public class CancelTorryTask : IReturn<TaskView>
    {
        public string Id { get; set; }
    }

    [Route("/Torry/Tasks/{Id}/Delete", "POST", Summary = "Supprime la tâche ; fichiers conservés sauf confirmation explicite")]
    [Authenticated(Roles = "admin")]
    public class DeleteTorryTask : IReturn<RemoveResult>
    {
        public string Id { get; set; }

        public bool DeleteFiles { get; set; }

        /// <summary>Doit être égal à l'identifiant de la tâche pour supprimer les fichiers.</summary>
        public string Confirmation { get; set; }
    }

    [Route("/Torry/Settings", "GET")]
    [Authenticated(Roles = "admin")]
    public class GetTorrySettings : IReturn<TorryOptions>
    {
    }

    [Route("/Torry/Settings", "POST")]
    [Authenticated(Roles = "admin")]
    public class UpdateTorrySettings : IReturn<TorryOptions>
    {
        public string IncompleteDirectory { get; set; }

        public string CompletedDirectory { get; set; }

        public int ListenPort { get; set; }

        public bool EnableDht { get; set; }

        public bool EnablePortForwarding { get; set; }

        public int MaxDownloadRateKiB { get; set; }

        public int MaxUploadRateKiB { get; set; }

        public int MaxActiveDownloads { get; set; }

        public int MaxConnections { get; set; }

        public bool SeedAfterDownload { get; set; }

        public double SeedRatioLimit { get; set; }

        public int SeedTimeLimitMinutes { get; set; }

        public bool AutoStartAfterMetadata { get; set; }

        public bool RefreshLibraryOnComplete { get; set; }
    }

    public class TorryService : IService, IRequiresRequest
    {
        private readonly IAuthorizationContext _authorizationContext;

        public TorryService(IAuthorizationContext authorizationContext)
        {
            _authorizationContext = authorizationContext;
        }

        public IRequest Request { get; set; }

        public object Get(GetTorryStatus request)
        {
            AssertAdministrator();
            return Manager().GetStatus();
        }

        public Task<object> Post(AddTorryMagnet request) => Run(m => m.AddMagnetAsync(request.Magnet));

        public Task<object> Post(AddTorryTorrentFile request)
        {
            return Run(m =>
            {
                byte[] data;
                try
                {
                    var base64 = request.Data ?? string.Empty;
                    var comma = base64.IndexOf(',');
                    if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
                    {
                        base64 = base64.Substring(comma + 1);
                    }

                    if (base64.Length > (TorrentInspector.MaxTorrentFileBytes / 3 * 4) + 8)
                    {
                        throw new TorryUserException("Fichier .torrent trop volumineux (10 Mio maximum).");
                    }

                    data = Convert.FromBase64String(base64);
                }
                catch (FormatException)
                {
                    throw new TorryUserException("Contenu du fichier .torrent illisible.");
                }

                return m.AddTorrentFileAsync(data);
            });
        }

        public Task<object> Get(GetTorryTaskFiles request) => Run(m => m.GetFilesAsync(request.Id));

        public Task<object> Post(StartTorryTask request)
        {
            return Run(m =>
            {
                var indices = new List<int>();
                foreach (var part in (request.SelectedFiles ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(part.Trim(), out var index))
                    {
                        throw new TorryUserException("Sélection de fichiers invalide.");
                    }

                    indices.Add(index);
                }

                return m.StartWithSelectionAsync(request.Id, indices);
            });
        }

        public Task<object> Post(PauseTorryTask request) => Run(m => m.PauseAsync(request.Id));

        public Task<object> Post(ResumeTorryTask request) => Run(m => m.ResumeAsync(request.Id));

        public Task<object> Post(CancelTorryTask request) => Run(m => m.CancelAsync(request.Id));

        public Task<object> Post(DeleteTorryTask request) => Run(m => m.RemoveAsync(request.Id, request.DeleteFiles, request.Confirmation));

        public object Get(GetTorrySettings request)
        {
            AssertAdministrator();
            return (Plugin.Instance?.Configuration ?? new PluginConfiguration()).ToOptions();
        }

        public async Task<object> Post(UpdateTorrySettings request)
        {
            AssertAdministrator();
            var options = new TorryOptions
            {
                IncompleteDirectory = request.IncompleteDirectory?.Trim(),
                CompletedDirectory = request.CompletedDirectory?.Trim(),
                ListenPort = request.ListenPort,
                EnableDht = request.EnableDht,
                EnablePortForwarding = request.EnablePortForwarding,
                MaxDownloadRateKiB = request.MaxDownloadRateKiB,
                MaxUploadRateKiB = request.MaxUploadRateKiB,
                MaxActiveDownloads = request.MaxActiveDownloads,
                MaxConnections = request.MaxConnections,
                SeedAfterDownload = request.SeedAfterDownload,
                SeedRatioLimit = request.SeedRatioLimit,
                SeedTimeLimitMinutes = request.SeedTimeLimitMinutes,
                AutoStartAfterMetadata = request.AutoStartAfterMetadata,
                RefreshLibraryOnComplete = request.RefreshLibraryOnComplete
            };

            try
            {
                await Manager().UpdateOptionsAsync(options).ConfigureAwait(false);
            }
            catch (TorryUserException ex)
            {
                throw new ArgumentException(ex.Message);
            }

            var plugin = Plugin.Instance;
            plugin.Configuration.CopyFrom(options);
            plugin.SaveConfiguration();
            return plugin.Configuration.ToOptions();
        }

        private async Task<object> Run<T>(Func<TorrentTaskManager, Task<T>> action)
        {
            AssertAdministrator();
            try
            {
                return await action(Manager()).ConfigureAwait(false);
            }
            catch (TorryUserException ex)
            {
                // Emby renvoie ArgumentException en HTTP 400 avec le message.
                throw new ArgumentException(ex.Message);
            }
        }

        private static TorrentTaskManager Manager()
        {
            return TorryEntryPoint.Manager ?? throw new ArgumentException("Le moteur Torry n'est pas encore démarré.");
        }

        /// <summary>
        /// Défense en profondeur : l'attribut [Authenticated(Roles = "admin")] fait déjà ce contrôle dans Emby.
        /// Les clés d'API serveur (créées par un administrateur) ne sont associées à aucun utilisateur : Emby les
        /// traite comme des accès administrateur, Torry fait de même.
        /// </summary>
        private void AssertAdministrator()
        {
            var info = _authorizationContext.GetAuthorizationInfo(Request);
            if (info?.User != null && !info.User.Policy.IsAdministrator)
            {
                throw new SecurityException("Réservé aux administrateurs.");
            }
        }
    }
}
