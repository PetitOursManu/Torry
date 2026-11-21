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
Info App: Loading PetitOurs.Torry, Version=0.1.0.0 ... from /config/plugins/PetitOurs.Torry.dll
Info Torry: Torry démarré : 0 tâche(s), port BitTorrent 6881.
```

### 5. Ouvrir le port BitTorrent

Ouvrez **6881 en TCP et en UDP** dans le pare-feu du fournisseur (Hetzner, OVH, Scaleway…). Avec UFW :
`sudo ufw allow 6881/tcp && sudo ufw allow 6881/udp`. Docker contourne souvent UFW : testez depuis une
autre machine avec `nc -vz <ip-du-serveur> 6881`. Sans port entrant, Torry fonctionne quand même, mais plus
lentement (voir [DOCKER-COOLIFY.md](docs/DOCKER-COOLIFY.md#sans-port-entrant-accessible)).

### 6. Configurer Emby et Torry

1. Dans le **navigateur**, ouvrez votre domaine Emby et connectez-vous en administrateur.
2. **Bibliothèques** : ajoutez une bibliothèque (Films, par exemple) avec le dossier `/downloads/complete`.
   N'ajoutez jamais `/downloads/incomplete`.
3. Ouvrez **Torry** dans le menu d'administration (ou Plugins, puis Torry for Emby, puis Paramètres).
   Dans l'onglet **Réglages**, les valeurs par défaut conviennent : `/downloads/incomplete`,
   `/downloads/complete`, port 6881. Cliquez sur **Enregistrer** : Torry revérifie les dossiers, et les
   avertissements disparaissent si tout est correct.
4. Collez un lien magnet ou importez un `.torrent`, choisissez les fichiers, puis lancez le téléchargement.

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
