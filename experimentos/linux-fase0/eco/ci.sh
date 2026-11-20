#!/usr/bin/env bash
# Fase 0 (e) de D-066: publica el eco con Native AOT para el RID del corredor y lo ejecuta.
# Sirve en Ubuntu 24.04 (linux-x64) y en macos-15 arm64 (osx-arm64). No instala nada: el SDK lo trae el corredor.
set -euo pipefail
cd "$(dirname "$0")"

case "$(uname -s)/$(uname -m)" in
    Linux/x86_64)  rid=linux-x64 ;;
    Darwin/arm64)  rid=osx-arm64 ;;
    *) echo "Corredor no previsto: $(uname -s) $(uname -m)" >&2; exit 2 ;;
esac

dotnet publish eco.csproj -c Release -r "$rid" -p:PublishAot=true
"./bin/Release/net10.0/$rid/publish/eco"
