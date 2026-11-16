#!/usr/bin/env bash
# Funciones de las recetas Unix. No se carga desde la receta de Windows.
FFMPEG_COMMIT=2a571b606854520cf89804d8030c8b328e621689
BTBN_COMMIT=6c9aec5fc9a72ec3abedd1fa84db141fa18cf52b
VERSION=n9.0.2-17-g2a571b6068-rodaje1
# GHCR, linux/amd64, creada 2026-10-31; ver docs/ffmpeg-lgpl-unix.md.
# shellcheck disable=SC2034 # Constante usada por la receta Linux, compartida con los otros llamadores.
IMAGEN_LINUX=ghcr.io/btbn/ffmpeg-builds/base-linux64@sha256:fa86b2845524170871766de2061b54974e1ef0ac5b4bdc2401b494c3f46d5968
BUILD_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

fallar() { echo "[ffmpeg-lgpl] ERROR: $*" >&2; exit 1; }
huella() {
    if command -v sha256sum >/dev/null; then sha256sum "$1"; else shasum -a 256 "$1"; fi | awk '{print $1}'
}
descargar_fuente() {
    local nombre="$1" entrada="$2" linea version archivo sha url licencia
    linea="$(grep -F "${nombre}|" "$BUILD_DIR/ffmpeg-lgpl-fuentes.txt")"
    IFS='|' read -r nombre version archivo sha url licencia <<< "$linea"
    [[ -n "$archivo" && "$sha" =~ ^[0-9a-f]{64}$ ]] || fallar "Fuente no fijada: $nombre"
    printf '[fuente] %s %s (%s)\n' "$nombre" "$version" "$licencia" >&2
    mkdir -p "$entrada"
    if [[ ! -f "$entrada/$archivo" && -n "${RODAJE_FFMPEG_FUENTES:-}" ]]; then
        if [[ -f "$RODAJE_FFMPEG_FUENTES/$archivo" ]]; then
            cp "$RODAJE_FFMPEG_FUENTES/$archivo" "$entrada/$archivo"
        elif [[ -f "$RODAJE_FFMPEG_FUENTES/tarballs/$archivo" ]]; then
            cp "$RODAJE_FFMPEG_FUENTES/tarballs/$archivo" "$entrada/$archivo"
        fi
    fi
    if [[ ! -f "$entrada/$archivo" ]]; then
        curl --fail --location --retry 3 --proto '=https' --proto-redir '=https' \
            "$url" -o "$entrada/$archivo.part"
        [[ "$(huella "$entrada/$archivo.part")" == "$sha" ]] || fallar "SHA-256 de $nombre"
        mv "$entrada/$archivo.part" "$entrada/$archivo"
    fi
    [[ "$(huella "$entrada/$archivo")" == "$sha" ]] || fallar "SHA-256 de $nombre (cache)"
    # Ruta de salida para el llamador; los mensajes de curl van a stderr.
    printf '%s\n' "$entrada/$archivo"
}

copiar_receta() {
    local fuente="$1" f
    mkdir -p "$fuente/build" "$fuente/.github/workflows" "$fuente/docs"
    for f in ffmpeg-lgpl-comun.sh ffmpeg-lgpl-fuentes.txt compilar-ffmpeg-lgpl.sh \
             compilar-ffmpeg-lgpl-linux.sh compilar-ffmpeg-lgpl-mac.sh auditar-ffmpeg.sh \
             probar-ffmpeg-lgpl.sh probar-auditoria-ffmpeg.sh preparar-entorno.ps1; do
        cp "$BUILD_DIR/$f" "$fuente/build/"
    done
    cp "$BUILD_DIR/../.github/workflows/ffmpeg-lgpl.yml" "$fuente/.github/workflows/"
    cp "$BUILD_DIR/../global.json" "$fuente/"
    cp "$BUILD_DIR/../docs/ffmpeg-lgpl-unix.md" "$fuente/docs/"
}

# $paquete es la carpeta interior ya auditada; $fuente contiene las fuentes exactas y metadatos.
empaquetar() {
    local paquete="$1" fuente="$2" salida="$3" nombre rid tarbin tarfuente f
    nombre="$(basename "$paquete")"
    rid="$(basename "$fuente")"
    tarbin="$salida/$nombre.tar.xz"
    tarfuente="$salida/$rid.tar.xz"
    cp "$paquete/configure.txt" "$paquete/auditoria.txt" "$salida/"
    cp "$paquete/configure.txt" "$paquete/auditoria.txt" "$fuente/"
    copiar_receta "$fuente"
    tar -cJf "$tarbin.part" -C "$(dirname "$paquete")" "$nombre"
    mv "$tarbin.part" "$tarbin"
    printf '%s  %s\n' "$(huella "$tarbin")" "$nombre.tar.xz" > "$tarbin.sha256"
    {
        printf 'Fuente correspondiente de %s\nSHA-256: %s\n\n' "$nombre.tar.xz" "$(huella "$tarbin")"
        printf 'FFmpeg: %s\nBtbN (Linux): %s, tag autobuild-2026-09-30-13-08\n' "$FFMPEG_COMMIT" "$BTBN_COMMIT"
        printf 'Reproduccion y herramientas: docs/ffmpeg-lgpl-unix.md, build/ y .github/workflows/ffmpeg-lgpl.yml.\n'
        printf 'Tarballs originales sin modificar; etapas/ (Linux) son los arboles montados por Dockerfile.generado.\n'
        printf 'build/ aplica las exclusiones y los ajustes de rutas; configure.txt describe el binario.\n'
        printf 'Los textos de licencia originales permanecen en las fuentes. No se publica una release.\n\n'
        printf 'Huellas SHA-256 (rutas relativas a esta carpeta):\n'
        (cd "$fuente"; find . -type f ! -name LEEME.txt | LC_ALL=C sort | while IFS= read -r f; do
            printf '%s  %s\n' "$(huella "$f")" "${f#./}"
        done)
    } > "$fuente/LEEME.txt"
    tar -cJf "$tarfuente.part" -C "$(dirname "$fuente")" "$rid"
    mv "$tarfuente.part" "$tarfuente"
    printf '%s  %s\n' "$(huella "$tarfuente")" "$rid.tar.xz" > "$tarfuente.sha256"
    echo "[ffmpeg-lgpl] $tarbin"
}
