#!/usr/bin/env bash
# Vérification de bout en bout de Torry dans un VRAI serveur Emby (Docker), sans Internet BitTorrent :
#  - un « seeder » local (eng/LocalSeeder, MonoTorrent) partage des vidéos de test générées avec le ffmpeg d'Emby
#    et se connecte au port BitTorrent de Torry (connexion entrante) ;
#  - les appels HTTP sont faits depuis un conteneur alpine (curl + jq) sur le même réseau.
# Tout est créé avec le préfixe « torry-verify » et supprimé au début de chaque exécution.
#
# Usage : eng/verify-emby.sh [--keep]     (--keep laisse Emby tourner sur http://localhost:18096)
set -euo pipefail
export MSYS_NO_PATHCONV=1

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
EMBY_IMAGE="${EMBY_IMAGE:-emby/embyserver:4.10.2.0}"
SDK_IMAGE="mcr.microsoft.com/dotnet/sdk:8.0"
P=torry-verify
NET=$P-net
KEEP="${1:-}"

log() { printf '\n=== %s\n' "$*"; }

log "Nettoyage des ressources de test précédentes"
docker rm -f $P-emby $P-seeder >/dev/null 2>&1 || true
docker volume rm $P-config $P-downloads $P-seed $P-seedout $P-state >/dev/null 2>&1 || true
docker network rm $NET >/dev/null 2>&1 || true

log "Build du plugin (DLL fusionnée)"
sh "$ROOT/build.sh"
DLL="$ROOT/artifacts/PetitOurs.Torry.dll"

log "Préparation : réseau, volumes, contenu de test"
docker network create $NET >/dev/null
for v in config downloads seed seedout state; do docker volume create $P-$v >/dev/null; done
docker run --rm -v $P-seed:/seed alpine sh -c 'mkdir -p "/seed/Torry Test Movie (2024)/Extras" && echo "notes de test" > "/seed/Torry Test Movie (2024)/notes.txt" && chmod -R 777 /seed'
docker run --rm --entrypoint /bin/ffmpeg -v $P-seed:/seed "$EMBY_IMAGE" -hide_banner -loglevel error -y \
  -f lavfi -i testsrc=duration=30:size=640x360:rate=25 -f lavfi -i sine=frequency=440:duration=30 \
  -c:v libx264 -preset veryfast -c:a aac -shortest "/seed/Torry Test Movie (2024)/Torry Test Movie (2024).mkv"
docker run --rm --entrypoint /bin/ffmpeg -v $P-seed:/seed "$EMBY_IMAGE" -hide_banner -loglevel error -y \
  -f lavfi -i testsrc2=duration=8:size=320x240:rate=25 -c:v libx264 -preset veryfast "/seed/Torry Test Movie (2024)/Extras/Making-of.mkv"

# Un seul volume /downloads contient « incomplete » et « complete » : même système de fichiers,
# donc la finalisation est un simple renommage (pas de copie).
docker run --rm -v $P-config:/config -v $P-downloads:/downloads -v "$ROOT/artifacts:/a:ro" alpine sh -c \
  'mkdir -p /config/plugins /downloads/incomplete /downloads/complete && cp /a/PetitOurs.Torry.dll /config/plugins/ && chown -R 2:2 /config /downloads'

log "Démarrage d'Emby ($EMBY_IMAGE)"
docker run -d --name $P-emby --network $NET -p 18096:8096 \
  -v $P-config:/config -v $P-downloads:/downloads "$EMBY_IMAGE" >/dev/null
for i in $(seq 1 60); do
  curl -sf http://localhost:18096/emby/System/Info/Public >/dev/null 2>&1 && break
  sleep 2
done
curl -s http://localhost:18096/emby/System/Info/Public; echo

log "Démarrage du seeder local"
docker run -d --name $P-seeder --network $NET -v $P-seed:/seed:ro -v $P-seedout:/out -v "$ROOT/eng/LocalSeeder:/app:ro" \
  -v torry-nuget:/root/.nuget "$SDK_IMAGE" sh -c 'rm -rf /tmp/app && cp -r /app /tmp/app && cd /tmp/app && dotnet run -c Release -- "/seed/Torry Test Movie (2024)" /out torry-verify-emby 6881' >/dev/null
for i in $(seq 1 90); do
  docker run --rm -v $P-seedout:/o alpine test -f /o/test.magnet && break
  sleep 2
done

api() {
  docker run --rm --network $NET -v $P-state:/state -v $P-seedout:/seed-out:ro -v "$ROOT/eng:/eng:ro" alpine \
    sh -c 'apk add --no-cache curl jq >/dev/null 2>&1 && sh /eng/verify-api.sh '"$1"
}

