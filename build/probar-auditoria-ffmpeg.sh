#!/usr/bin/env bash
# Pruebas del control de dependencias con salidas simuladas de readelf/otool.
# No compila ni ejecuta binarios Linux/Mac. Necesita Bash, incluso en Git Bash de Windows.
set -euo pipefail
raiz="$(cd "$(dirname "$0")/.." && pwd)"
temporal="$(mktemp -d "$raiz/.prueba-auditoria.XXXXXX")"
trap 'rm -rf "$temporal"' EXIT
export FIXTURE="$temporal"
mkdir -p "$temporal/tools" "$temporal/paquete/bin" "$temporal/paquete/lib"
export PATH="$temporal/tools:$PATH"
cat > "$temporal/tools/file" <<'EOF'
#!/usr/bin/env bash
case "${@: -1}" in
    */bin/*|*.so.*|*.dylib)
        if [[ "$PLATAFORMA" == linux ]]; then echo 'ELF 64-bit LSB shared object, x86-64'; else echo 'Mach-O 64-bit dynamically linked shared library arm64'; fi ;;
    *) echo 'ASCII text' ;;
esac
EOF
cat > "$temporal/tools/readelf" <<'EOF'
#!/usr/bin/env bash
cat "$FIXTURE/dinamica.txt"
EOF
cat > "$temporal/tools/otool" <<'EOF'
#!/usr/bin/env bash
if [[ "$1" == -L ]]; then cat "$FIXTURE/dinamica.txt"; else cat "$FIXTURE/cargas.txt"; fi
EOF
cat > "$temporal/paquete/bin/ffmpeg" <<'EOF'
#!/usr/bin/env bash
case "$*" in
    *-buildconf*) cat "$FIXTURE/config.txt" ;;
    *-L*) cat "$FIXTURE/licencia.txt" ;;
    *) echo 'ffmpeg fixture' ;;
esac
EOF
cp "$temporal/paquete/bin/ffmpeg" "$temporal/paquete/bin/ffprobe"
chmod +x "$temporal/tools/"* "$temporal/paquete/bin/"*
cuenta=0
probar() {
    local esperado="$1" nombre="$2" estado=0
    bash "$raiz/build/auditar-ffmpeg.sh" "$temporal/paquete" "$PLATAFORMA" > "$temporal/log.txt" 2>&1 || estado=$?
    if [[ "$esperado" == ok ]]; then
        [[ "$estado" == 0 ]] || { cat "$temporal/log.txt"; echo "FALLO: $nombre"; exit 1; }
        grep -q 'RESULTADO: OK' "$temporal/paquete/auditoria.txt"
    else
        [[ "$estado" != 0 ]] || { echo "Acepto indebidamente: $nombre"; exit 1; }
        grep -q 'ERROR:' "$temporal/paquete/auditoria.txt"
        ! grep -q 'RESULTADO: OK' "$temporal/paquete/auditoria.txt"
    fi
    cuenta=$((cuenta + 1))
    echo "OK: $PLATAFORMA / $nombre"
}
restaurar() {
    printf 'GNU Lesser General Public License version 3 or later\n' > "$temporal/licencia.txt"
    printf '  --enable-version3\n  --enable-shared\n  --disable-static\n' > "$temporal/config.txt"
    : > "$temporal/cargas.txt"
    if [[ "$PLATAFORMA" == linux ]]; then
        printf ' (NEEDED) Shared library: [libc.so.6]\n (NEEDED) Shared library: [libavcodec.so.63]\n (RUNPATH) Library runpath: [$ORIGIN/../lib]\n' > "$temporal/dinamica.txt"
    else
        printf 'archivo:\n\t/usr/lib/libSystem.B.dylib (compatibility version 1.0.0, current version 1.0.0)\n\t@rpath/libavcodec.63.dylib (compatibility version 63.0.0, current version 63.0.0)\n' > "$temporal/dinamica.txt"
        printf 'cmd LC_RPATH\npath @loader_path/../lib (offset 12)\n' > "$temporal/cargas.txt"
    fi
}
for PLATAFORMA in linux mac; do
    export PLATAFORMA
    for lib in avcodec:63 avformat:63 avutil:61 avfilter:12 avdevice:63 swscale:10 swresample:7; do
        if [[ "$PLATAFORMA" == linux ]]; then nombre="lib${lib%:*}.so.${lib#*:}"; else nombre="lib${lib%:*}.${lib#*:}.dylib"; fi
        : > "$temporal/paquete/lib/$nombre"
    done
    restaurar
    probar ok 'paquete permitido y dependencias propias'
    printf '  --extra-cflags=-O2 -fPIC -I/prefijo/include\n' >> "$temporal/config.txt"
    probar ok 'configure conserva argumentos con espacios'
    grep -Fxq -- '--extra-cflags=-O2 -fPIC -I/prefijo/include' "$temporal/paquete/configure.txt"
    for flag in gpl nonfree chromaprint libzvbi libx264 libx265 libxvid frei0r librubberband libvidstab libdavs2 libxavs2 libfdk-aac; do
        restaurar
        printf '  --enable-%s\n' "$flag" >> "$temporal/config.txt"
        probar error "--enable-$flag"
    done
    restaurar
    printf '  --enable-gpl=yes\n' >> "$temporal/config.txt"
    probar error 'GPL con =yes'
    restaurar
    printf 'GNU General Public License version 3\n' > "$temporal/licencia.txt"
    probar error 'licencia GPL'
    restaurar
    printf ' --enable-shared\n --disable-static\n' > "$temporal/config.txt"
    probar error 'sin version3'
    restaurar
    if [[ "$PLATAFORMA" == linux ]]; then
        printf ' (NEEDED) Shared library: [libx264.so.164]\n' >> "$temporal/dinamica.txt"
    else
        printf '\t/opt/homebrew/opt/opus/lib/libopus.0.dylib (compatibility version 1.0.0, current version 1.0.0)\n' >> "$temporal/dinamica.txt"
    fi
    probar error 'dependencia externa'
    restaurar
    if [[ "$PLATAFORMA" == linux ]]; then
        printf ' (NEEDED) Shared library: [libavmaliciosa.so.63]\n' >> "$temporal/dinamica.txt"
    else
        printf '\t@rpath/libavmaliciosa.63.dylib (compatibility version 1.0.0, current version 1.0.0)\n' >> "$temporal/dinamica.txt"
    fi
    probar error 'prefijo libav no basta'
    restaurar
    if [[ "$PLATAFORMA" == linux ]]; then
        printf ' (RUNPATH) Library runpath: [/tmp/compilacion/lib]\n' >> "$temporal/dinamica.txt"
    else
        printf 'cmd LC_RPATH\npath /opt/homebrew/lib (offset 12)\n' >> "$temporal/cargas.txt"
    fi
    probar error 'ruta de carga absoluta'
    rm "$temporal/paquete/lib/"*
done
echo "$cuenta comprobaciones del auditor: OK"
