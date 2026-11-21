#!/bin/sh
# Vérifications HTTP contre un vrai serveur Emby (exécuté dans un conteneur alpine sur le réseau de test).
# Appelé par eng/verify-emby.sh, une phase à la fois. État partagé dans /state.
set -eu

BASE="${EMBY_URL:-http://torry-verify-emby:8096/emby}"
STATE=/state
PHASE="$1"
ADMIN_PW="TorryVerif-Admin-1"
GUEST_PW="TorryVerif-Invite-1"
AUTH_HDR='X-Emby-Authorization: Emby Client="TorryVerify", Device="cli", DeviceId="torry-verify", Version="1.0"'

pass() { echo "PASS  $1" | tee -a "$STATE/results.txt"; }
fail() { echo "FAIL  $1" | tee -a "$STATE/results.txt"; }
check() { if [ "$2" = "$3" ]; then pass "$1 ($2)"; else fail "$1 (obtenu: $2, attendu: $3)"; fi; }

code() { # code HTTP d'une requête : code METHODE CHEMIN [TOKEN] [CORPS]
  if [ -n "${4:-}" ]; then
    curl -s -o /tmp/body -w '%{http_code}' -X "$1" -H "X-Emby-Token: $3" -H 'Content-Type: application/json' --data-binary "$4" "$BASE$2"
  elif [ -n "${3:-}" ]; then
    curl -s -o /tmp/body -w '%{http_code}' -X "$1" -H "X-Emby-Token: $3" -H 'Content-Type: application/json' "$BASE$2"
  else
    curl -s -o /tmp/body -w '%{http_code}' -X "$1" -H 'Content-Type: application/json' "$BASE$2"
  fi
}

login() { # login UTILISATEUR MOTDEPASSE -> jeton
  curl -s -X POST -H "$AUTH_HDR" -H 'Content-Type: application/json' \
    --data-binary "{\"Username\":\"$1\",\"Pw\":\"$2\"}" "$BASE/Users/AuthenticateByName" | jq -r .AccessToken
}

status_of() { # status_of ID -> état
  curl -s -H "X-Emby-Token: $(cat $STATE/admin.token)" "$BASE/Torry/Status" | jq -r --arg id "$1" '.Tasks[] | select(.Id==$id) | .State'
}

wait_state() { # wait_state ID ETAT1|ETAT2 SECONDES
  i=0
  while [ $i -lt "$3" ]; do
    s="$(status_of "$1")"
    case "|$2|" in *"|$s|"*) echo "$s"; return 0;; esac
    i=$((i + 2)); sleep 2
  done
  echo "${s:-inconnu}"
}

