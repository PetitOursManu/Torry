#!/usr/bin/env sh
# Build reproductible de Torry for Emby.
# - Si un SDK .NET 8 est installé localement, il est utilisé.
# - Sinon, le build s'exécute dans l'image officielle du SDK .NET (version épinglée).
# Usage : ./build.sh            -> compile la DLL dans artifacts/
#         ./build.sh --test     -> compile et exécute les tests
set -eu

SDK_IMAGE="mcr.microsoft.com/dotnet/sdk:8.0.428@sha256:1b955f179b02df4f0eb5fadedaa898cbe44d620a64aab52eec72c8e91d1355b5"
ROOT="$(cd "$(dirname "$0")" && pwd)"
RUN_TESTS=0
[ "${1:-}" = "--test" ] && RUN_TESTS=1

if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^8\.'; then
  sh "$ROOT/eng/build-inner.sh" "$ROOT" "$RUN_TESTS"
else
  MSYS_NO_PATHCONV=1 docker run --rm \
    -v "$ROOT:/src" -v torry-nuget:/root/.nuget \
    -w /src "$SDK_IMAGE" sh /src/eng/build-inner.sh /src "$RUN_TESTS"
fi
