# Compte rendu des vérifications réellement exécutées

Date : 21/11/2026. Environnement : Windows 11 + Docker Desktop (moteur Linux, WSL2, x86_64).
Aucun SDK .NET n'était installé localement : tous les builds et tests ont tourné dans le conteneur officiel
`mcr.microsoft.com/dotnet/sdk:8.0.428`, épinglé par empreinte.

Tout ce qui figure ci-dessous a été **exécuté**. Ce qui n'a pas pu l'être est listé à la fin.

## 1. Documentation, SDK et runtime d'Emby

| Vérification | Méthode | Résultat |
|---|---|---|
| Dernière version d'Emby et du SDK | GitHub `MediaBrowser/Emby.Releases`, `Emby.SDK`, dev.emby.media, NuGet | Serveur 4.10.2.0 (stable, 20/10/2026) ; `MediaBrowser.Server.Core` 4.9.1.90 = dernière version stable sur NuGet |
| Runtime de l'image Docker | `EmbyServer.runtimeconfig.json` de `emby/embyserver:4.10.2.0` | **.NET 8.0.28** (`net8.0`), autonome |
| Chargement des plugins | Décompilation de `Emby.Server.Implementations` (`ApplicationHost.GetPluginAssemblies`) | Chaque `*.dll` de `plugins/` est chargé par `Assembly.Load(byte[])` ; aucun résolveur de dépendances |
| Authentification | Décompilation de `AuthenticatedAttribute` / `AuthService` | `Roles="admin"` → 403 si l'utilisateur n'est pas administrateur ; 401 sans jeton valide ; les clés d'API serveur sont exemptées des rôles |
| Codes HTTP des erreurs | Décompilation de `HttpListenerHost` | `ArgumentException` → 400 ; `SecurityException` → 401/403 |
| Journalisation des requêtes | Idem | Emby journalise l'**URL** des requêtes, d'où l'envoi des magnets dans le corps POST |
| Pages de plugin | Lecture du client web 4.10.2 (`pluginmanager.js`, `approuter.js`, `viewmanager`), plugin MBBackup livré avec Emby | `EnableInMainMenu` + `MenuSection` pour le menu d'administration ; `data-controller="__plugin/<nom>"` ; contrôleur `BaseView` |
| Applications natives | `pluginmanager.js` | Liste blanche `allowedPluginConfigs` appliquée si l'hôte déclare `restrictedplugins` : Torry n'y figure pas |
| MonoTorrent | NuGet + décompilation | 3.0.2, MIT, entièrement managé (12 assemblies) |
| Défauts de MonoTorrent qui influent sur la conception | Décompilation | (a) `MoveFileAsync` ajoute `.!mt` à tort quand `UsePartialFiles` est activé ; (b) `DownloadMetadataAsync` refuse les connexions entrantes (gestionnaire privé) ; (c) `PathValidator` laisse passer `a/..`. Contournements : fichiers partiels désactivés ; gestionnaire public arrêté dès réception des métadonnées ; validation propre à Torry |

## 2. Validation progressive du chargement dans Emby 4.10.2 (Docker)

1. **Plugin minimal** (classe `Plugin` + `IServerEntryPoint`) : chargé, point d'entrée exécuté :
   `Torry: point d'entrée démarré (runtime .NET 8.0.28)`.
2. **MonoTorrent en DLL séparées** à côté du plugin : **échec** reproduit,
   `Could not load file or assembly 'MonoTorrent, Version=2.0.0.0'`.
3. **MonoTorrent fusionné** (dotnet-ilrepack 2.0.44, `/internalize`) : types résolus, moteur instancié.
   La dernière version, 2.0.48, cible .NET 10 et ne s'exécute pas avec le SDK 8 : d'où l'épinglage en 2.0.44.

## 3. Tests automatisés (xUnit) — `./build.sh --test`

**66 tests, 66 réussis**, exécutés dans le conteneur SDK Linux, en root :

- **Chemins malveillants** (`PathGuardTests`, `TorrentInspectorTests`) : `..`, `a/..`, `a\..\..\x`, chemins
  absolus Unix et Windows, `C:x`, composants vides, caractères de contrôle et nuls, noms faits de points ou
  d'espaces. Torrents fabriqués avec ces chemins (`..`/`evil.mkv`, `S01/../../evil.mkv`, `/etc/cron.d/evil`…),
  doublons de chemins, nom de torrent `../../etc`, préfixe frère (`complete-evil`). Collisions : `Film (2)`,
  `Film (3)`, fichier homonyme.
- **Persistance** : aller-retour JSON, absence de fichier temporaire résiduel, fichier corrompu mis de côté
  sans bloquer le démarrage, mode 600 des fichiers d'état et `.torrent`, identifiants de tâche dangereux refusés.
