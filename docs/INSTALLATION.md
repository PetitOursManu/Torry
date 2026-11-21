# Installation, mise à jour et désinstallation

Torry for Emby s'installe manuellement : il ne passe pas par le catalogue de plugins d'Emby.

## Prérequis

- Emby Server **4.9.1.80 ou plus récent** (runtime .NET 8). Seule la version **4.10.2.0** (image Docker
  `emby/embyserver:4.10.2.0`, Linux amd64) a été testée. Voir [COMPATIBILITE-ET-LIMITES.md](COMPATIBILITE-ET-LIMITES.md).
- Un compte **administrateur** Emby.
- Un dossier de téléchargement accessible en écriture par l'utilisateur qui exécute Emby.

## 1. Obtenir la DLL

Torry tient en **un seul fichier** : `PetitOurs.Torry.dll` (environ 1 Mo). MonoTorrent et ses dépendances y sont
fusionnés, car Emby ne résout pas les DLL de dépendances placées à côté d'un plugin (vérifié dans Emby 4.10.2).

Pour la compiler vous-même (Docker suffit, aucun SDK .NET local n'est nécessaire) :

```sh
./build.sh            # produit artifacts/PetitOurs.Torry.dll
./build.sh --test     # compile et exécute les tests
```

## 2. Préparer les dossiers (Docker)

Montez **un seul volume** sur `/downloads`, avec deux sous-dossiers :

```sh
sudo mkdir -p /srv/torry/incomplete /srv/torry/complete
sudo chown -R 1000:1000 /srv/torry        # UID/GID utilisés par le conteneur Emby
```

- `/downloads/incomplete` : téléchargements en cours. **Ne l'ajoutez à aucune bibliothèque.**
- `/downloads/complete` : fichiers terminés. **Ajoutez-le à une bibliothèque Emby** (Films, Séries…).

Les deux sous-dossiers doivent être sur le même montage : la finalisation est alors un renommage instantané.
Deux montages Docker distincts, même sur le même disque, obligent à copier puis supprimer les fichiers.

Exemple complet : [`deploy/docker-compose.yml`](../deploy/docker-compose.yml) et [DOCKER-COOLIFY.md](DOCKER-COOLIFY.md).

## 3. Installer le plugin

1. Arrêtez Emby (ou le conteneur).
2. Copiez `PetitOurs.Torry.dll` dans le dossier `plugins` de la configuration d'Emby :
   - Docker : `<volume /config>/plugins/` (par ex. `/srv/emby/config/plugins/`) ;
   - Linux natif : `/var/lib/emby/plugins/` ;
   - Windows : `%AppData%\Emby-Server\programdata\plugins\`.
3. Ne copiez **aucune autre DLL** (pas de `MonoTorrent.dll`).
4. Redémarrez Emby.
5. Vérifiez le journal (`/config/logs/embyserver.txt`) :
   ```
   Info App: Loading PetitOurs.Torry, Version=0.1.0.0 ...
   Info Torry: Torry démarré : 0 tâche(s), port BitTorrent 6881.
   ```

## 4. Premier réglage

Dans un **navigateur**, connectez-vous en administrateur, puis ouvrez l'une de ces entrées :

- le menu d'administration du serveur, entrée **Torry** (section « Serveur ») ;
- ou **Plugins**, puis **Torry for Emby**, puis **Paramètres**.

Dans l'onglet **Réglages**, vérifiez :

- les dossiers `/downloads/incomplete` et `/downloads/complete` (valeurs par défaut) ;
- le **port BitTorrent**, identique au port publié dans Docker (6881 par défaut) ;
- les limites de débit, le nombre de téléchargements simultanés et les règles de partage.

Si aucune bibliothèque ne contient le dossier « terminés », un avertissement s'affiche en haut de la page.

## Mise à jour

1. Arrêtez Emby.
2. Remplacez `plugins/PetitOurs.Torry.dll` par la nouvelle version.
3. Redémarrez Emby. Les tâches, le cache de reprise et les réglages sont conservés.

## Désinstallation

1. Dans Torry, supprimez les tâches en cours si vous ne souhaitez pas les reprendre plus tard.
2. Arrêtez Emby.
3. Supprimez `plugins/PetitOurs.Torry.dll`.
4. Facultatif : supprimez aussi les données du plugin.
   - `plugins/configurations/PetitOurs.Torry.xml` : les réglages.
   - `data/torry/` : l'état des tâches, les métadonnées `.torrent` et le cache de reprise MonoTorrent.
     **Ce dossier peut contenir des liens magnet et des passkeys de trackers privés.**
5. Redémarrez Emby.

**Les fichiers téléchargés ne sont jamais supprimés par la désinstallation**, ni dans `/downloads/complete`, ni
dans `/downloads/incomplete`. Supprimez-les à la main si besoin, puis retirez le port BitTorrent de votre
configuration Docker et de votre pare-feu.
