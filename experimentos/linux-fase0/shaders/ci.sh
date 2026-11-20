#!/usr/bin/env bash
# Parte (d), Linux (Ubuntu 24.04 con lavapipe): publica ejecutar-vulkan con Native AOT para linux-x64 y lo corre contra los
# .spv y las referencias de WARP que dejó el trabajo de Windows (ci.ps1), descargados como artefacto.
#   FASE0_SPIRV: carpeta con manifiesto.json y los .spv (por defecto salida/spirv).
#   FASE0_REF:   carpeta con casos.json y los .bin (por defecto salida/ref).
#   RODAJE_VK_GPU: subcadena del nombre de la GPU (en el corredor, «llvmpipe»).
# Deja salida/resultado.json, salida/vulkan.txt y salida/publish.log. No instala nada: lavapipe (mesa-vulkan-drivers) y
# libvulkan1 los pone el workflow.
set -uo pipefail
cd "$(dirname "$0")"
mkdir -p salida
export FASE0_SPIRV="${FASE0_SPIRV:-$PWD/salida/spirv}"
export FASE0_REF="${FASE0_REF:-$PWD/salida/ref}"
export FASE0_RESULTADO="$PWD/salida/resultado.json"
for f in "$FASE0_SPIRV/manifiesto.json" "$FASE0_REF/casos.json"; do
  [ -f "$f" ] || { echo "Falta $f (artefacto del trabajo de Windows)."; exit 1; }
done

dotnet publish EjecutarVulkan/EjecutarVulkan.csproj -c Release -r linux-x64 -o salida/ejecutar-vulkan > salida/publish.log 2>&1
publicar=$?
tail -n 5 salida/publish.log
if [ "$publicar" -ne 0 ]; then echo "La publicación AOT falló."; exit 1; fi

salida/ejecutar-vulkan/ejecutar-vulkan 2>&1 | tee salida/vulkan.txt
exit "${PIPESTATUS[0]}"
