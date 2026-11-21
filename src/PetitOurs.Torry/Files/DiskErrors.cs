using System;
using System.IO;
using System.Net.Sockets;

namespace PetitOurs.Torry.Files
{
    /// <summary>Traduit les exceptions techniques en messages clairs (sans chemin sensible ni URL de tracker).</summary>
    public static class DiskErrors
    {
        // errno Linux / codes Windows correspondants.
        private const int ENOSPC = 28;
        private const int EDQUOT = 122;
        private const int ERROR_DISK_FULL = 112;
        private const int ERROR_HANDLE_DISK_FULL = 39;
        private const int EROFS = 30;

        public static bool IsDiskFull(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is IOException io)
                {
                    var code = io.HResult & 0xFFFF;
                    if (code == ENOSPC || code == EDQUOT || code == ERROR_DISK_FULL || code == ERROR_HANDLE_DISK_FULL)
                    {
                        return true;
                    }

                    var message = io.Message ?? string.Empty;
                    if (message.IndexOf("No space left", StringComparison.OrdinalIgnoreCase) >= 0
                        || message.IndexOf("Disk quota", StringComparison.OrdinalIgnoreCase) >= 0
                        || message.IndexOf("not enough space", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static string Describe(Exception ex)
        {
            if (ex == null)
            {
                return "Erreur inconnue.";
            }

            if (IsDiskFull(ex))
            {
                return "Disque plein : libérez de l'espace puis reprenez le téléchargement.";
            }

            for (var e = ex; e != null; e = e.InnerException)
            {
                switch (e)
                {
                    case UnsafePathException unsafePath:
                        return unsafePath.Message;
                    case UnauthorizedAccessException _:
                        return "Permissions insuffisantes : le processus Emby ne peut pas écrire dans le dossier de téléchargement.";
                    case IOException io when ((io.HResult & 0xFFFF) == EROFS || (io.Message ?? string.Empty).IndexOf("Read-only file system", StringComparison.OrdinalIgnoreCase) >= 0):
                        return "Système de fichiers en lecture seule : vérifiez le montage du volume Docker.";
                    case PathTooLongException _:
                        return "Chemin de fichier trop long pour le système de fichiers.";
                    case DirectoryNotFoundException _:
                        return "Dossier introuvable : vérifiez que le volume Docker est bien monté.";
                    case SocketException socket when socket.SocketErrorCode == SocketError.AddressAlreadyInUse:
                        return "Le port BitTorrent est déjà utilisé par un autre programme.";
                    case SocketException _:
                        return "Erreur réseau : " + ((SocketException)e).SocketErrorCode + ".";
                }
            }

            if (ex is IOException)
            {
                return "Erreur d'entrée/sortie disque : " + SafeMessage(ex.Message);
            }

            return SafeMessage(ex.Message);
        }

        /// <summary>Évite de recopier d'éventuelles URL (trackers privés avec passkey) dans un message affiché ou journalisé.</summary>
        public static string SafeMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return "Erreur inconnue.";
            }

            var redacted = System.Text.RegularExpressions.Regex.Replace(message, @"(?i)\b(https?|udp|wss?|magnet):[^\s'""]+", "[adresse masquée]");
            return redacted.Length > 300 ? redacted.Substring(0, 300) + "…" : redacted;
        }
    }
}