log "Phase 1 : installation, autorisations, import .torrent, téléchargement"
api setup

log "Contrôle des fichiers sur disque"
docker run --rm -v $P-downloads:/downloads alpine sh -c '
  echo "--- /downloads/complete"; find /downloads/complete -type f | sort
  echo "--- /downloads/incomplete"; find /downloads/incomplete -type f | sort
  test -f "/downloads/complete/Torry Test Movie (2024)/Torry Test Movie (2024).mkv" && echo "PASS  vidéo principale finalisée" || echo "FAIL  vidéo principale absente"
  test -f "/downloads/complete/Torry Test Movie (2024)/Extras/Making-of.mkv" && echo "PASS  structure du torrent conservée (Extras/)" || echo "FAIL  sous-dossier Extras absent"
  test ! -e "/downloads/complete/Torry Test Movie (2024)/notes.txt" && echo "PASS  fichier non sélectionné absent de la bibliothèque" || echo "FAIL  fichier non sélectionné présent"
  test -z "$(find /downloads/incomplete -name "*.mkv")" && echo "PASS  aucun doublon dans « incomplete »" || echo "FAIL  doublon dans « incomplete »"' | tee -a /dev/stderr | grep -E '^(PASS|FAIL)' > /tmp/torry-disk.txt || true
SEED_SUM=$(docker run --rm -v $P-seed:/s alpine sha256sum "/s/Torry Test Movie (2024)/Torry Test Movie (2024).mkv" | cut -d' ' -f1)
DL_SUM=$(docker run --rm -v $P-downloads:/d alpine sha256sum "/d/complete/Torry Test Movie (2024)/Torry Test Movie (2024).mkv" | cut -d' ' -f1)
[ "$SEED_SUM" = "$DL_SUM" ] && echo "PASS  contenu identique à la source (sha256)" | tee -a /tmp/torry-disk.txt || echo "FAIL  contenu différent" | tee -a /tmp/torry-disk.txt

log "Phase 2 : apparition dans la bibliothèque Emby"
api library

log "Redémarrage d'Emby (arrêt propre du moteur, reprise des tâches)"
docker restart -t 30 $P-emby >/dev/null
for i in $(seq 1 60); do
  curl -sf http://localhost:18096/emby/System/Info/Public >/dev/null 2>&1 && break
  sleep 2
done
docker logs $P-emby 2>&1 | grep -i "Torry arrêté\|Torry démarré" | tail -3 || true

log "Phase 3 : reprise après redémarrage, arrêt du partage, suppression (fichiers conservés)"
api after-restart

# Le seeder a vu Torry en « seeder » (tâche restaurée) : MonoTorrent ne reconnecte pas deux seeders entre eux.
# On le redémarre pour qu'il se reconnecte au port de Torry lors de la récupération des métadonnées du magnet.
RESTART_AT=$(date -u +%Y-%m-%dT%H:%M:%SZ)
docker restart $P-seeder >/dev/null
for i in $(seq 1 60); do
  docker logs --since "$RESTART_AT" $P-seeder 2>&1 | grep -q "state=Seeding" && break
  sleep 2
done

log "Phase 4 : magnet (pair entrant), collision de nom, pause/reprise, suppression des fichiers"
api magnet

log "Journaux Emby : aucune fuite de lien magnet"
LOGS=$(docker exec $P-emby sh -c 'cat /config/logs/*.txt' 2>/dev/null || true)
if printf '%s' "$LOGS" | grep -qi 'magnet:\|xt=urn:btih'; then
  echo "FAIL  un lien magnet apparaît dans les journaux" | tee -a /tmp/torry-disk.txt
else
  echo "PASS  aucun lien magnet dans les journaux Emby" | tee -a /tmp/torry-disk.txt
fi
printf '%s' "$LOGS" | grep -i "torry" | grep -iv "Loading\|Copying" | tail -25

log "Résultat"
RESULTS=$(docker run --rm -v $P-state:/state alpine cat /state/results.txt; cat /tmp/torry-disk.txt)
printf '%s\n' "$RESULTS"
TOTAL=$(printf '%s\n' "$RESULTS" | grep -c '^PASS\|^FAIL' || true)
FAILED=$(printf '%s\n' "$RESULTS" | grep -c '^FAIL' || true)
echo "Total : $TOTAL vérifications, $FAILED échec(s)."

if [ "$KEEP" != "--keep" ]; then
  docker rm -f $P-emby $P-seeder >/dev/null 2>&1 || true
fi
[ "$FAILED" = "0" ]
