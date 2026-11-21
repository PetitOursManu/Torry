#!/usr/bin/env sh
# Étapes de build exécutées soit localement, soit dans le conteneur SDK (voir build.sh).
set -eu
cd "$1"
RUN_TESTS="${2:-0}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 CI=true

dotnet tool restore
dotnet restore PetitOurs.Torry.sln
dotnet build src/PetitOurs.Torry/PetitOurs.Torry.csproj -c Release --no-restore

if [ "$RUN_TESTS" = "1" ]; then
  dotnet test tests/PetitOurs.Torry.Tests/PetitOurs.Torry.Tests.csproj -c Release --no-restore \
    --logger "console;verbosity=normal"
fi

mkdir -p artifacts
cp src/PetitOurs.Torry/bin/Release/net8.0/merged/PetitOurs.Torry.dll artifacts/PetitOurs.Torry.dll
sha256sum artifacts/PetitOurs.Torry.dll
