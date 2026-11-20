#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "=== CI Vulkan Video (Ubuntu 24.04 / linux-x64) ==="

if [ -z "${RODAJE_FFMPEG:-}" ]; then
    echo "ERROR: La variable de entorno RODAJE_FFMPEG no está definida."
    echo "Debe apuntar a la carpeta que contiene bin/ y lib/ del build LGPL de FFmpeg."
    exit 1
fi

echo "RODAJE_FFMPEG: $RODAJE_FFMPEG"
export LD_LIBRARY_PATH="${RODAJE_FFMPEG}/lib:${RODAJE_FFMPEG}/bin:${LD_LIBRARY_PATH:-}"
export PATH="${RODAJE_FFMPEG}/bin:${PATH:-}"

PUBLISH_DIR="${SCRIPT_DIR}/publish"

echo "Publicando con Native AOT para linux-x64..."
dotnet publish "${SCRIPT_DIR}/VulkanVideo.csproj" \
    -r linux-x64 \
    -c Release \
    /p:PublishAot=true \
    -o "$PUBLISH_DIR"

EXECUTABLE="${PUBLISH_DIR}/vulkan-video"
if [ ! -f "$EXECUTABLE" ]; then
    echo "ERROR: No se encontró el binario compilado en $EXECUTABLE"
    exit 1
fi

chmod +x "$EXECUTABLE"

echo "Ejecutando prueba de 60 segundos..."
"$EXECUTABLE" --segundos 60 --salida "${SCRIPT_DIR}/resultado.json"

if [ ! -f "${SCRIPT_DIR}/resultado.json" ]; then
    echo "ERROR: No se generó resultado.json"
    exit 1
fi

echo "Prueba completada exitosamente. Contenido de resultado.json:"
cat "${SCRIPT_DIR}/resultado.json"
