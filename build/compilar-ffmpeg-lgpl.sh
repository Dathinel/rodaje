#!/bin/bash
# Corre dentro de un contenedor docker:cli con el socket de Docker; lo lanza build/compilar-ffmpeg-lgpl.ps1.
# Uso: compilar-ffmpeg-lgpl.sh preparar|descargar|compilar <imagen base-win64 con huella> <commit de FFmpeg> <etapas a quitar...>
#   $W     volumen con nombre montado en la misma ruta que en el demonio, para que los "docker run -v $PWD/..." de
#          BtbN apunten a carpetas reales
#   /entrada tarball de los scripts de BtbN en el commit fijado
#   /salida  zip de la compilación y la fuente de cada etapa
#
# Es la compilación win64-lgpl-shared de BtbN sin cambiar nada más que: las etapas quitadas (sus scripts y su línea en
# zz-final.sh), lcms2 sin sus plugins GPL, la imagen base fijada por huella en vez de :latest, FFmpeg en el commit fijado en vez de la punta de
# release/9.0 y --extra-version=rodaje en vez de la fecha.
set -eo pipefail

ACCION="$1"
IMAGEN="$2"
FFMPEG_COMMIT="$3"
shift 3
QUITAR=("$@")

W=/var/lib/docker/volumes/rodaje-ffmpeg-lgpl/_data
cd "$W"
# Los temporales de download.sh y build.sh se montan en otros contenedores: tienen que estar en el volumen.
export TMPDIR="$W/tmp"
mkdir -p "$TMPDIR"

preparar() {
    rm -rf btbn.tmp && mkdir btbn.tmp
    tar -xzf /entrada/FFmpeg-Builds-*.tar.gz -C btbn.tmp --strip-components=1
    cd btbn.tmp
    for etapa in "${QUITAR[@]}"; do
        rm -rf scripts.d/??-"$etapa" scripts.d/??-"$etapa".sh
        sed -i "/^    echo $etapa\$/d" scripts.d/zz-final.sh
    done
    # Los plugins fast_float y threaded de Little CMS son GPL-3.0 (el núcleo es MIT): sin ellos.
    sed -i 's#-Dfastfloat=true#-Dfastfloat=false#; s#-Dthreaded=true#-Dthreaded=false#' scripts.d/50-libjxl/45-lcms2.sh
    grep -q -- '-Dfastfloat=false' scripts.d/50-libjxl/45-lcms2.sh && grep -q -- '-Dthreaded=false' scripts.d/50-libjxl/45-lcms2.sh \
        || { echo "No se pudieron quitar los plugins GPL de lcms2"; exit 1; }
    # Las etapas de C++ pesado compilan con 6 trabajos como mucho: con -j$(nproc) (16) vvenc y harfbuzz pasan de 6 GB.
    for s in 50-vvenc.sh 47-vulkan/50-shaderc.sh 47-vulkan/55-spirv-cross.sh 50-libjxl/*.sh 50-libplacebo.sh 50-openmpt.sh 50-aom.sh \
             45-fonts/45-harfbuzz.sh 50-libaribcaption.sh 50-librsvg/*.sh; do
        sed -i 's#\$(nproc)#$(( $(nproc) < 6 ? $(nproc) : 6 ))#g' scripts.d/$s
    done
    grep -q 'nproc) < 6' scripts.d/50-vvenc.sh || { echo "No se pudo limitar vvenc"; exit 1; }
    sed -i 's#^to_df "FROM ${REGISTRY}/${REPO}/base-${TARGET}:latest${DOCKER_TAG_SUFFIX:-} AS ${BASELAYER}"#to_df "FROM '"$IMAGEN"' AS ${BASELAYER}"#' generate.sh
    grep -q "FROM $IMAGEN AS" generate.sh || { echo "No se pudo fijar la imagen en generate.sh"; exit 1; }
    sed -i 's#"${REGISTRY}/${REPO}/base:latest${DOCKER_TAG_SUFFIX:-}"#"'"$IMAGEN"'"#' download.sh
    grep -q "$IMAGEN" download.sh || { echo "No se pudo fijar la imagen en download.sh"; exit 1; }
    sed -i 's#^    cd ffmpeg$#    cd ffmpeg \&\& git checkout '"$FFMPEG_COMMIT"'#' build.sh
    sed -i 's#--extra-version="\\$(date +%Y%m%d)"#--extra-version=rodaje#' build.sh
    grep -q "git checkout $FFMPEG_COMMIT" build.sh && grep -q -- '--extra-version=rodaje' build.sh \
        || { echo "No se pudo fijar FFmpeg en build.sh"; exit 1; }
    cd "$W"
    # Lo bajado (.cache/downloads y la caché de buildx) se conserva entre ejecuciones.
    [[ -d btbn/.cache ]] && mv btbn/.cache btbn.tmp/.cache
    rm -rf btbn && mv btbn.tmp btbn
}

case "$ACCION" in
    preparar) preparar ;;
    descargar)
        cd btbn
        ./download.sh
        ;;
    compilar)
        cd btbn
        rm -rf artifacts
        # El mismo constructor que crea makeimage.sh, pero acotado para no ahogar el equipo: memoria RODAJE_MEMORIA
        # (8g) y una etapa a la vez. makeimage.sh lo reutiliza porque ya existe; si existe con otros límites, se
        # recrea conservando su caché.
        limites="memory=${RODAJE_MEMORIA:-8g} paralelo=1"
        if ! docker buildx inspect ffbuilder &>/dev/null || [[ "$(cat "$W/limites-constructor" 2>/dev/null)" != "$limites" ]]; then
            docker buildx rm --keep-state ffbuilder &>/dev/null || true
            docker buildx create --bootstrap --name ffbuilder \
                --buildkitd-flags "--oci-max-parallelism=1" --driver-opt network=host \
                --driver-opt env.BUILDKIT_STEP_LOG_MAX_SIZE=-1 --driver-opt env.BUILDKIT_STEP_LOG_MAX_SPEED=-1 \
                --driver-opt "memory=${RODAJE_MEMORIA:-8g}" --driver-opt "memory-swap=${RODAJE_MEMORIA:-8g}"
            echo "$limites" >"$W/limites-constructor"
        fi
        sed -i "s#^docker run --rm -i \$TTY_ARG#docker run --rm -i --memory ${RODAJE_MEMORIA:-8g} \$TTY_ARG#" build.sh
        QUICKBUILD=1 ./makeimage.sh win64 lgpl-shared 9.0
        ./build.sh win64 lgpl-shared 9.0
        # Fuente: exactamente los tarballs que monta el Dockerfile generado (download.sh baja también los de GPL).
        rm -rf /salida/fuente.tmp && mkdir -p /salida/fuente.tmp
        grep -o '\.cache/downloads/[^,]*\.tar\.xz' Dockerfile | sort -u | while read -r f; do cp "$f" /salida/fuente.tmp/; done
        rm -rf /salida/fuente && mv /salida/fuente.tmp /salida/fuente
        cp Dockerfile /salida/Dockerfile.generado
        cp artifacts/*.zip /salida/
        ;;
    *) echo "Acción desconocida: $ACCION"; exit 2 ;;
esac
