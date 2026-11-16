#!/usr/bin/env bash
# Ejecuta el SDK de .NET del entorno propio (.entorno/dotnet) con la caché NuGet local: el dotnet-local.cmd de Unix.
# Si falta algo lo prepara build/preparar-entorno.ps1 con pwsh. RODAJE_ENTORNO apunta a otro .entorno.
set -euo pipefail

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENTORNO="${RODAJE_ENTORNO:-$RAIZ/.entorno}"
export DOTNET_ROOT="$ENTORNO/dotnet"
export NUGET_PACKAGES="$ENTORNO/nuget"
export DOTNET_CLI_HOME="$ENTORNO"
export DOTNET_CLI_FORCE_UTF8_ENCODING=1
export DOTNET_MULTILEVEL_LOOKUP=0
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export PATH="$DOTNET_ROOT:$PATH"

# Listo = existen las marcas .sdk-<versión> (de global.json) y .ffmpeg-<versión> (de preparar-entorno.ps1, solo si
# este RID tiene huella publicada); el binario dotnet no basta tras un apagón.
SDKVER="$(sed -n 's/.*"version" *: *"\([^"]*\)".*/\1/p' "$RAIZ/global.json" | head -n 1)"
FFVER="$(sed -n "s/^\$FfmpegVersion = '\([^']*\)'.*/\1/p" "$RAIZ/build/preparar-entorno.ps1" | head -n 1)"

# RID del anfitrión, igual que en preparar-entorno.ps1.
case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) RID=linux-x64 ;;
    Darwin-arm64) RID=osx-arm64 ;;
    *)            RID="$(uname -s | tr '[:upper:]' '[:lower:]')-$(uname -m)" ;;
esac

# La huella SHA-256 de FFmpeg de este RID dentro de la tabla $FfmpegPorRid (vacía = todavía no publicado).
FFSHA="$(sed -n '/FfmpegPorRid = @{/,/^}/p' "$RAIZ/build/preparar-entorno.ps1" \
    | sed -n "/'$RID' = @{/,/}/p" \
    | sed -n "s/.*Sha256 *= *'\([^']*\)'.*/\1/p" | head -n 1)"

falta=0
[ -f "$DOTNET_ROOT/.sdk-$SDKVER" ] && [ -f "$DOTNET_ROOT/dotnet" ] || falta=1
if [ -n "$FFSHA" ]; then
    [ -f "$ENTORNO/ffmpeg/.ffmpeg-$FFVER" ] || falta=1
fi

if [ "$falta" -eq 1 ]; then
    if ! command -v pwsh >/dev/null 2>&1; then
        echo "Falta el entorno y también pwsh (PowerShell 7), que es quien lo prepara." >&2
        exit 1
    fi
    pwsh -NoProfile -File "$RAIZ/build/preparar-entorno.ps1"
fi

exec "$DOTNET_ROOT/dotnet" "$@"
