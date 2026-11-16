#!/usr/bin/env bash
# Parte (c): compila vt_vulkan.m en el corredor de macOS (arm64) y lo corre con H.264 8 bits (NV12) y HEVC 10 bits (P010).
# FFmpeg, MoltenVK y las cabeceras de Vulkan vienen de Homebrew (instalados por el workflow). El FFmpeg de Homebrew es una
# compilación GPL: aquí solo es herramienta de la prueba en el corredor, no se distribuye ni se enlaza con Rodaje.
set -uo pipefail
cd "$(dirname "$0")"
mkdir -p salida
segundos="${FASE0_SEGUNDOS:-30}"

ffmpeg -hide_banner -loglevel error -y -f lavfi -i testsrc2=size=1920x1080:rate=30 -t 3 -c:v libx264 -pix_fmt yuv420p -bf 0 salida/h264-8.mp4 || exit 1
ffmpeg -hide_banner -loglevel error -y -f lavfi -i testsrc2=size=1920x1080:rate=30 -t 3 -c:v libx265 -pix_fmt yuv420p10le -tag:v hvc1 -x265-params log-level=error salida/hevc-10.mp4 || exit 1

molten="$(brew --prefix molten-vk)"
clang -fobjc-arc -O2 -Wall -o salida/vt_vulkan vt_vulkan.m \
  $(pkg-config --cflags --libs libavcodec libavformat libavutil) \
  -I"$(brew --prefix vulkan-headers)/include" -L"$molten/lib" -lMoltenVK -Wl,-rpath,"$molten/lib" \
  -framework Foundation -framework Metal -framework CoreVideo -framework IOSurface || exit 1

estado=0
: > salida/resultado.jsonl
for video in salida/h264-8.mp4 salida/hevc-10.mp4; do
  ./salida/vt_vulkan "$video" "$segundos" >> salida/resultado.jsonl 2> "salida/$(basename "$video").log" || estado=1
  tail -n 5 "salida/$(basename "$video").log"
done
cat salida/resultado.jsonl
sw_vers
ffmpeg -version | head -n 1
brew list --versions molten-vk
exit "$estado"
