#!/usr/bin/env bash
# Pruebas de los proyectos que compilan para net10.0 sin Windows (Linux y macOS), con el filtro de categorías que se pase.
#   build/pruebas-linux.sh 'Categoria!=SoloWindows&Categoria!=TiempoReal&Categoria!=Rendimiento'
# Deja los .trx en TestResults/; una prueba colgada se corta a los 10 min con su nombre. Un proyecto en rojo no salta los siguientes; el código de salida es el del peor.
# Lo usan los trabajos de Linux y macOS de ci.yml y el de Linux de nocturna.yml.
set -uo pipefail
filtro="$1"
estado=0
for p in Core Packages Playback TestServer Analysis Modules.Protocol Media Audio Engine TestMedia Module.AudioAi Module.Voice Module.Subtitles Module.Translate Module.Vision Text; do
  proyecto="tests/Rodaje.$p.Tests/Rodaje.$p.Tests.csproj"
  dotnet restore "$proyecto" --locked-mode || { estado=1; continue; }
  dotnet test "$proyecto" --no-restore --filter "$filtro" \
    --logger "trx;LogFilePrefix=$p" --results-directory TestResults \
    --blame-hang-timeout 10m --blame-hang-dump-type none || estado=1
done
exit $estado
