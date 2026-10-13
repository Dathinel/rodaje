#!/usr/bin/env bash
# Pruebas de los proyectos que compilan para net10.0 sin Windows, con el filtro de categorías que se pase.
#   build/pruebas-linux.sh 'Categoria!=SoloWindows&Categoria!=TiempoReal&Categoria!=Rendimiento'
# Deja los .trx en TestResults/. Lo usan los trabajos de Linux de ci.yml y nocturna.yml.
set -euo pipefail
filtro="$1"
for p in Core Packages Playback TestServer; do
  proyecto="tests/Rodaje.$p.Tests/Rodaje.$p.Tests.csproj"
  dotnet restore "$proyecto" --locked-mode
  dotnet test "$proyecto" --no-restore --filter "$filtro" \
    --logger "trx;LogFilePrefix=$p" --results-directory TestResults
done
