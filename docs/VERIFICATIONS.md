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

## 5 nonies. Corbeille (versions 0.4.0 et 0.4.1)

- Code d'Emby 4.10.2 : `CoreResolutionIgnoreRule` ignore sous Linux tout nom commençant par « . », ce qui
  justifie le dossier caché `.torry-corbeille/`.
- Tests automatisés (94 au total) : mise à la corbeille, corbeille absente de la liste et inaccessible par
  chemin, index (origine, nombre de fichiers, taille, échéance), restauration avec collision → `Album (2)`,
  confirmation obligatoire pour l'effacement définitif, vidage automatique d'un élément expiré, refus de
  « Vider » sans confirmation.
- Dans `lscr.io/linuxserver/emby:4.10.2`, avec une bibliothèque Films :

| Vérification | Résultat |
|---|---|
| Fenêtre « Supprimer » | Action « Mettre à la corbeille (restaurable) » par défaut, durée de 30 jours affichée, aucune saisie requise |
| Mise à la corbeille d'un film de la bibliothèque | Dossier déplacé dans `/downloads/films/.torry-corbeille/<id>/` |
| Analyse **complète** de la bibliothèque Emby ensuite | Seul le dossier racine « films » est indexé : le film en corbeille n'apparaît pas |
| Vue Corbeille | « 1 élément(s) · 41,9 Kio · vidage automatique après 30 jour(s) », origine « Films › Film Corbeille (2020) », dates |
| Restaurer | Fichier remis à sa place, film de nouveau visible dans Emby en 2 s |
| Correction (0.4.1) | La barre de sélection restait visible au-dessus de la corbeille, car le CSS `display:flex` l'emportait sur `hidden` ; règle `[hidden] { display:none !important }` ajoutée |

## 5 octies. Suppression depuis l'explorateur (version 0.3.5)

**Tests automatisés** (93 au total) : suppression d'un fichier puis d'un dossier, refus sur une racine, un
élément absent ou `..`. Un lien symbolique placé dans un dossier supprimé est retiré sans toucher sa cible, et
un chemin qui traverse un lien est refusé. Essaim local : suppression du dossier d'une tâche en partage, qui
arrête le partage (3 fichiers effacés) et détache les fichiers de la tâche. Une confirmation autre que
« SUPPRIMER » est refusée.

**Dans `lscr.io/linuxserver/emby:4.10.2`** :

| Vérification | Résultat |
|---|---|
| `POST /Torry/Files/Delete` sans jeton / non administrateur | 401 / 403 |
| Confirmation « oui » | 400 « Confirmation explicite requise : saisissez « SUPPRIMER ». » |
| Racine / `../precieux.txt` | 400 « Impossible de supprimer un dossier racine. » / 400 « Chemin refusé (remontée de dossier (..)) » ; `precieux.txt` intact |
| Interface, une ligne | Fenêtre « 1 fichier(s) · 293 Kio seront effacés », bouton désactivé tant que « SUPPRIMER » n'est pas saisi, puis « Supprimé (1 fichier(s)) » et liste actualisée |
| Interface, sélection de 2 dossiers | « Supprimer 2 éléments définitivement · 2 fichier(s) · 585,9 Kio », 2 suppressions terminées, liste actualisée |

## 5 septies. Navigation et sélection multiple (versions 0.3.3 et 0.3.4)

Dans `lscr.io/linuxserver/emby:4.10.2`, avec des albums imbriqués (`Anastacia…/CD1`, `CD2`…) et la
bibliothèque Music préparée :

| Vérification | Résultat |
|---|---|
| Clic sur la ligne d'un dossier | Ouverture de `Anastacia…`, puis de `CD1` (icônes audio, tailles) ; fil d'Ariane complet |
| Fil d'Ariane et « Dossier parent » | Retour à la racine |
| Sélection de 2 dossiers | Lignes surlignées, « Tout sélectionner » partiellement coché, bouton « Déplacer les 2 éléments… » |
| Fenêtre pour 2 éléments | « 2 éléments · 4 fichier(s) dont 4 audio · type détecté : Musique », Music conseillé, liste des éléments, pas de champ « Nom » |
| Déplacement groupé | 2 opérations terminées, « Bibliothèque actualisée » |
| Rafraîchissement de la liste | Les dossiers déplacés disparaissent tout seuls, ce qui corrige le défaut signalé : un déplacement plus rapide que le rafraîchissement laissait l'élément affiché |
| Barre de sélection (0.3.4) | Hauteur constante : en 0.3.3, cocher une case ajoutait une ligne, décalait la liste et faisait ouvrir un dossier au clic suivant |

## 5 sexies. « Préparer les bibliothèques » (version 0.3.2)

Reproduction du cas réel dans `lscr.io/linuxserver/emby:4.10.2` : les bibliothèques Films (`/media/Films`) et
Music 2 (`/musics`) sont sur des volumes appartenant à `root` (mode 755), et `/downloads` appartient à
`abc` (UID 1000).

| Vérification | Résultat |
|---|---|
| Enregistrer une destination `/media/Films` | HTTP 400 « Permissions insuffisantes » : erreur de l'utilisateur reproduite |
| Clic sur « Préparer les bibliothèques » | « 2 bibliothèque(s) prête(s) », destinations Films → `/downloads/bibliotheques/Films`, Music 2 → `/downloads/bibliotheques/Music 2`, persistées dans le XML |
| Côté Emby (`/Library/VirtualFolders`) | Films : `/downloads/bibliotheques/Films` + `/media/Films` ; Music 2 : `/downloads/bibliotheques/Music 2` + `/musics` |
| Droits | `/downloads/bibliotheques/Films` appartient à `abc` |
| Déplacement d'un film de `complete/` vers Films | Renommage instantané (pas de copie), « Bibliothèque actualisée » |
| Bibliothèque Films dans Emby | « Ancien Film » (`/media/Films/…`) et « Nouveau Film » (`/downloads/bibliotheques/Films/…`) |