- **Suppression** : seuls les fichiers de la tâche sont supprimés (un `.nfo` ajouté à la main est conservé) ;
  chemins falsifiés hors des racines refusés ; lien symbolique non suivi.
- **Autorisations (statique)** : toutes les routes portent `[Authenticated(Roles="admin")]`, sans exemption
  locale ni pré-assistant ; toutes sont servies ; magnet et `.torrent` uniquement en POST.
- **Essaim BitTorrent local réel** (`SwarmIntegrationTests`) : deux moteurs MonoTorrent sur 127.0.0.1.
  - Import `.torrent`, sélection des seules vidéos, téléchargement, finalisation dans `Mon Film (2024) (2)`
    (un dossier `Mon Film (2024)` préexistant reste intact), sommes SHA-256 identiques, fichier non sélectionné
    absent, aucun doublon dans `incomplete`, notification de la bibliothèque.
  - Arrêt puis nouvelle instance : le partage reprend depuis l'emplacement final, sans retéléchargement.
  - Téléchargement interrompu à environ 5 % (débit limité), redémarrage, reprise jusqu'au bout.
  - Suppression avec fichiers refusée sans la bonne confirmation, puis effective avec elle (3 fichiers).
  - Magnet : métadonnées reçues via un **pair entrant** et via un **pair sortant** ; **0 octet** écrit avant
    la sélection ; téléchargement après sélection.
  - Annulation pendant la recherche des métadonnées : la tâche reste reprenable.
  - Doublons, magnets invalides et sélections vides ou hors limites refusés.
  - Aucun lien magnet dans les journaux.

Le test de permissions `Write_check_reports_missing_permissions` ne fait rien quand il tourne en root. Il
est remplacé par la vérification dans Emby (section 5).

## 4. Vérification de bout en bout dans un vrai Emby — `eng/verify-emby.sh`

Le script crée un serveur `emby/embyserver:4.10.2.0` neuf et un seeder local (`eng/LocalSeeder`, MonoTorrent),
sans aucun tracker ni accès Internet BitTorrent. Le seeder partage deux vraies vidéos H.264, générées avec le
ffmpeg d'Emby, et un fichier texte. Tous les appels HTTP passent par l'API réelle d'Emby.

**Exécution finale : 48 vérifications, 0 échec.**

| Domaine | Vérifications réussies |
|---|---|
| Installation | Assistant de premier démarrage ; plugin listé par `/Plugins` ; page `torry` déclarée dans le menu d'administration (section `server`) ; HTML et JS servis |
| Autorisations | Sans jeton → 401 ; jeton invalide → 401 ; utilisateur non administrateur → 403 (statut, magnet, lecture et écriture des réglages) ; administrateur → 200 |
| Validation | Magnet invalide → 400 « Lien magnet invalide. » ; dossier « en cours » dans « terminés » → 400 avec message explicite ; `.torrent` en double → 400 |
| `.torrent` | Import (base64) → `AwaitingSelection` ; 3 fichiers listés ; sélection des 2 vidéos → téléchargement → partage |
| Fichiers | Vidéo dans `complete/Torry Test Movie (2024)/` ; `Extras/` conservé ; `notes.txt` (non sélectionné) absent de la bibliothèque ; aucun `.mkv` restant dans `incomplete` ; SHA-256 identique à la source |
| Bibliothèque | Film visible dans Emby (`/Items`, type Movie) **85 s** après la finalisation : délai `LibraryMonitorDelaySeconds` = 90 s par défaut |
| Redémarrage | `docker restart` d'Emby : journal « Torry arrêté, état enregistré. », puis tâche restaurée et **partage repris** |
| Tâches | Arrêt du partage → `Completed` ; suppression en conservant les fichiers ; pause → `Paused` ; reprise → partage |
| Magnet | Métadonnées obtenues via une **connexion entrante** sur le port 6881 du conteneur Emby ; téléchargement ; collision de nom → dossier `Torry Test Movie (2024) (2)` |
| Suppression des fichiers | Sans la bonne confirmation → 400 « Confirmation explicite requise… » ; avec confirmation → 3 fichiers effacés |
| Confidentialité | Aucun `magnet:` ni `xt=urn:btih` dans les journaux Emby (`/config/logs/*.txt`), y compris dans les URL de requêtes journalisées par Emby |

Deux premières exécutions ont échoué sur la partie magnet. Elles ont révélé :

1. un vrai problème : `DownloadMetadataAsync` de MonoTorrent refuse les connexions entrantes. Torry utilise
   désormais un gestionnaire public, arrêté dès réception des métadonnées. Un test unitaire couvre les pairs
   entrants et sortants ;
