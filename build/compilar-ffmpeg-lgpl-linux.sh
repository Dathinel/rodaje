#!/usr/bin/env bash
# Anfitrion Linux x64 con Docker. Uso: bash build/compilar-ffmpeg-lgpl-linux.sh [directorio de salida]
# No publica ni necesita secretos. Directorio nuevo por compilacion; no modifica .entorno.
set -euo pipefail
# shellcheck source=build/ffmpeg-lgpl-comun.sh
source "$(dirname "$0")/ffmpeg-lgpl-comun.sh"
[[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]] || fallar 'Se necesita Linux x64'
for herramienta in docker patchelf readelf curl tar xz; do command -v "$herramienta" >/dev/null; done
salida="${1:-$PWD/salida-ffmpeg/linux64}"
mkdir -p "$salida"
salida="$(cd "$salida" && pwd -P)"
trabajo="$salida/trabajo"
mkdir "$trabajo" # No sobrescribir una compilacion previa.
export RODAJE_FFMPEG_TRABAJO="$trabajo"
export RODAJE_FFMPEG_ENTRADA="$trabajo/entrada"
export RODAJE_FFMPEG_SALIDA="$trabajo/salida-btbn"
mkdir -p "$RODAJE_FFMPEG_ENTRADA" "$RODAJE_FFMPEG_SALIDA"
docker info >/dev/null
if docker buildx inspect ffbuilder >/dev/null 2>&1; then fallar 'Ya existe ffbuilder: usar un anfitrion/runner dedicado'; fi
# shellcheck disable=SC2154 # estado se asigna dentro de la propia trampa.
trap 'estado=$?; docker buildx rm -f ffbuilder >/dev/null 2>&1 || true; exit "$estado"' EXIT
docker buildx imagetools inspect "$IMAGEN_LINUX" > "$trabajo/imagen-base.txt"
descargar_fuente FFmpeg-Builds "$RODAJE_FFMPEG_ENTRADA" >/dev/null
descargar_fuente FFmpeg "$RODAJE_FFMPEG_ENTRADA" >/dev/null
# BtbN usa GITHUB_REPOSITORY para nombrar imagenes locales. No tomar el repositorio de Rodaje.
unset GITHUB_REPOSITORY
for accion in preparar descargar compilar; do
    bash "$BUILD_DIR/compilar-ffmpeg-lgpl.sh" linux64 "$accion" "$IMAGEN_LINUX" "$FFMPEG_COMMIT" chromaprint zvbi
    if [[ "$accion" == preparar && -n "${RODAJE_FFMPEG_FUENTES:-}" && -d "$RODAJE_FFMPEG_FUENTES/etapas" ]]; then
        mkdir -p "$trabajo/btbn/.cache/downloads"
        cp "$RODAJE_FFMPEG_FUENTES/etapas/"*.tar.xz "$trabajo/btbn/.cache/downloads/"
    fi
done
nombre="ffmpeg-$VERSION-linux64-lgpl-shared-9.0"
mkdir "$trabajo/paquete"
archivos=("$RODAJE_FFMPEG_SALIDA"/ffmpeg-*-linux64-lgpl-shared-9.0.tar.xz)
[[ ${#archivos[@]} == 1 && -f "${archivos[0]}" ]] || fallar 'Se esperaba un unico paquete de BtbN'
tar -xJf "${archivos[0]}" -C "$trabajo/paquete"
carpetas=("$trabajo/paquete"/*)
[[ ${#carpetas[@]} == 1 && -d "${carpetas[0]}" ]] || fallar 'Estructura inesperada de BtbN'
paquete="$trabajo/paquete/$nombre"
mv "${carpetas[0]}" "$paquete"
# Rodaje no usa ffplay (en Mac ni se compila): fuera, con su SDL.
rm -f "$paquete/bin/ffplay"
# Cada DSO resuelve sus dependencias junto a si mismo, incluso cargado con dlopen por Rodaje.
# Solo Linux: la receta Windows y sus bytes no pasan por aqui.
# shellcheck disable=SC2016 # ORIGIN lo expande el cargador ELF, no Bash.
while IFS= read -r -d '' f; do patchelf --set-rpath '$ORIGIN' "$f"; done < <(find "$paquete/lib" -type f -name '*.so*' -print0)
# shellcheck disable=SC2016
while IFS= read -r -d '' f; do patchelf --set-rpath '$ORIGIN/../lib' "$f"; done < <(find "$paquete/bin" -type f -print0)
bash "$BUILD_DIR/auditar-ffmpeg.sh" "$paquete" linux
fuente="$trabajo/rodaje-ffmpeg-lgpl-fuente-linux-x64"
mkdir -p "$fuente/etapas"
cp "$RODAJE_FFMPEG_ENTRADA"/*.tar.gz "$fuente/"
cp "$RODAJE_FFMPEG_SALIDA"/fuente/*.tar.xz "$fuente/etapas/"
cp "$RODAJE_FFMPEG_SALIDA/Dockerfile.generado" "$trabajo/imagen-base.txt" "$fuente/"
printf '%s\n' "$IMAGEN_LINUX" > "$fuente/imagen-base-ref.txt"
empaquetar "$paquete" "$fuente" "$salida"