La confirmation (fenêtre native `window.confirm`) a été acceptée automatiquement par script : le panneau de
navigateur de test ne permet pas de cliquer sur les fenêtres natives. Tout le reste a été fait par de vrais
clics. Test automatisé : création, rattachement, reprise de la destination « Films » qui échouait, retrait
d'une destination inaccessible, et relance sans doublon (90 tests au total).

## 5 quinquies. Fenêtre de déplacement repensée (version 0.3.1)

- Analyse du contenu (`GET /Torry/Files/Analyze`), dans `lscr.io/linuxserver/emby:4.10.2` : un album FLAC
  (2 `.flac` et un `.jpg`) donne « audio », type « Musique » et la destination conseillée `musique`. La liste
  interne des fichiers n'est pas exposée.
- Interface : résumé « Dossier · 3 fichier(s) dont 2 audio, 1 image · type détecté : Musique »,
  « Musique — conseillé » présélectionné, « Sera déplacé dans : Musique », bouton « Déplacer dans
  « Musique » », puis déplacement effectué.
- Correction d'affichage : le menu latéral d'Emby (z-index 1099) passait au-dessus des fenêtres de Torry,
  désormais en z-index 1200.
- Tests : album → Musique, dossier « Saison 1 » → Séries, fichier seul → Films ; analyse dans le test
  d'essaim (89 tests au total).

## 5 quater. Destinations et explorateur de fichiers (version 0.3.0)

**Tests automatisés** (88 au total, tous réussis) :
- détection du type de contenu : films, séries (`S01E02`, « Saison 1 »), musique, livres audio, livres ;
  destination conseillée ;
- validation des destinations : doublons, type inconnu, destination dans « en cours » refusée, identifiant
  dangereux refusé ;
- explorateur : `..` refusé, chemin absolu ramené à la racine, liens symboliques masqués et refusés, dossier
  déplacé dans lui-même refusé, collisions `(2)` ;
- déplacement d'un dossier entre deux systèmes de fichiers réellement distincts (`/dev/shm` tmpfs → overlay
  `/tmp` du conteneur SDK) : copie avec progression, SHA-256 identique, original supprimé ;
- essaim local réel : torrent finalisé directement dans la destination « Films », puis dossier déplacé vers
  « Archives » pendant le partage. Le partage s'arrête, la tâche suit le dossier, la bibliothèque est
  notifiée, et la suppression avec fichiers efface bien le nouvel emplacement.

**Dans `lscr.io/linuxserver/emby:4.10.2`** (image du modèle Coolify), avec les bibliothèques Films
(`/media/films`) et Séries (`/media/series`) sur un volume Docker, et `/downloads` en tmpfs :

| Vérification | Résultat |
|---|---|
| `GET /Torry/Libraries` | Films (movies) et Séries (tvshows) avec leurs emplacements |
| Enregistrement des destinations | Persistées dans `PetitOurs.Torry.xml`. Un accent saisi dans l'interface est stocké en UTF-8 correct |
| Racines dans le statut | Téléchargements terminés, Films et Séries, toutes disponibles |
| Déplacement `/downloads/complete` → `/media/films` (volumes différents) | Copie avec progression (52 618 / 52 618 octets), SHA-256 identique, original supprimé, film visible dans Emby |
| Traversée `..` et racine inconnue | HTTP 400, « Chemin refusé (remontée de dossier (..)) » |
| Nouvelles routes sans jeton / non administrateur | 401 / 403 pour `Files`, `Files/Operations`, `Libraries`, `Files/Move`, `Files/Folder` |
| Interface : onglet Fichiers | Navigation, icônes, fenêtre de déplacement avec destinations compatibles, déplacement vers « Films › A trier », liste actualisée |
| Interface : choix des fichiers d'un torrent `Saison 1/… S01E01.mkv` | Destination « Séries » présélectionnée |
| Redémarrage d'Emby | Tâche et destinations conservées |
| Problème trouvé | DLL montée dans un conteneur neuf : `/config/plugins` appartient à `root`, et l'enregistrement des réglages échoue (HTTP 500). Torry affiche maintenant un message explicite ; documenté |

## 5 ter. Bouton « Actualiser la bibliothèque » (version 0.2.0)

Vérifié dans `lscr.io/linuxserver/emby:4.10.2` (image du modèle Coolify), avec une bibliothèque Films sur
`/downloads/complete` :

| Vérification | Résultat |
|---|---|
| Sans bibliothèque couvrant le dossier | `Refreshed: false`, « Aucune bibliothèque Emby ne contient le dossier… » |
| Vidéo déposée à la main, puis appel de l'API | Film visible dans Emby **5 s** après l'appel (contre environ 90 s via la surveillance d'Emby) |
| Vidéo déposée, puis clic sur le bouton dans l'interface | Message « Actualisation lancée : Films… » ; film visible **17 s** après le dépôt, temps du clic compris |
| Cache du navigateur | Avec une version inchangée (0.1.0.0), l'ancienne page restait affichée : ETag Emby = `MD5(version du plugin)`, réponse 304. Après passage en 0.2.0.0, la nouvelle page est servie sans vider le cache |

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
