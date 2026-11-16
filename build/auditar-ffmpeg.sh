#!/usr/bin/env bash
# Uso: bash build/auditar-ffmpeg.sh <carpeta con bin/ y lib/> <linux|mac>
# Audita todos los binarios, antes de ejecutarlos. Un fallo deja el informe incompleto con ERROR.
set -euo pipefail
export LC_ALL=C
[[ $# == 2 && ( "$2" == linux || "$2" == mac ) ]] || { echo 'Uso: auditar-ffmpeg.sh <carpeta> <linux|mac>' >&2; exit 2; }
raiz="$(cd "$1" && pwd -P)"
sistema="$2"
informe="$raiz/auditoria.txt"
exec 3>&1
exec > "$informe" 2>&1
# shellcheck disable=SC2154 # estado se asigna dentro de la propia trampa.
trap 'estado=$?; trap - EXIT; cat "$informe" >&3; exit "$estado"' EXIT
fallar() { echo "ERROR: $*"; exit 1; }
echo "Auditoria FFmpeg LGPL v3: $sistema"
echo 'Alcance: dependencias declaradas, rutas de carga, arquitectura, licencia y buildconf.'
echo 'No sustituye la revision de licencias de codigo enlazado estaticamente ni de plugins cargados con dlopen.'
command -v file >/dev/null
if [[ "$sistema" == linux ]]; then command -v readelf >/dev/null; else command -v otool >/dev/null; fi
[[ -x "$raiz/bin/ffmpeg" && -x "$raiz/bin/ffprobe" ]] || fallar 'Faltan ffmpeg/ffprobe ejecutables'
[[ -d "$raiz/lib" ]] || fallar 'Falta lib/'

# No permitir que un enlace de la carpeta remita a Homebrew, al sistema o a un arbol de compilacion.
while IFS= read -r -d '' archivo; do
    enlace="$(readlink "$archivo")"
    [[ "$enlace" != */* && -f "$archivo" ]] || fallar "Enlace externo o roto: $archivo -> $enlace"
done < <(find "$raiz" -type l -print0)

propia_linux='^lib(avcodec|avformat|avutil|avfilter|avdevice|swresample|swscale)\.so\.[0-9]+(\.[0-9]+)*$'
propia_mac='^lib(avcodec|avformat|avutil|avfilter|avdevice|swresample|swscale)\.[0-9]+(\.[0-9]+)*\.dylib$'
permitida() {
    local dep="$1" nombre
    nombre="${dep##*/}"
    if [[ "$sistema" == linux ]]; then
        if [[ "$dep" =~ $propia_linux ]]; then [[ -f "$raiz/lib/$dep" ]]; return; fi
        case "$dep" in
            libc.so.6|libm.so.6|libpthread.so.0|libdl.so.2|librt.so.1|ld-linux-x86-64.so.2) return 0 ;;
        esac
    else
        case "$dep" in
            @rpath/*|@loader_path/../lib/*|@loader_path/*)
                [[ "$nombre" =~ $propia_mac && -f "$raiz/lib/$nombre" ]] || return 1
                [[ "$dep" == "@rpath/$nombre" || "$dep" == "@loader_path/../lib/$nombre" || "$dep" == "@loader_path/$nombre" ]]
                return ;;
            /usr/lib/*)
                [[ "$dep" =~ ^/usr/lib/lib(System|z|bz2|iconv|c\+\+)(\.[A-Za-z0-9]+)*\.dylib$ ]]; return ;;
            /System/Library/Frameworks/*)
                [[ "$dep" != *'/../'* ]]; return ;;
        esac
    fi
    return 1
}

cantidad=0
while IFS= read -r -d '' archivo; do
    tipo="$(file -b "$archivo")"
    case "$tipo" in *ELF*|*Mach-O*) ;; *)
        case "$archivo" in *.so|*.so.*|*.dylib|"$raiz/bin/"*) fallar "No es un binario auditable: $archivo ($tipo)" ;; esac
        continue ;;
    esac
    cantidad=$((cantidad + 1))
    printf '\nArchivo: %s\nTipo: %s\n' "${archivo#"$raiz/"}" "$tipo"
    if [[ "$sistema" == linux ]]; then
        [[ "$tipo" == *ELF* && "$tipo" == *x86-64* && "$tipo" == *64-bit* ]] || fallar 'Arquitectura distinta de linux-x64'
        dinamica="$(readelf -d "$archivo")"
        printf '%s\n' "$dinamica"
        deps="$(sed -n 's/.*(NEEDED).*\[\([^]]*\)\].*/\1/p' <<< "$dinamica")"
        [[ -n "$deps" ]] || fallar "Sin dependencias dinamicas: $archivo"
        # -E: el sed de macOS no entiende «\|» en expresiones básicas (la prueba del auditor corre también allí).
        rutas="$(sed -nE '/\((RPATH|RUNPATH)\)/s/.*\[([^]]*)\].*/\1/p' <<< "$dinamica")"
        while IFS= read -r ruta; do
            [[ -z "$ruta" ]] && continue
            IFS=: read -r -a partes <<< "$ruta"
            for parte in "${partes[@]}"; do
                # shellcheck disable=SC2016 # Token literal del cargador ELF.
                [[ "$parte" == '$ORIGIN' || "$parte" == '$ORIGIN/../lib' ]] || fallar "RPATH no portable: $parte"
            done
        done <<< "$rutas"
    else
        [[ "$tipo" == *Mach-O* && "$tipo" == *arm64* && "$tipo" != *universal* ]] || fallar 'Arquitectura distinta de osx-arm64'
        dinamica="$(otool -L "$archivo")"
        printf '%s\n' "$dinamica"
        deps="$(sed '1d; s/^[[:space:]]*//; s/ (compatibility version.*$//' <<< "$dinamica")"
        [[ -n "$deps" ]] || fallar "Sin dependencias dinamicas: $archivo"
        cargas="$(otool -l "$archivo")"
        rutas="$(awk '/cmd LC_RPATH/ {p=1; next} p && /path / {print $2; p=0}' <<< "$cargas")"
        while IFS= read -r ruta; do
            [[ -z "$ruta" || "$ruta" == '@loader_path' || "$ruta" == '@loader_path/../lib' ]] || fallar "LC_RPATH no portable: $ruta"
        done <<< "$rutas"
    fi
    while IFS= read -r dep; do
        [[ -n "$dep" ]] || continue
        permitida "$dep" || fallar "Dependencia no permitida: $dep ($archivo)"
    done <<< "$deps"
done < <(find "$raiz" -type f ! -name auditoria.txt -print0)
[[ "$cantidad" -ge 9 ]] || fallar 'Se esperaban al menos siete bibliotecas y dos ejecutables'

# Ejecutar sin rutas de bibliotecas heredadas: valida tambien la carga desde la carpeta portable.
unset LD_LIBRARY_PATH LD_PRELOAD DYLD_LIBRARY_PATH DYLD_FALLBACK_LIBRARY_PATH DYLD_INSERT_LIBRARIES
licencia="$("$raiz/bin/ffmpeg" -hide_banner -L 2>&1)"
printf '\nLicencia:\n%s\n' "$licencia"
grep -Eq 'GNU Lesser General Public License|LGPL' <<< "$licencia" || fallar 'ffmpeg -L no declara LGPL'
grep -Eq 'version 3|version3' <<< "$licencia" || fallar 'ffmpeg -L no declara version 3'
grep -Eq 'GNU General Public License|nonfree|non-free|unredistributable' <<< "$licencia" && fallar 'Licencia GPL/no libre'
conf="$("$raiz/bin/ffmpeg" -hide_banner -buildconf 2>&1)"
printf '\nBuildconf:\n%s\n' "$conf"
opciones="$(sed -n 's/^[[:space:]]*\(--.*\)$/\1/p' <<< "$conf")"
[[ -n "$opciones" ]] || fallar 'buildconf vacio'
for requerida in --enable-version3 --enable-shared --disable-static; do
    grep -Fxq -- "$requerida" <<< "$opciones" || fallar "Falta $requerida"
done
prohibidos=(gpl nonfree chromaprint libzvbi libx264 libx265 libxvid frei0r librubberband libvidstab libdavs2 libxavs2 libfdk-aac)
for prohibido in "${prohibidos[@]}"; do
    # Busca tambien =yes y la linea completa de configuracion, no solo opciones aisladas.
    grep -Eq -- "--enable-${prohibido}([=[:space:]'\"]|$)" <<< "$conf" && fallar "Opcion prohibida: --enable-$prohibido"
done
printf '%s\n' "$opciones" > "$raiz/configure.txt"
"$raiz/bin/ffprobe" -hide_banner -version
printf '\nRESULTADO: OK (%s binarios auditados)\n' "$cantidad"
