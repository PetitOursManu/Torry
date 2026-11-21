# Déploiement Docker / Coolify et réseau

## Deux ports, deux rôles

| Port | Protocole | Rôle | Qui s'y connecte | Passe par le proxy / domaine ? |
|---|---|---|---|---|
| **8096** (ou 8920 en HTTPS) | TCP (HTTP) | Interface web et API d'Emby, donc la page Torry | Vous (navigateur), les applications Emby | **Oui** : Coolify (Traefik ou Caddy), Cloudflare, reverse proxy |
| **6881** (réglable) | TCP + UDP | BitTorrent : TCP pour les pairs, UDP pour le DHT | Les autres clients BitTorrent d'Internet | **Non** : connexion directe sur l'IP publique du serveur |

Quelques points importants :

- **Un domaine web ne transporte pas BitTorrent.** `emby.mondomaine.fr` derrière le proxy de Coolify ne sert que le
  HTTP(S). Les pairs n'utilisent pas ce domaine : ils obtiennent l'IP publique de votre serveur par les trackers et
  le DHT, puis se connectent directement au port BitTorrent.
- **Un tunnel Cloudflare (cloudflared) ou un enregistrement DNS « proxied » (nuage orange) ne transporte pas le
  port BitTorrent.** Ces services ne relaient que le HTTP(S), sauf offres spécifiques non couvertes ici. Le port
  6881 doit être joignable directement.
- **Le port est fixé dans les réglages de Torry** (« Port d'écoute BitTorrent »). Il doit être identique des deux
  côtés de la publication Docker (`6881:6881`) : Torry annonce son port interne aux trackers.

## Exemple de configuration

Voir [`deploy/docker-compose.yml`](../deploy/docker-compose.yml) :

```yaml
services:
  emby:
    image: emby/embyserver:4.10.2.0
    restart: unless-stopped
    environment:
      - UID=1000
      - GID=1000
      - GIDLIST=1000
    volumes:
      - /srv/emby/config:/config
      - /srv/torry:/downloads
      - /srv/media:/media
    ports:
      - "8096:8096"
      - "6881:6881/tcp"
      - "6881:6881/udp"
```

### Volumes

| Dans le conteneur | Sur l'hôte (exemple) | Contenu |
|---|---|---|
| `/config` | `/srv/emby/config` | Configuration Emby ; `plugins/PetitOurs.Torry.dll` ; `data/torry/` (état des tâches, `.torrent`, cache de reprise) |
| `/downloads/incomplete` | `/srv/torry/incomplete` | Téléchargements en cours, un sous-dossier par tâche. **Hors bibliothèque.** |
| `/downloads/complete` | `/srv/torry/complete` | Fichiers terminés, un dossier par torrent. **À ajouter à une bibliothèque Emby.** |

- Montez `/srv/torry` **en une seule fois** sur `/downloads`. Ne montez pas `incomplete` et `complete`
  séparément. Avec deux montages, le déplacement final devient une copie suivie d'une suppression : c'est plus
  lent et l'espace disque est temporairement doublé.
- L'utilisateur défini par `UID`/`GID` doit pouvoir écrire dans `/srv/torry`. En cas de « Permissions
  insuffisantes » dans Torry :
  ```sh
  sudo chown -R 1000:1000 /srv/torry
  ```
- Vous pouvez placer `complete` dans votre arborescence média (par ex. monter `/srv/media/torrents` sur
  `/downloads/complete`). Dans ce cas, montez aussi `incomplete` depuis le même système de fichiers, et
  **jamais** à l'intérieur d'une bibliothèque. Torry refuse un dossier « en cours » placé dans le dossier « terminés ».

## Coolify

**Emby déjà installé via le service « Emby » de Coolify** (image `lscr.io/linuxserver/emby`) : suivez le
guide pas à pas du [README](../README.md#installation-sur-un-emby-déployé-avec-coolify).

**Nouvelle installation avec l'image officielle** (`emby/embyserver`), à la place du modèle Coolify :

1. Créez une ressource **Docker Compose** (« Empty Compose ») et collez le contenu de `deploy/docker-compose.yml`.
2. Domaine : associez votre domaine au service `emby`, **port 8096**. Vous pouvez alors supprimer la ligne
   `"8096:8096"` : le proxy de Coolify joint le conteneur en interne.
3. Gardez les lignes `6881:6881/tcp` et `6881:6881/udp`. Elles publient le port directement sur l'hôte,
   sans passer par le proxy.
4. Copiez la DLL dans `/srv/emby/config/plugins/` sur le serveur, puis redéployez ou redémarrez la ressource.

Différences entre les deux images :

| | Officielle `emby/embyserver` | linuxserver (modèle Coolify) |
|---|---|---|
| Utilisateur | `UID`/`GID`/`GIDLIST` | `PUID`/`PGID`, utilisateur `abc` |
| Dossier des plugins | `/config/plugins` | `/config/plugins` |
| Vérifiée avec Torry | Oui : bout en bout, 48 vérifications | Chargement et démarrage, permissions des volumes |

## Pare-feu

Ouvrez le port BitTorrent en **TCP et UDP** à tous les niveaux :

- **Fournisseur cloud** (groupe de sécurité, pare-feu Hetzner, OVH, Scaleway…) : entrée TCP 6881 et UDP 6881.
- **Hôte** : avec UFW, `sudo ufw allow 6881/tcp && sudo ufw allow 6881/udp`. Attention : Docker publie ses
  ports en écrivant directement dans iptables, ce qui contourne souvent UFW. Vérifiez donc le résultat
  réel depuis l'extérieur plutôt que de vous fier à la seule règle UFW.
- **Box ou routeur** (serveur à domicile) : redirigez le port 6881 TCP et UDP vers l'IP locale du serveur.
  La redirection automatique UPnP / NAT-PMP de Torry ne fonctionne pas en réseau Docker « bridge »
  (le conteneur ne voit pas le routeur). Elle est désactivée par défaut.

Test depuis une autre machine : `nc -vz <ip-publique> 6881` doit réussir pendant qu'Emby tourne.

N'exposez pas le port 6881 pour autre chose : seul le moteur BitTorrent y écoute. Aucune commande Torry
n'est accessible sur ce port. L'interface et l'API restent derrière l'authentification Emby, sur le port web.

## Sans port entrant accessible

Torry fonctionne aussi sans port entrant ouvert, avec des limites :

- seules les connexions **sortantes** sont possibles : Torry ne joint que les pairs qui, eux, acceptent les
  connexions entrantes ;
- deux pairs dans cette situation ne peuvent pas se connecter l'un à l'autre : un torrent peu partagé peut
  rester bloqué, faute de source joignable ;
- les débits sont généralement plus faibles, et le partage après téléchargement est très limité : le ratio
  progresse peu ;
- la récupération des métadonnées d'un lien magnet peut être lente ou échouer, faute de pairs joignables ;
- les trackers privés qui exigent d'être « connectable » peuvent pénaliser le compte.

## Confidentialité réseau

BitTorrent expose l'**IP publique du serveur** aux trackers et aux pairs. Torry n'intègre pas de VPN (hors
périmètre). Si vous en utilisez un, il doit être configuré au niveau du conteneur ou de l'hôte, et le port
BitTorrent doit alors être redirigé par le fournisseur VPN.
