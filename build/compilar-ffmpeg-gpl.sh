#!/bin/bash
# Corre dentro de la imagen base-win64 de BtbN/FFmpeg-Builds; lo lanza build/compilar-ffmpeg-gpl.ps1.
# Uso: compilar-ffmpeg-gpl.sh descargar|compilar <versión de FFmpeg> <tarball de FFmpeg>
#   /btbn   scripts de BtbN en el commit fijado (solo lectura)
#   /fuente un tarball por etapa (lo que se compila es exactamente lo que se publica)
#   /salida ffmpeg.exe, su configuración y el registro
#
# Mismo procedimiento que BtbN (cada etapa con util/run_stage.sh y el configure que generan los scripts), pero solo con
# las etapas de ETAPAS: x264 y x265, los codificadores de audio de la exportación (libopus, libmp3lame) y sus
# dependencias. Sin buildx ni imagen intermedia, y la fuente sale de /fuente, no de la red.
set -eo pipefail

ETAPAS=(10-mingw 10-mingw-std-threads 20-libiconv 50-x264 50-x265 50-libopus 50-libmp3lame)
export TARGET=win64 VARIANT=gpl ADDINS_STR=9.0 FFVER=900 REPO=btbn/ffmpeg-builds

ACCION="$1"
FFMPEG_VERSION="$2"
FFMPEG_TARBALL="$3"

# Las funciones por defecto (ffbuild_*), las de la variante win64-gpl y el addin 9.0, como en generate.sh.
cargar_btbn() {
    cd /btbn
    source util/vars.sh "$TARGET" "$VARIANT" "$ADDINS_STR" >/dev/null
    source util/dl_functions.sh
}

descargar() {
    local etapa="$1" destino="/fuente/$1.tar.xz"
    [[ -f "$destino" ]] && { echo "== $etapa: ya descargada"; return 0; }
    echo "== Descargando $etapa"
    (
        cargar_btbn
        SELF="scripts.d/$etapa.sh"
        STAGENAME="$etapa"
        source "$SELF"
        STG="$(ffbuild_dockerdl)"
        trabajo="$(mktemp -d)"
        cd "$trabajo"
        eval "set -e; $STG"
        # Sin metadatos de control de versiones: el árbol es lo que se compila y lo que se publica.
        find . \( -name .git -o -name .svn \) -prune -exec rm -rf {} +
        # autogen.sh de libopus deja el archivo de modelos que ya extrajo en dnn/ (135 MB repetidos).
        rm -f opus_data-*.tar.gz
        tar --sort=name --mtime=@0 --owner=0 --group=0 --numeric-owner -cf - . | xz -9 -T1 >"$destino.part"
        mv "$destino.part" "$destino"
        rm -rf "$trabajo"
    )
}

compilar_etapa() {
    local etapa="$1"
    echo "== Compilando $etapa"
    ln -sfn "/fuente/$etapa.tar.xz" /cache.tar.xz
    (
        cd /btbn
        export SELF="/btbn/scripts.d/$etapa.sh" STAGENAME="$etapa"
        bash /btbn/util/run_stage.sh "$SELF"
    )
    rm -f /cache.tar.xz
    # La capa de cada etapa (ffbuild_dockerlayer): mingw instala en la raíz; el resto, en el prefijo.
    if [[ "$etapa" == 10-mingw ]]; then
        cp -a /opt/mingw/. /
        rm -rf /opt/mingw
    elif [[ -d "$FFBUILD_DESTPREFIX" ]]; then
        cp -a "$FFBUILD_DESTPREFIX"/. "$FFBUILD_PREFIX"/
    fi
    rm -rf "$FFBUILD_DESTDIR"
}

salida_de() {
    local etapa="$1" tipo="$2"
    (
        cargar_btbn
        SELF="scripts.d/$etapa.sh"
        source "$SELF"
        if ffbuild_enabled; then "ffbuild_$tipo" || true; else "ffbuild_un$tipo" || true; fi
    )
}

compilar() {
    for etapa in "${ETAPAS[@]}"; do compilar_etapa "$etapa"; done

    cargar_btbn
    source "variants/$TARGET-$VARIANT.sh"
    source "addins/$ADDINS_STR.sh"
    for etapa in "${ETAPAS[@]}"; do
        FF_CONFIGURE+=" $(salida_de "$etapa" configure)"
        FF_CFLAGS+=" $(salida_de "$etapa" cflags)"
        FF_CXXFLAGS+=" $(salida_de "$etapa" cxxflags)"
        FF_LDFLAGS+=" $(salida_de "$etapa" ldflags)"
        FF_LDEXEFLAGS+=" $(salida_de "$etapa" ldexeflags)"
        FF_LIBS+=" $(salida_de "$etapa" libs)"
    done
    FF_CONFIGURE="$(xargs <<<"$FF_CONFIGURE")"
    FF_CFLAGS="$(xargs <<<"$FF_CFLAGS")"
    FF_CXXFLAGS="$(xargs <<<"$FF_CXXFLAGS")"
    FF_LDFLAGS="$(xargs <<<"$FF_LDFLAGS")"
    FF_LDEXEFLAGS="$(xargs <<<"$FF_LDEXEFLAGS")"
    FF_LIBS="$(xargs <<<"$FF_LIBS")"

    echo "== Compilando FFmpeg $FFMPEG_VERSION"
    rm -rf /ffbuild && mkdir -p /ffbuild/ffmpeg
    tar -xzf "/fuente/$FFMPEG_TARBALL" -C /ffbuild/ffmpeg --strip-components=1
    cd /ffbuild/ffmpeg
    # Sin .git, version.sh toma la versión de este archivo (como en los tarballs de ffmpeg.org).
    echo "$FFMPEG_VERSION" >VERSION

    ./configure --prefix=/ffbuild/prefix --pkg-config-flags="--static" $FFBUILD_TARGET_FLAGS $FF_CONFIGURE \
        --extra-cflags="$FF_CFLAGS" --extra-cxxflags="$FF_CXXFLAGS" --extra-libs="$FF_LIBS" \
        --extra-ldflags="$FF_LDFLAGS" --extra-ldexeflags="$FF_LDEXEFLAGS" \
        --cc="$CC" --cxx="$CXX" --ar="$AR" --ranlib="$RANLIB" --nm="$NM" \
        --extra-version=rodaje || { cat ffbuild/config.log; exit 1; }
    make -j"$(nproc)"
    make install

    rm -rf /salida/*.part
    cp /ffbuild/prefix/bin/ffmpeg.exe /salida/ffmpeg.exe.part
    cp COPYING.GPLv3 /salida/COPYING.GPLv3.txt
    mv /salida/ffmpeg.exe.part /salida/ffmpeg.exe
    x86_64-w64-mingw32-gcc --version | head -1 >/salida/compilador.txt
}

case "$ACCION" in
    descargar) for etapa in "${ETAPAS[@]}"; do descargar "$etapa"; done ;;
    compilar) compilar ;;
    *) echo "Acción desconocida: $ACCION"; exit 2 ;;
esac
