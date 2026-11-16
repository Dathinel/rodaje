#!/usr/bin/env bash
# Prueba el paquete real con el instalador y despues de moverlo. Nunca abre ffplay ni una interfaz.
# Uso: probar-ffmpeg-lgpl.sh <tar.xz> <linux|mac> <directorio nuevo para la prueba>
set -euo pipefail
# shellcheck source=build/ffmpeg-lgpl-comun.sh
source "$(dirname "$0")/ffmpeg-lgpl-comun.sh"
[[ $# == 3 && ( "$2" == linux || "$2" == mac ) ]] || fallar 'Uso: probar-ffmpeg-lgpl.sh <tar.xz> <linux|mac> <directorio nuevo>'
archivo="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
sistema="$2"
mkdir "$3"
prueba="$(cd "$3" && pwd -P)"
export RODAJE_ENTORNO="$prueba/entorno"
export RODAJE_FFMPEG_URL="$archivo"
RODAJE_FFMPEG_SHA256="$(huella "$archivo")"; export RODAJE_FFMPEG_SHA256
esperada="$(awk '{print $1}' "$archivo.sha256")"
[[ "$RODAJE_FFMPEG_SHA256" == "$esperada" ]] || fallar 'El paquete no coincide con su .sha256'
pwsh -NoProfile -File "$BUILD_DIR/preparar-entorno.ps1" -SoloFFmpeg
[[ -f "$RODAJE_ENTORNO/ffmpeg/.ffmpeg-$VERSION" ]] || fallar 'El instalador no creo la marca'
mv "$RODAJE_ENTORNO/ffmpeg" "$prueba/ffmpeg movido"
paquete="$prueba/ffmpeg movido"
unset LD_LIBRARY_PATH LD_PRELOAD DYLD_LIBRARY_PATH DYLD_FALLBACK_LIBRARY_PATH DYLD_INSERT_LIBRARIES
for lib in avcodec:63 avformat:63 avutil:61 swresample:7 swscale:10; do
    if [[ "$sistema" == linux ]]; then ruta="lib${lib%:*}.so.${lib#*:}"; else ruta="lib${lib%:*}.${lib#*:}.dylib"; fi
    [[ -f "$paquete/lib/$ruta" ]] || fallar "Falta ABI $ruta"
done
for lib in avfilter avdevice; do
    if [[ "$sistema" == linux ]]; then rutas=("$paquete/lib/lib$lib.so."*); else rutas=("$paquete/lib/lib$lib."*.dylib); fi
    [[ -f "${rutas[0]}" ]] || fallar "Falta lib$lib"
done
bash "$BUILD_DIR/auditar-ffmpeg.sh" "$paquete" "$sistema"
"$paquete/bin/ffmpeg" -hide_banner -encoders > "$prueba/encoders.txt" 2>&1
codificadores=(libopenh264 libsvtav1 libvpx-vp9 libopus libmp3lame libkvazaar libwebp prores_ks ffv1 aac png)
[[ "$sistema" != mac ]] || codificadores+=(h264_videotoolbox)
for codificador in "${codificadores[@]}"; do
    awk '{print $2}' "$prueba/encoders.txt" | grep -Fx -- "$codificador" || fallar "Falta codificador: $codificador"
done
# Comprueba que ambos ejecutables funcionan con la carpeta reubicada (sin GPU ni pantalla).
"$paquete/bin/ffmpeg" -hide_banner -loglevel error -f lavfi -i color=size=64x64:rate=1 \
    -frames:v 1 -c:v ffv1 "$prueba/prueba.mkv"
"$paquete/bin/ffprobe" -v error -show_entries stream=codec_name -of default=nw=1 "$prueba/prueba.mkv" > "$prueba/ffprobe.txt"
grep -Fx 'codec_name=ffv1' "$prueba/ffprobe.txt"
echo 'Instalacion, reubicacion y codificadores: OK'
