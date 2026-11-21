# Compatibilité, interface et limites connues

## Versions d'Emby

| Version d'Emby | Runtime | Statut |
|---|---|---|
| **4.10.2.0** (Docker `emby/embyserver:4.10.2.0`, Linux amd64) | .NET 8.0.28 | **Testée** : chargement, API, téléchargement, bibliothèque, redémarrage (voir [VERIFICATIONS.md](VERIFICATIONS.md)) |
| 4.9.1.80 à 4.10.x | .NET 8 | Devrait fonctionner (même runtime, même API de plugin). **Non testée.** |
| 4.11 bêta | .NET 8 (à confirmer) | Non testée |
| < 4.9.1.80 | runtime .NET antérieur | **Non compatible** : la DLL cible `net8.0` |

- La DLL est compilée contre `MediaBrowser.Server.Core` **4.9.1.90**, la dernière version stable sur NuGet au
  21/11/2026. Elle a été exécutée dans Emby 4.10.2.0.
- MonoTorrent est entièrement managé, sans aucune dépendance native. L'architecture n'a donc pas d'incidence
  en théorie, mais seul Linux amd64 a été testé. ARM64 (Raspberry Pi, etc.) n'a pas été vérifié.

## Ce qui est vérifié dans le SDK et le serveur Emby

Toutes les vérifications ont porté sur Emby 4.10.2, par décompilation des assemblies et lecture du client web.

- **Chargement des plugins** : Emby charge chaque `*.dll` de `plugins/` avec `Assembly.Load(byte[])`, sans
  résolveur de dépendances. Des DLL MonoTorrent posées à côté du plugin provoquent `Could not load file or
  assembly 'MonoTorrent'` (erreur reproduite). Les dépendances sont donc fusionnées (ILRepack) dans une DLL
  unique.
- **Authentification** : `[Authenticated(Roles = "admin")]` lève une `SecurityException` si l'utilisateur
  n'est pas administrateur (HTTP 403), et une erreur 401 sans jeton valide.
  **Exception d'Emby** : les clés d'API serveur (sans utilisateur associé) ne sont pas soumises aux rôles.
  Emby les traite comme un accès complet, et Torry fait de même.
- **Interface** : `IHasWebPages` / `PluginPageInfo`. `EnableInMainMenu = true` avec `MenuSection = "server"`
  ajoute l'entrée au menu d'administration du serveur. `IsMainConfigPage = true` relie la page au bouton
  « Paramètres » du plugin. Le contrôleur JS est chargé via `data-controller="__plugin/torryjs"`, selon le
  même modèle que les plugins livrés avec Emby (MBBackup).
- **Bibliothèque** : `ILibraryMonitor.ReportFileSystemChanged(dossier)` demande l'analyse du seul dossier
  ajouté, dans la bibliothèque qui le contient.

## Navigateur ou applications natives

| Client | Page Torry | Détails |
|---|---|---|
| **Navigateur** (ordinateur, tablette, téléphone), interface web d'Emby | **Oui** | Menu d'administration, entrée « Torry » ; ou Plugins, puis Torry for Emby, puis Paramètres. Compte administrateur requis. |
| Applications natives (Android TV, Apple TV, Roku, téléviseurs, consoles, iOS, Android) | **Non prévu, non garanti** | Voir ci-dessous. |

Torry ne propose **aucun bouton dans les applications TV ou mobiles**. Plusieurs raisons :

1. Emby n'offre aucune API de plugin pour ajouter un bouton ou un écran aux lecteurs natifs. Ces applications
   ne sont pas modifiables (hors périmètre).
2. Le client web contient une **liste blanche** de plugins autorisés (`allowedPluginConfigs` dans
   `modules/common/pluginmanager.js`), appliquée quand l'application hôte déclare la capacité
   `restrictedplugins`. Torry n'y figure pas : sa page est donc masquée dans ces applications.
   Nous n'avons pas pu vérifier quelles applications déclarent cette capacité.
3. Les applications qui embarquent l'interface web complète peuvent afficher la page. Cela n'a pas été
   testé et n'est pas garanti.

**Recommandation** : depuis un téléphone ou une tablette, utilisez le **navigateur** à l'adresse web de votre
serveur Emby, pas l'application.

## Limites connues du MVP

- **Pas de lecture pendant le téléchargement**, pas de recherche de contenus, pas de VPN : hors périmètre.
- **Sélection des fichiers** : uniquement avant le démarrage. Pour modifier la sélection ensuite, supprimez la
  tâche en gardant les fichiers, puis ajoutez à nouveau le torrent.
- **Fragments de fichiers non sélectionnés** : une pièce BitTorrent peut chevaucher deux fichiers. Les morceaux
  de fichiers non sélectionnés restent alors dans `incomplete/<id>/`, hors bibliothèque. Ils disparaissent
  quand la tâche est supprimée avec ses fichiers.
- **Fichiers partiels** : le suffixe `.!mt` de MonoTorrent est désactivé, à cause d'un défaut de
  `MoveFileAsync` dans MonoTorrent 3.0.2. Les fichiers incomplets restent visibles sous leur nom final, mais
  uniquement dans `incomplete/`, jamais dans une bibliothèque.
- **Changement de dossiers** : refusé tant que des tâches sont actives (téléchargement, partage, file
  d'attente). Mettez-les d'abord en pause.
- **Port d'écoute ouvert à la demande** : MonoTorrent n'ouvre le port BitTorrent (TCP et UDP) qu'au
  démarrage du premier torrent actif, et le referme quand plus aucun ne l'est. Un test de port avec une
  liste vide échoue donc toujours. La page Torry affiche l'état (« en écoute » ou « fermé »).
- **Changement de port** : appliqué à chaud via MonoTorrent (non testé). N'oubliez pas de modifier aussi la
  publication Docker et le pare-feu. En cas de doute, redémarrez Emby.
- **Métadonnées d'un magnet** : Torry ajoute le lien au moteur pour accepter aussi les pairs entrants, puis
  l'arrête dès réception des métadonnées, avant tout téléchargement de contenu. En test, 0 octet a été écrit
  avant la sélection. Si un pair envoyait des données dans l'intervalle, elles resteraient dans
  `incomplete/<id>/`, hors bibliothèque.
- **Ratio** : il est calculé sur la taille des fichiers sélectionnés (envoyé ÷ taille). Il est mémorisé
  entre les redémarrages.
- **Arrêt brutal** (kill, coupure de courant) : sans arrêt propre, MonoTorrent revérifie les données au
  redémarrage (hash check), ce qui peut prendre du temps sur de gros fichiers. Aucune donnée n'est perdue.
- **Lien magnet sans pair joignable** : la récupération des métadonnées attend indéfiniment. Utilisez
  « Annuler » ; la tâche reste reprenable.
- **Trackers privés** : le DHT et l'échange de pairs sont désactivés automatiquement (drapeau `private`).
  Le lien magnet et le `.torrent`, qui peuvent contenir une passkey, sont stockés dans
  `/config/data/torry/` en mode 600. Ils ne sont jamais journalisés ni renvoyés par l'API.
- **Liens symboliques** : Torry ne suit jamais un lien symbolique lors d'une suppression. Un lien placé à la
  main dans les dossiers de téléchargement n'est pas résolu lors des écritures du moteur. Ne créez pas de
  liens symboliques dans `incomplete/`.
- **Mises à jour en direct** : l'interface interroge le serveur toutes les 2 secondes tant que la page est
  ouverte. Il n'y a pas de notifications.
- **Langue** : l'interface est en français uniquement.