case "$PHASE" in
  setup)
    : > "$STATE/results.txt"
    curl -s -X POST -H 'Content-Type: application/json' --data-binary '{"UICulture":"fr","MetadataCountryCode":"FR","PreferredMetadataLanguage":"fr"}' "$BASE/Startup/Configuration" >/dev/null || true
    curl -s -X POST -H 'Content-Type: application/json' --data-binary "{\"Name\":\"admin\",\"Password\":\"$ADMIN_PW\"}" "$BASE/Startup/User" >/dev/null || true
    curl -s -X POST "$BASE/Startup/Complete" >/dev/null || true
    TOKEN="$(login admin "$ADMIN_PW")"
    [ -n "$TOKEN" ] && [ "$TOKEN" != "null" ] && pass "connexion administrateur" || { fail "connexion administrateur"; exit 1; }
    echo "$TOKEN" > "$STATE/admin.token"

    # Utilisateur non administrateur
    GUEST_ID="$(curl -s -X POST -H "X-Emby-Token: $TOKEN" -H 'Content-Type: application/json' --data-binary '{"Name":"invite"}' "$BASE/Users/New" | jq -r .Id)"
    curl -s -X POST -H "X-Emby-Token: $TOKEN" -H 'Content-Type: application/json' --data-binary "{\"NewPw\":\"$GUEST_PW\"}" "$BASE/Users/$GUEST_ID/Password" >/dev/null
    GUEST="$(login invite "$GUEST_PW")"
    echo "$GUEST" > "$STATE/guest.token"
    [ -n "$GUEST" ] && [ "$GUEST" != "null" ] && pass "connexion utilisateur non administrateur" || fail "connexion utilisateur non administrateur"

    # Plugin chargé et page déclarée dans le menu d'administration
    PLUGIN="$(curl -s -H "X-Emby-Token: $TOKEN" "$BASE/Plugins" | jq -r '.[] | select(.Id=="ae967ccc2927494eaae63db68f8913cf" or .Id=="ae967ccc-2927-494e-aae6-3db68f8913cf") | .Name')"
    check "plugin listé par Emby" "$PLUGIN" "Torry for Emby"
    PAGE="$(curl -s -H "X-Emby-Token: $TOKEN" "$BASE/web/ConfigurationPages?EnableInMainMenu=true" | jq -r '.[] | select(.Name=="torry") | .MenuSection')"
    check "page Torry dans le menu d'administration (section)" "$PAGE" "server"
    check "page HTML servie" "$(code GET '/web/ConfigurationPage?name=torry')" "200"
    check "script JS servi" "$(code GET '/web/ConfigurationPage?name=torryjs')" "200"

    # Bibliothèque « Films » sur le dossier des téléchargements terminés
    curl -s -o /dev/null -w '' -X POST -H "X-Emby-Token: $TOKEN" -H 'Content-Type: application/json' \
      --data-binary '{"LibraryOptions":{"PathInfos":[{"Path":"/downloads/complete"}]}}' \
      "$BASE/Library/VirtualFolders?Name=Films&CollectionType=movies&RefreshLibrary=false&Paths=%2Fdownloads%2Fcomplete"
    LIB="$(curl -s -H "X-Emby-Token: $TOKEN" "$BASE/Library/VirtualFolders" | jq -r '.[] | select(.Name=="Films") | .Locations[0]')"
    check "bibliothèque Films créée" "$LIB" "/downloads/complete"

    # Autorisations : anonyme, non admin, admin
    check "GET /Torry/Status sans jeton -> 401" "$(code GET /Torry/Status)" "401"
    check "GET /Torry/Status jeton invalide -> 401" "$(code GET /Torry/Status not-a-token)" "401"
    check "GET /Torry/Status non admin -> 403" "$(code GET /Torry/Status "$GUEST")" "403"
    check "POST magnet sans jeton -> 401" "$(code POST /Torry/Tasks/Magnet '' '{"Magnet":"magnet:?xt=urn:btih:0000000000000000000000000000000000000000"}')" "401"
    check "POST magnet non admin -> 403" "$(code POST /Torry/Tasks/Magnet "$GUEST" '{"Magnet":"magnet:?xt=urn:btih:0000000000000000000000000000000000000000"}')" "403"
    check "GET /Torry/Settings non admin -> 403" "$(code GET /Torry/Settings "$GUEST")" "403"
    check "POST /Torry/Settings non admin -> 403" "$(code POST /Torry/Settings "$GUEST" '{}')" "403"
    check "GET /Torry/Status admin -> 200" "$(code GET /Torry/Status "$TOKEN")" "200"
    RUNNING="$(jq -r .Engine.Running /tmp/body)"
    check "moteur démarré" "$RUNNING" "true"

    # Validation côté serveur
    check "magnet invalide -> 400" "$(code POST /Torry/Tasks/Magnet "$TOKEN" '{"Magnet":"http://example.org"}')" "400"
    echo "      message: $(cat /tmp/body)"
    check "réglage dangereux (en cours dans terminés) -> 400" "$(code POST /Torry/Settings "$TOKEN" '{"IncompleteDirectory":"/downloads/complete/tmp","CompletedDirectory":"/downloads/complete","ListenPort":6881,"MaxActiveDownloads":3,"MaxConnections":150,"SeedRatioLimit":1,"SeedTimeLimitMinutes":60}')" "400"
    echo "      message: $(cat /tmp/body)"

    # Réglages effectifs du test : partage sans limite (pour vérifier la reprise du partage après redémarrage)
    check "enregistrement des réglages -> 200" "$(code POST /Torry/Settings "$TOKEN" '{"IncompleteDirectory":"/downloads/incomplete","CompletedDirectory":"/downloads/complete","ListenPort":6881,"EnableDht":false,"MaxActiveDownloads":3,"MaxConnections":150,"SeedAfterDownload":true,"SeedRatioLimit":0,"SeedTimeLimitMinutes":0,"RefreshLibraryOnComplete":true}')" "200"

    # Import du .torrent (corps JSON base64)
    DATA="$(base64 -w0 /seed-out/test.torrent)"
    printf '{"Data":"%s"}' "$DATA" > /tmp/torrent.json
    check "import .torrent -> 200" "$(curl -s -o /tmp/body -w '%{http_code}' -X POST -H "X-Emby-Token: $TOKEN" -H 'Content-Type: application/json' --data-binary @/tmp/torrent.json "$BASE/Torry/Tasks/TorrentFile")" "200"
    ID="$(jq -r .Id /tmp/body)"
    echo "$ID" > "$STATE/task1.id"
    check "état après import" "$(jq -r .State /tmp/body)" "AwaitingSelection"
    check "doublon refusé -> 400" "$(curl -s -o /tmp/body -w '%{http_code}' -X POST -H "X-Emby-Token: $TOKEN" -H 'Content-Type: application/json' --data-binary @/tmp/torrent.json "$BASE/Torry/Tasks/TorrentFile")" "400"

    # Sélection : uniquement les vidéos
    curl -s -H "X-Emby-Token: $TOKEN" "$BASE/Torry/Tasks/$ID/Files" > /tmp/files.json
    SEL="$(jq -r '[.[] | select(.IsVideo) | .Index] | join(",")' /tmp/files.json)"
    check "nombre de fichiers" "$(jq length /tmp/files.json)" "3"
    check "démarrage avec sélection -> 200" "$(code POST "/Torry/Tasks/$ID/Start" "$TOKEN" "{\"SelectedFiles\":\"$SEL\"}")" "200"
    check "téléchargement terminé puis partage" "$(wait_state "$ID" "Seeding|Completed" 180)" "Seeding"
    curl -s -H "X-Emby-Token: $TOKEN" "$BASE/Torry/Status" | jq ".Tasks[] | select(.Id==\"$ID\") | {Name,State,Progress,FinalFolder,LibraryNote,Ratio}" | sed 's/^/      /'
    ;;

  library)
    TOKEN="$(cat $STATE/admin.token)"
    i=0; FOUND=""
    while [ $i -lt 240 ]; do
      FOUND="$(curl -s -H "X-Emby-Token: $TOKEN" "$BASE/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path" | jq -r '.Items[] | select(.Path | startswith("/downloads/complete/")) | .Name' | head -1)"
      [ -n "$FOUND" ] && break
      i=$((i + 5)); sleep 5
    done
    [ -n "$FOUND" ] && pass "film visible dans la bibliothèque Emby après ${i}s : $FOUND" || fail "film absent de la bibliothèque Emby après ${i}s"
    ;;

  after-restart)
    TOKEN="$(cat $STATE/admin.token)"
    ID="$(cat $STATE/task1.id)"
    check "tâche restaurée après redémarrage d'Emby, partage repris" "$(wait_state "$ID" "Seeding" 60)" "Seeding"

    # Arrêt du partage, puis suppression en conservant les fichiers (défaut)
    check "arrêt du partage -> 200" "$(code POST "/Torry/Tasks/$ID/Cancel" "$TOKEN" '{}')" "200"
    check "état après arrêt du partage" "$(jq -r .State /tmp/body)" "Completed"
    check "suppression (fichiers conservés) -> 200" "$(code POST "/Torry/Tasks/$ID/Delete" "$TOKEN" '{"DeleteFiles":false}')" "200"
    ;;

  magnet)
    TOKEN="$(cat $STATE/admin.token)"
    # Lien magnet : métadonnées obtenues auprès du pair (connexion entrante), puis collision de nom
    MAGNET="$(cat /seed-out/test.magnet)"
    check "ajout magnet -> 200" "$(code POST /Torry/Tasks/Magnet "$TOKEN" "{\"Magnet\":\"$MAGNET\"}")" "200"
    ID2="$(jq -r .Id /tmp/body)"
    echo "$ID2" > "$STATE/task2.id"
    check "métadonnées du magnet reçues" "$(wait_state "$ID2" "AwaitingSelection" 120)" "AwaitingSelection"
    check "démarrage magnet (tous fichiers) -> 200" "$(code POST "/Torry/Tasks/$ID2/Start" "$TOKEN" '{"SelectedFiles":"0,1,2"}')" "200"
    check "magnet téléchargé" "$(wait_state "$ID2" "Seeding|Completed" 180)" "Seeding"
    FOLDER="$(curl -s -H "X-Emby-Token: $TOKEN" "$BASE/Torry/Status" | jq -r ".Tasks[] | select(.Id==\"$ID2\") | .FinalFolder")"
    check "collision de nom gérée (pas d'écrasement)" "$FOLDER" "Torry Test Movie (2024) (2)"

    # Pause / reprise
    check "pause -> 200" "$(code POST "/Torry/Tasks/$ID2/Pause" "$TOKEN" '{}')" "200"
    check "état en pause" "$(jq -r .State /tmp/body)" "Paused"
    check "reprise -> 200" "$(code POST "/Torry/Tasks/$ID2/Resume" "$TOKEN" '{}')" "200"
    check "état après reprise" "$(wait_state "$ID2" "Seeding" 30)" "Seeding"

    # Suppression des fichiers : confirmation explicite obligatoire
    check "suppression fichiers sans confirmation -> 400" "$(code POST "/Torry/Tasks/$ID2/Delete" "$TOKEN" '{"DeleteFiles":true,"Confirmation":"oui"}')" "400"
    echo "      message: $(cat /tmp/body)"
    check "suppression fichiers confirmée -> 200" "$(code POST "/Torry/Tasks/$ID2/Delete" "$TOKEN" "{\"DeleteFiles\":true,\"Confirmation\":\"$ID2\"}")" "200"
    check "fichiers supprimés" "$(jq -r .DeletedFiles /tmp/body)" "3"
    ;;

  *)
    echo "phase inconnue: $PHASE" >&2; exit 2;;
esac
