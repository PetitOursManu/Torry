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
