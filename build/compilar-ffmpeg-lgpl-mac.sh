#!/usr/bin/env bash
# macos-15 arm64. Uso: bash build/compilar-ffmpeg-lgpl-mac.sh [directorio de salida]
# Homebrew aporta solo herramientas; las ocho bibliotecas se compilan desde fuentes verificadas. zlib es la del sistema
# (/usr/lib/libz): sin ella, --disable-autodetect deja fuera el codificador PNG y las cabeceras comprimidas de MOV.
set -euo pipefail
# shellcheck source=build/ffmpeg-lgpl-comun.sh
source "$(dirname "$0")/ffmpeg-lgpl-comun.sh"
[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || fallar 'Se necesita macOS arm64 nativo'
for herramienta in nasm meson ninja cmake pkg-config xcrun make curl tar xz codesign; do command -v "$herramienta" >/dev/null; done
salida="${1:-$PWD/salida-ffmpeg/macarm64}"
mkdir -p "$salida"
salida="$(cd "$salida" && pwd -P)"
# Los sistemas de construccion de algunas dependencias no admiten espacios en el prefijo.
[[ "$salida" != *[[:space:]]* ]] || fallar 'La ruta de compilacion Mac no debe contener espacios'
trabajo="$salida/trabajo"
mkdir "$trabajo"
prefijo="$trabajo/deps"
fuente="$trabajo/rodaje-ffmpeg-lgpl-fuente-osx-arm64"
paquete="$trabajo/paquete/ffmpeg-$VERSION-macarm64-lgpl-shared-9.0"
mkdir -p "$prefijo" "$fuente/tarballs" "$trabajo/src" "$paquete/licencias"
export MACOSX_DEPLOYMENT_TARGET=13.0
SDKROOT="$(xcrun --sdk macosx --show-sdk-path)"; export SDKROOT
CC="$(xcrun -f clang)"; CXX="$(xcrun -f clang++)"; export CC CXX
export CFLAGS='-O2 -fPIC -arch arm64' CXXFLAGS='-O2 -fPIC -arch arm64 -stdlib=libc++'
export CPPFLAGS="-I$prefijo/include" LDFLAGS="-L$prefijo/lib"
export PKG_CONFIG_PATH='' PKG_CONFIG_LIBDIR="$prefijo/lib/pkgconfig:$prefijo/share/pkgconfig"
unset CPATH C_INCLUDE_PATH CPLUS_INCLUDE_PATH LIBRARY_PATH CMAKE_PREFIX_PATH PKG_CONFIG_SYSROOT_DIR
unset DYLD_LIBRARY_PATH DYLD_FALLBACK_LIBRARY_PATH DYLD_INSERT_LIBRARIES
trabajos="$(sysctl -n hw.logicalcpu)"
(( trabajos <= 6 )) || trabajos=6
{
    sw_vers
    xcodebuild -version
    "$CC" --version
    printf 'MACOSX_DEPLOYMENT_TARGET=%s\nSDKROOT=%s\n' "$MACOSX_DEPLOYMENT_TARGET" "$SDKROOT"
    brew list --versions nasm meson ninja cmake pkg-config
    nasm -v; meson --version; ninja --version; cmake --version; pkg-config --version
} > "$fuente/herramientas.txt"

extraer() {
    local nombre="$1" archivo f
    archivo="$(descargar_fuente "$nombre" "$fuente/tarballs")"
    mkdir "$trabajo/src/$nombre"
    tar -xf "$archivo" -C "$trabajo/src/$nombre" --strip-components=1
    mkdir "$paquete/licencias/$nombre"
    # Incluye avisos y concesiones de patentes, tambien los de codigo de terceros anidado.
    (cd "$trabajo/src/$nombre"; find . -type f \( -iname '*license*' -o -iname '*copying*' -o -iname '*patent*' -o -iname '*notice*' \) -print) |
        while IFS= read -r f; do
            mkdir -p "$paquete/licencias/$nombre/$(dirname "$f")"
            cp "$trabajo/src/$nombre/$f" "$paquete/licencias/$nombre/$f"
        done
}
for nombre in openh264 dav1d libvpx opus lame svt-av1 kvazaar libwebp FFmpeg; do extraer "$nombre"; done

# OpenH264: el objetivo install-static no construye el ejecutable ni la biblioteca compartida.
# Su .pc estatico usa libstdc++ por defecto; Darwin requiere la libc++ del sistema.
make -C "$trabajo/src/openh264" -j"$trabajos" OS=darwin ARCH=arm64 \
    BUILDTYPE=Release DEBUGSYMBOLS=False PREFIX="$prefijo" LIBDIR_NAME=lib \
    STATIC_LDFLAGS=-lc++ install-static

meson setup "$trabajo/dav1d-build" "$trabajo/src/dav1d" --prefix="$prefijo" --libdir=lib \
    --buildtype=release --default-library=static --wrap-mode=nodownload -Db_staticpic=true \
    -Denable_tools=false -Denable_tests=false -Denable_examples=false
meson compile -C "$trabajo/dav1d-build" -j "$trabajos"
meson install -C "$trabajo/dav1d-build"

(
    cd "$trabajo/src/libvpx"
    # En libvpx, darwin sin numero selecciona iOS; Darwin 24 es macOS 15.
    ./configure --prefix="$prefijo" --target=arm64-darwin24-gcc --enable-static --disable-shared \
        --enable-pic --enable-vp9-highbitdepth --disable-examples --disable-tools --disable-docs --disable-unit-tests
    make -j"$trabajos"
    make install
)
(
    cd "$trabajo/src/opus"
    ./configure --prefix="$prefijo" --enable-static --disable-shared --with-pic --disable-extra-programs --disable-doc
    make -j"$trabajos"
    make install
)
(
    cd "$trabajo/src/lame"
    # Su config.guess de 2017 confunde Darwin moderno con x86_64.
    ./configure --prefix="$prefijo" --enable-static --disable-shared --with-pic \
        --build=aarch64-apple-darwin --host=aarch64-apple-darwin \
        --disable-frontend --disable-decoder --disable-cpml --disable-nasm
    make -j"$trabajos"
    make install
)

cmake -S "$trabajo/src/svt-av1" -B "$trabajo/svt-build" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$prefijo" -DCMAKE_INSTALL_LIBDIR=lib \
    -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET="$MACOSX_DEPLOYMENT_TARGET" \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DBUILD_SHARED_LIBS=OFF -DBUILD_TESTING=OFF -DBUILD_APPS=OFF \
    -DSVT_AV1_LTO=OFF -DNATIVE=OFF -DENABLE_SVE=OFF -DENABLE_SVE2=OFF
cmake --build "$trabajo/svt-build" --parallel "$trabajos"
cmake --install "$trabajo/svt-build"
(
    cd "$trabajo/src/kvazaar"
    ./configure --prefix="$prefijo" --enable-static --disable-shared --with-pic --without-cryptopp
    make -j"$trabajos"
    make install
)
# El .pc de Kvazaar no declara estas necesidades de su enlace estatico (igual que BtbN).
printf '\nCflags.private: -DKVZ_STATIC_LIB\nLibs.private: -lpthread\n' >> "$prefijo/lib/pkgconfig/kvazaar.pc"

cmake -S "$trabajo/src/libwebp" -B "$trabajo/webp-build" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$prefijo" -DCMAKE_INSTALL_LIBDIR=lib \
    -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET="$MACOSX_DEPLOYMENT_TARGET" \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DBUILD_SHARED_LIBS=OFF \
    -DWEBP_BUILD_ANIM_UTILS=OFF -DWEBP_BUILD_CWEBP=OFF -DWEBP_BUILD_DWEBP=OFF \
    -DWEBP_BUILD_GIF2WEBP=OFF -DWEBP_BUILD_IMG2WEBP=OFF -DWEBP_BUILD_VWEBP=OFF \
    -DWEBP_BUILD_WEBPINFO=OFF -DWEBP_BUILD_WEBPMUX=OFF -DWEBP_BUILD_EXTRAS=OFF \
    -DWEBP_BUILD_LIBWEBPMUX=ON -DWEBP_BUILD_FUZZTEST=OFF
cmake --build "$trabajo/webp-build" --parallel "$trabajos"
cmake --install "$trabajo/webp-build"
[[ -z "$(find "$prefijo" -name '*.dylib' -o -name '*.so*')" ]] || fallar 'Una dependencia se construyo compartida'

(
    cd "$trabajo/src/FFmpeg"
    # Los archivos de GitHub no contienen .git. VERSION evita tomar el Git del proyecto padre.
    printf '%s\n' n9.0.2-17-g2a571b6068 > VERSION
    ./configure --prefix="$paquete" --cc="$CC" --cxx="$CXX" --arch=aarch64 --target-os=darwin \
        --enable-version3 --enable-shared --disable-static --disable-gpl --disable-nonfree \
        --disable-autodetect --enable-zlib --disable-ffplay --disable-doc --disable-debug \
        --enable-videotoolbox --enable-audiotoolbox \
        --enable-libopenh264 --enable-libdav1d --enable-libvpx --enable-libopus \
        --enable-libmp3lame --enable-libsvtav1 --enable-libkvazaar --enable-libwebp \
        --pkg-config-flags=--static --extra-version=rodaje --install-name-dir=@rpath \
        --extra-cflags="$CFLAGS -I$prefijo/include" --extra-cxxflags="$CXXFLAGS -I$prefijo/include" \
        --extra-ldflags="-L$prefijo/lib -Wl,-rpath,@loader_path/../lib -Wl,-headerpad_max_install_names" \
        --extra-libs=-lc++
    make -j"$trabajos"
    make install
    cp ffbuild/config.log config.h ffbuild/config.mak "$fuente/"
    cp COPYING.LGPLv3 "$paquete/LICENSE.txt"
)
# Los Mach-O arm64 requieren una firma valida; ad-hoc, sin identidad ni secreto de Apple.
while IFS= read -r -d '' f; do codesign --force --sign - "$f"; done < <(find "$paquete/lib" -type f -name '*.dylib' -print0)
codesign --force --sign - "$paquete/bin/ffmpeg" "$paquete/bin/ffprobe"
bash "$BUILD_DIR/auditar-ffmpeg.sh" "$paquete" mac
empaquetar "$paquete" "$fuente" "$salida"
