#!/usr/bin/env bash
# CI de Linux y macOS: instala el FFmpeg LGPL de este sistema en .entorno/ffmpeg con build/preparar-entorno.ps1, que
# comprueba la huella fijada. Mientras el paquete no esté publicado en la release, lo toma de los artefactos de
# .github/workflows/ffmpeg-lgpl.yml: el que tenga esa misma huella se copia a .entorno/ffmpeg-lgpl/, donde
# preparar-entorno lo busca antes que la URL. Necesita GH_TOKEN con «actions: read» y pwsh.
set -euo pipefail
case "$(uname -s)" in
    Linux) destino=linux64 rid=linux-x64 ;;
    Darwin) destino=macarm64 rid=osx-arm64 ;;
    *) echo "Sistema sin FFmpeg previsto: $(uname -s)" >&2; exit 1 ;;
esac
raiz="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
entorno="${RODAJE_ENTORNO:-$raiz/.entorno}"
# La huella fijada de este RID en la tabla $FfmpegPorRid (la misma lectura que dotnet-local.sh).
fijada="$(sed -n '/FfmpegPorRid = @{/,/^}/p' "$raiz/build/preparar-entorno.ps1" | sed -n "/'$rid' = @{/,/}/p" \
    | sed -n "s/.*Sha256 *= *'\([^']*\)'.*/\1/p" | head -n 1)"
[[ -n "$fijada" ]] || { echo "preparar-entorno.ps1 no fija la huella de FFmpeg para $rid." >&2; exit 1; }
mkdir -p "$entorno/ffmpeg-lgpl"
temporal="$(mktemp -d)"
trap 'rm -rf "$temporal"' EXIT
for id in $(gh api "repos/${GITHUB_REPOSITORY:?}/actions/artifacts?name=ffmpeg-lgpl-$destino&per_page=30" \
        --jq '.artifacts[] | select(.expired == false) | .id'); do
    rm -rf "$temporal/a" && mkdir "$temporal/a"
    gh api "repos/$GITHUB_REPOSITORY/actions/artifacts/$id/zip" > "$temporal/a.zip"
    unzip -q -o "$temporal/a.zip" -d "$temporal/a"
    if grep -q "^$fijada " "$temporal/a"/ffmpeg-*.tar.xz.sha256 2>/dev/null; then
        cp "$temporal/a"/ffmpeg-*-"$destino"-lgpl-shared-9.0.tar.xz "$entorno/ffmpeg-lgpl/"
        echo "FFmpeg $destino del artefacto $id (huella $fijada)."
        break
    fi
done
pwsh -NoProfile -File "$raiz/build/preparar-entorno.ps1" -SoloFFmpeg