2. un artefact du banc de test : le seeder ne se reconnecte pas à un pair qu'il a vu en « seeder ». Il est
   désormais redémarré avant la phase magnet.

## 5. Vérifications manuelles complémentaires dans Emby 4.10.2

| Vérification | Méthode | Résultat |
|---|---|---|
| Interface dans un navigateur | Panneau navigateur, connexion administrateur | Entrée **Torry** dans le menu latéral d'administration (avec icône) ; page affichée ; onglets Téléchargements / Réglages |
| Choix des fichiers | Bouton « Choisir les fichiers », puis « Vidéos uniquement », puis « Démarrer le téléchargement » | 2 fichiers sur 3 cochés ; téléchargement terminé, dossier `(2)`, note de bibliothèque affichée |
| Suppression avec fichiers | Case cochée : bouton désactivé tant que « SUPPRIMER » n'est pas saisi | « Tâche supprimée, 3 fichier(s) effacé(s). » |
| Session expirée | Jeton d'un serveur recréé | Message « Session expirée : reconnectez-vous. » |
| Permissions insuffisantes | `/downloads/complete` passé en `root:root 755` (Emby tourne sous l'UID 2), puis « Enregistrer » | « Dossier « terminés » : Permissions insuffisantes : le processus Emby ne peut pas écrire… » |
| Espace insuffisant (contrôle préalable) | `/downloads` en tmpfs de 300 Kio, torrent de 708 Kio | Tâche en erreur : « Espace disque insuffisant : 708,5 Kio nécessaires, 300,0 Kio disponibles. » |
| Disque plein pendant l'écriture | tmpfs de 70 Mio rempli à 100 % après le démarrage | « Disque plein : libérez de l'espace puis reprenez le téléchargement. » ; après libération et « Reprendre », téléchargement terminé et finalisé |
| Dépendances natives | Analyse des assemblies fusionnées + exécution dans l'image Emby | Aucune : MonoTorrent est entièrement managé |
| Build reproductible | Deux builds successifs | SHA-256 identique : `d32e8b693ef5760de41b7a6932a0af6c7f40afec4da2522cbd9bb4ab33629a28` |

Note : cette empreinte est celle de la version vérifiée ce jour-là. La DLL publiée a ensuite été recompilée
(licence MIT, indicateur d'écoute du port), son empreinte a donc changé. `./build.sh` affiche celle de la
version courante.

## 5 bis. Indicateur d'écoute du port (ajouté ensuite)

Ajout effectué après qu'un test de port depuis une machine externe a échoué. MonoTorrent n'ouvre le port
qu'au démarrage du premier torrent actif. Vérifié dans `lscr.io/linuxserver/emby:4.10.2` (image du modèle
Coolify), en comparant `Engine.Listening` de l'API à `/proc/net/tcp` du conteneur :

| Situation | API `Listening` | Noyau (port 6881) | Interface |
|---|---|---|---|
| Aucune tâche | `false` | fermé | « fermé tant qu'aucun torrent n'est actif » |
| Magnet en recherche de métadonnées | `true` | en écoute | « en écoute » (après « Reprendre ») |
| Tâche en pause | `false` | fermé | « fermé tant qu'aucun torrent n'est actif » |

Le test d'intégration vérifie aussi qu'une connexion TCP au port aboutit pendant le partage.

## 6. Ce qui n'a PAS été vérifié

- **Applications natives Emby** (Android TV, Apple TV, Roku, téléviseurs, iOS, Android) : aucune n'a été
  testée. L'analyse du client web indique que la page y est masquée (liste blanche `restrictedplugins`).
- **Essaim public sur Internet** : aucun torrent public n'a été téléchargé. Le DHT, les trackers HTTP/UDP
  publics et l'accessibilité d'un port depuis Internet n'ont pas été éprouvés (tests hors ligne uniquement).
- **Coolify** : le `docker-compose.yml` fourni n'a pas été déployé dans une instance Coolify. Il a été rédigé
  à partir du compose utilisé en test.
- **Versions d'Emby** autres que 4.10.2.0, et architectures autres que Linux amd64 (ARM64 en particulier).
- **Gros volumes** : uniquement des contenus de quelques Mio. Ni les performances ni la vérification d'un
  fichier de plusieurs Go après un arrêt brutal n'ont été mesurées.
- **Changement de port à chaud** et UPnP / NAT-PMP : non testés.
- **Clé d'API serveur** : acceptée par conception (comportement d'Emby), mais non testée.
- **Arrêt brutal** (`kill -9`) d'Emby : non testé. Seul l'arrêt propre (`docker restart`) l'a été.
