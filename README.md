# Torry for Emby

**Torry for Emby** est une extension qui télécharge des torrents directement sur le serveur Emby, avec un moteur
BitTorrent intégré ([MonoTorrent](https://github.com/alanmcgovern/monotorrent), MIT).

Vous collez un lien magnet ou importez un fichier `.torrent` depuis la page Torry de l'interface web d'Emby.
Le serveur télécharge le contenu, puis les vidéos apparaissent dans votre bibliothèque. **Aucun contenu ne
transite par votre ordinateur**, et aucune application intermédiaire n'est nécessaire (ni Transmission, ni
qBittorrent, ni Radarr ou Sonarr, ni service de débridage).

> Torry for Emby est une extension communautaire indépendante, **sans affiliation officielle avec Emby**.
> Torry ne fournit ni moteur de recherche ni catalogue : vous ajoutez uniquement des liens et fichiers dont
> vous disposez, sous votre responsabilité.

- Projet : `Torry` · Espace de noms : `PetitOurs.Torry` · Assembly : `PetitOurs.Torry.dll`
- Emby testé : **4.10.2.0** (Docker, Linux amd64, .NET 8.0.28)

## Fonctionnalités

- Ajout d'un lien magnet ou import d'un fichier `.torrent`.
- Récupération des métadonnées, puis **choix des fichiers avant tout téléchargement de contenu**
  (raccourci « Vidéos uniquement »).
- Suivi de chaque tâche : nom, état, progression, volume téléchargé, taille, débits, pairs, temps restant estimé,
  ratio.
- Pause, reprise, annulation (fichiers conservés), arrêt du partage.
- Suppression d'une tâche en **conservant ses fichiers par défaut**. Effacer aussi les fichiers demande une
  confirmation explicite : case à cocher, saisie de « SUPPRIMER », puis vérification côté serveur.
- Réglages : dossiers « en cours » et « terminés », port BitTorrent, DHT, limites de débit, téléchargements
  simultanés, connexions maximales.
- Partage après téléchargement, arrêté à un ratio et/ou après une durée cumulée.
- Persistance : les tâches, leur état, les compteurs et le cache de reprise MonoTorrent survivent au
  redémarrage d'Emby.
- Actualisation de la bibliothèque Emby qui contient le dossier, dès que les fichiers sont disponibles.
  Le bouton **« Actualiser la bibliothèque »** de l'onglet Téléchargements force une analyse immédiate du
  dossier des téléchargements terminés. Sans lui, Emby attend environ 90 s (délai de surveillance par défaut).
- **Dossiers de destination par type de contenu Emby** (Films, Séries, Musique, Livres…), importables en un clic
  depuis vos bibliothèques Emby. Au choix des fichiers, Torry propose la destination adaptée au contenu.
- **Onglet « Fichiers »** : mini explorateur pour parcourir, déplacer, renommer et créer des dossiers dans les
  téléchargements terminés et les destinations.

## Dossiers de destination et onglet « Fichiers »

**Préparer les bibliothèques (recommandé).** Dans **Réglages > Dossiers de destination**, le bouton
**« Préparer les bibliothèques »** règle tout en un clic, sans terminal :

1. pour chaque bibliothèque Emby, Torry crée son propre dossier dans `bibliotheques/`, à côté du dossier
   « en cours » (par exemple `/downloads/bibliotheques/Films`) ;
2. il ajoute ce dossier comme **emplacement supplémentaire** de la bibliothèque : Films regarde alors
   `/media/Films` **et** `/downloads/bibliotheques/Films` ;
3. il l'enregistre comme destination.

Torry tourne sous l'utilisateur d'Emby et ne peut pas changer les droits des dossiers qui ne lui
appartiennent pas, par exemple des dossiers de bibliothèque appartenant à `root` (« Permissions
insuffisantes »). Il crée donc les siens : ils ont toujours les bons droits et sont sur le même volume que les
téléchargements, donc **finalisation et déplacements instantanés**. Vos dossiers existants ne sont pas
modifiés. Les fichiers déjà présents dans un dossier appartenant à `root` restent visibles dans Emby, mais
Torry ne peut pas les déplacer. L'opération peut être relancée sans risque : rien n'est ajouté deux fois.
Pour annuler, retirez l'emplacement dans les réglages de la bibliothèque, côté Emby.

**Destinations manuelles.** Vous pouvez aussi déclarer chaque dossier vous-même : nom, type (Films, Séries,
Musique, Clips musicaux, Vidéos personnelles, Photos, Livres, Livres audio, Mixte) et chemin.
**« Importer les bibliothèques Emby »** préremplit la liste avec les emplacements existants, qui doivent alors
être accessibles en écriture par Emby.

**Au choix des fichiers**, une liste « Destination » propose automatiquement :

| Fichiers sélectionnés | Destination proposée |
|---|---|
| Vidéos dont les noms contiennent `S01E02`, `1x02`, « Saison 1 »… | Séries |
| Autres vidéos | Films |
| Audio (`.mp3`, `.flac`…) | Musique (`.m4b` : Livres audio) |
| Images | Photos |
| `.epub`, `.pdf`, `.cbz`… | Livres |

Si aucune destination ne correspond, les fichiers vont dans `complete/`. Le torrent est alors finalisé
directement dans `<destination>/<Nom du torrent>/` : il n'y a pas d'étape intermédiaire.

**Onglet « Fichiers ».** Un explorateur limité au dossier des téléchargements terminés et aux destinations :

- naviguer dans les dossiers et sous-dossiers : un clic sur la ligne d'un dossier l'ouvre ; « Dossier
  parent » et le fil d'Ariane permettent de remonter ;
- créer un dossier, **renommer**, **déplacer** un fichier ou un dossier ;
- **sélection multiple** : cochez plusieurs fichiers ou dossiers (ou « Tout sélectionner »), puis
  « Déplacer les N éléments… ». La destination est proposée d'après l'ensemble de la sélection, et un
  déplacement est lancé par élément ;
- la fenêtre de déplacement **analyse le contenu** (« Dossier · 12 fichiers dont 12 audio · type détecté :
  Musique »), présélectionne la destination conseillée et signale les bibliothèques d'un autre type. Elle
  procède en trois étapes (bibliothèque, sous-dossier facultatif, nom), puis affiche le résultat
  (« Sera déplacé dans : Musique ») ;
- les déplacements s'exécutent **en arrière-plan, un par un**, avec leur progression. Sur un même volume, c'est
  un renommage instantané. Entre deux volumes Docker, c'est une copie suivie de la suppression de l'original ;
  en cas d'échec, la copie partielle est supprimée et l'original reste intact ;
- rien n'est jamais écrasé : en cas de collision, `Nom (2)`, `Nom (3)`… ;
- la bibliothèque d'origine et celle d'arrivée sont actualisées tout de suite ;
- un fichier encore **en partage** peut être déplacé : son partage est alors arrêté, et la tâche suit le dossier
  déplacé (la suppression avec fichiers reste possible) ;
- l'explorateur ne supprime pas de fichiers, ne suit pas les liens symboliques et refuse tout chemin hors des
  racines (`..`, chemins absolus).

> Pour des déplacements instantanés, les dossiers doivent être sur **le même montage Docker**. Par exemple,
> montez un seul dossier hôte `/srv/media` sur `/media`, avec `/media/downloads/incomplete`,
> `/media/downloads/complete`, `/media/films` et `/media/series`. Deux montages distincts imposent une copie,
> même s'ils sont sur le même disque.

## Comment les fichiers sont gérés

```
/downloads/incomplete/<id-tâche>/…structure du torrent…   ← téléchargement (hors bibliothèque)
                │  fin du téléchargement des fichiers choisis :
                │  déplacement par le moteur (MoveFileAsync), simple renommage sur un même volume
                ▼
/downloads/complete/<Nom du torrent>/…structure du torrent… ← bibliothèque Emby ; le partage continue d'ici
```

- Les fichiers incomplets ne sont **jamais** dans le dossier « terminés ». Torry refuse aussi un dossier
  « en cours » placé à l'intérieur du dossier « terminés ».
- La structure des torrents multi-fichiers est conservée.
- Les fichiers ne sont ni copiés ni dupliqués : le moteur les déplace, puis continue de partager depuis leur
  emplacement final.
- Les collisions de noms ne provoquent aucun écrasement : `Film`, puis `Film (2)`, `Film (3)`…
- Tout chemin qui sortirait des dossiers autorisés est refusé : chemin absolu, `..`, caractères de contrôle,
  noms de torrent malveillants, liens symboliques lors des suppressions.
- Torry signale clairement les erreurs de permissions, de disque plein et de réseau (en français, sans
  URL de tracker).

## Sécurité

- **Réservé aux administrateurs Emby.** Chaque route exige un jeton Emby valide et le rôle `admin`, et le
  service vérifie à nouveau ce rôle. Aucune route n'est anonyme.
- Les liens magnet et les `.torrent` ne passent que dans le corps JSON des requêtes POST. Emby journalise les
  URL, pas les corps. Torry ne journalise jamais un lien magnet ni une URL de tracker, et masque les URL dans
  les messages d'erreur.
- Toutes les chaînes issues des torrents sont échappées dans l'interface (aucune injection HTML).
- L'état des tâches est stocké en mode 600 dans `/config/data/torry/`.
- Le moteur démarre en arrière-plan : il ne retarde pas Emby. À l'arrêt, il s'arrête proprement et enregistre
  son état.

## Installation sur un Emby déployé avec Coolify

Ce guide part du service **Emby** proposé par Coolify (« + New » > « Service » > « Emby »). Ce modèle
utilise l'image `lscr.io/linuxserver/emby:4.10.2`. Emby y tourne sous l'utilisateur `abc` (UID/GID 1000,
fixé par `PUID`/`PGID`) avec la configuration dans `/config`. Le modèle ne publie aucun port : Coolify sert
l'interface web par son proxy.

> Vérifié : Torry se charge et démarre dans `lscr.io/linuxserver/emby:4.10.2`. Les plugins vont dans
> `/config/plugins`, et le runtime est .NET 8.0.28.
> Non vérifié : le déploiement dans une instance Coolify réelle. Les étapes ci-dessous en découlent.

Toutes les commandes se lancent **en SSH sur le serveur Coolify**.

### 1. Créer le dossier de téléchargement sur le serveur

```sh
sudo mkdir -p /srv/torry/incomplete /srv/torry/complete
sudo chown -R 1000:1000 /srv/torry
```

Le `chown` est indispensable : sans lui, Emby (UID 1000) ne peut pas écrire, et Torry affiche « Permissions
insuffisantes ». Un volume nommé créé par Docker appartient à `root` : c'est pourquoi on utilise ici un
dossier de l'hôte.

### 2. Modifier le compose du service dans Coolify

Dans Coolify, ouvrez le service Emby, puis **« Edit Compose File »**. Ajoutez la ligne de volume `/downloads`
et le bloc `ports`, sans toucher au reste :

```yaml
services:
  emby:
    image: lscr.io/linuxserver/emby:4.10.2
    environment:
      - SERVICE_URL_EMBY_8096
      - PUID=1000
      - PGID=1000
      - TZ=Europe/Paris
    volumes:
      - emby-config:/config
      - emby-tvshows:/tvshows
      - emby-movies:/movies
      - /srv/torry:/downloads          # ← ajout Torry : incomplete/ et complete/
    ports:                             # ← ajout Torry : port BitTorrent, distinct du web
      - "6881:6881/tcp"
      - "6881:6881/udp"
    # healthcheck : inchangé
```

- Montez `/srv/torry` **en entier** sur `/downloads`. Ne montez pas `incomplete` et `complete` séparément :
  le déplacement final reste alors un renommage instantané, sans copie.
- Le domaine Coolify ne transporte que le web (port 8096). Le port BitTorrent doit être publié ici, et il
  n'est **pas** transporté par le proxy ni par un tunnel Cloudflare.

Enregistrez, puis cliquez sur **Redeploy**.

### 3. Copier le plugin dans le conteneur

```sh
# Télécharger la DLL
curl -L -o PetitOurs.Torry.dll https://github.com/PetitOursManu/Torry/raw/main/artifacts/PetitOurs.Torry.dll

# Trouver le conteneur Emby de Coolify (nom du type emby-<identifiant>)
docker ps --format '{{.Names}}  {{.Image}}' | grep -i emby
CONTAINER=emby-xxxxxxxxxxxx          # ← remplacez par le nom affiché

# Copier la DLL dans /config/plugins (volume persistant) et la donner à l'utilisateur abc
docker cp PetitOurs.Torry.dll "$CONTAINER":/config/plugins/
docker exec "$CONTAINER" chown abc:abc /config/plugins/PetitOurs.Torry.dll
```

La DLL se trouve dans le volume `/config` : elle survit aux redéploiements et aux mises à jour de l'image.

### 4. Redémarrer Emby et vérifier

Redémarrez le service depuis Coolify (**Restart**), puis :

```sh
docker exec "$CONTAINER" grep -i "torry" /config/logs/embyserver.txt | tail -3
```

Vous devez voir :

```
Info App: Loading PetitOurs.Torry, Version=0.3.4.0 ... from /config/plugins/PetitOurs.Torry.dll
Info Torry: Torry démarré : 0 tâche(s), port BitTorrent 6881.
```

### 5. Ouvrir le port BitTorrent

Ouvrez **6881 en TCP et en UDP** dans le pare-feu du fournisseur (Hetzner, OVH, Scaleway…). Avec UFW :
`sudo ufw allow 6881/tcp && sudo ufw allow 6881/udp`. Coolify n'a pas de pare-feu à configurer : les
`ports` du compose sont publiés directement par Docker. Sans port entrant, Torry fonctionne quand même, mais
plus lentement (voir [DOCKER-COOLIFY.md](docs/DOCKER-COOLIFY.md#sans-port-entrant-accessible)).

Le test du port se fait **à l'étape 6, une fois un torrent actif** (voir [Tester le port](#tester-le-port-bittorrent)) :
Torry n'écoute sur 6881 que pendant qu'un torrent est en téléchargement ou en partage.

### 6. Configurer Emby et Torry

1. Dans le **navigateur**, ouvrez votre domaine Emby et connectez-vous en administrateur.
2. **Bibliothèques** : ajoutez une bibliothèque (Films, par exemple) avec le dossier `/downloads/complete`.
   N'ajoutez jamais `/downloads/incomplete`.
3. Ouvrez **Torry** dans le menu d'administration (ou Plugins, puis Torry for Emby, puis Paramètres).
   Dans l'onglet **Réglages**, les valeurs par défaut conviennent : `/downloads/incomplete`,
   `/downloads/complete`, port 6881. Cliquez sur **Enregistrer** : Torry revérifie les dossiers, et les
   avertissements disparaissent si tout est correct.
4. Dans **Réglages > Dossiers de destination**, cliquez sur **« Préparer les bibliothèques »**. Torry crée
   `/downloads/bibliotheques/<Bibliothèque>` et l'ajoute à chaque bibliothèque Emby : ni `chown`, ni copie
   entre volumes. Voir [Dossiers de destination](#dossiers-de-destination-et-onglet-fichiers).
5. Collez un lien magnet ou importez un `.torrent`, choisissez les fichiers et la destination, puis lancez
   le téléchargement.
6. Pendant que ce torrent est actif, [testez le port](#tester-le-port-bittorrent).

### Tester le port BitTorrent

Le moteur BitTorrent (MonoTorrent) n'ouvre le port qu'au démarrage du premier torrent actif, et le referme
quand plus aucun ne l'est. **Avec une liste vide, le test échoue forcément**, même si tout est bien configuré.
La page Torry indique l'état en direct : « Port BitTorrent 6881 : en écoute » ou « fermé tant qu'aucun
torrent n'est actif ».

1. Ajoutez un torrent et démarrez-le. Attendez que Torry affiche « en écoute ».
2. Testez depuis **une autre machine** que le serveur :
   - Windows (PowerShell) : `Test-NetConnection <ip-du-serveur> -Port 6881`, qui doit afficher
     `TcpTestSucceeded : True`. Par défaut, `nc` n'existe pas sous Windows.
   - Linux / macOS : `nc -vz <ip-du-serveur> 6881`.

   Ces tests ne vérifient que le TCP : l'UDP, utilisé par le DHT, ne se teste pas aussi simplement.
3. En cas d'échec, l'erreur indique la piste :
   - **refus immédiat** (« connection refused ») : le port atteint bien le serveur, mais rien n'écoute.
     Vérifiez que Torry affiche « en écoute » et que le port est publié :
     `docker ps --format '{{.Names}}  {{.Ports}}' | grep -i emby` doit montrer `0.0.0.0:6881->6881/tcp`
     et `0.0.0.0:6881->6881/udp`. Sinon, cliquez sur **Redeploy** dans Coolify.
   - **délai dépassé** (« timed out ») : un pare-feu bloque encore, chez l'hébergeur (`ufw status`,
     `iptables -L -n`) ou avec une règle qui ne s'applique pas à ce serveur.

### Mise à jour et désinstallation (Coolify)

- **Mise à jour** : refaites l'étape 3 avec la nouvelle DLL, puis **Restart**.
- **Désinstallation** :
  1. `docker exec "$CONTAINER" rm /config/plugins/PetitOurs.Torry.dll`
  2. Facultatif : `docker exec "$CONTAINER" rm -rf /config/data/torry /config/plugins/configurations/PetitOurs.Torry.xml`
  3. **Restart**, puis retirez le volume `/downloads` et les `ports` du compose si vous n'en voulez plus.

  Les fichiers de `/srv/torry` ne sont jamais supprimés automatiquement.

## Documentation

- [Installation, mise à jour, désinstallation](docs/INSTALLATION.md)
- [Docker / Coolify, volumes, ports et pare-feu](docs/DOCKER-COOLIFY.md)
- [Compatibilité, navigateur et applications natives, limites connues](docs/COMPATIBILITE-ET-LIMITES.md)
- [Compte rendu des vérifications exécutées](docs/VERIFICATIONS.md)
- [Composants tiers et licences](THIRD-PARTY-NOTICES.md)
- Exemple : [`deploy/docker-compose.yml`](deploy/docker-compose.yml)

## Compiler

> **À chaque nouvelle version publiée, augmentez `Version`, `AssemblyVersion` et `FileVersion` dans
> `Directory.Build.props`.** Emby calcule l'ETag des pages de plugin à partir de la seule version du plugin
> (`MD5(plugin.Version)`, vérifié dans Emby 4.10.2) : si la version ne change pas, les navigateurs gardent
> l'ancienne page en cache, même après le remplacement de la DLL.

Docker suffit. Le build utilise l'image SDK .NET 8.0.428, épinglée par empreinte, si aucun SDK local n'est
présent :

```sh
./build.sh          # → artifacts/PetitOurs.Torry.dll (MonoTorrent fusionné, DLL unique)
./build.sh --test   # + tests unitaires et d'intégration (essaim BitTorrent local)
eng/verify-emby.sh  # vérification de bout en bout dans un vrai Emby 4.10.2 (Docker, hors ligne)
```

## Architecture

| Dossier | Rôle | Dépend d'Emby ? |
|---|---|---|
| `src/PetitOurs.Torry/Engine` | Moteur torrent : hôte MonoTorrent, orchestrateur des tâches, validation des torrents | Non |
| `src/PetitOurs.Torry/Persistence` | Enregistrements de tâches et stockage JSON atomique | Non |
| `src/PetitOurs.Torry/Files` | Garde de chemins, opérations disque, erreurs disque | Non |
| `src/PetitOurs.Torry/Core` | Réglages, vues renvoyées à l'interface, abstractions (journal, bibliothèque) | Non |
| `src/PetitOurs.Torry/Api` | Routes REST et autorisations | Oui |
| `src/PetitOurs.Torry/Emby` | Point d'entrée (démarrage et arrêt), journal, actualisation de la bibliothèque | Oui |
| `src/PetitOurs.Torry/Web` | Page HTML et contrôleur JS (navigateur) | — |
| `tests/` | Tests xUnit, dont un essaim BitTorrent local réel | — |
| `eng/` | Build, vérification Emby de bout en bout, seeder de test | — |

## Licence

Torry for Emby est distribué sous licence [MIT](LICENSE). Copyright (c) 2026 PetitOurs.

La DLL publiée contient aussi MonoTorrent, ReusableTasks et Mono.Nat, tous sous licence MIT. Leurs mentions
de copyright figurent dans [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) et doivent accompagner toute
redistribution de la DLL.
