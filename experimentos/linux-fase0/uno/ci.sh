#!/usr/bin/env bash
# Parte (a): publica Fase0.Uno con Native AOT para el sistema del corredor y lo prueba.
# Linux (dentro de xvfb-run): arranca, carga un .rodaje, recibe 3 clics de xdotool en «Contar», uno en «Cerrar» y sale con 0.
# macOS: arranca, carga el proyecto y llega a «ventana lista» (sin clics: inyectarlos pide permisos de accesibilidad).
# Deja salida/resultado.json, salida/registro.txt, salida/publish.log y, en Linux, salida/captura.png.
set -uo pipefail
cd "$(dirname "$0")"
raiz="$(cd ../../.. && pwd)"
mkdir -p salida
rm -f salida/registro.txt salida/resultado.json
case "$(uname -s)" in
  Linux) rid=linux-x64 ;;
  Darwin) rid=osx-arm64 ;;
  *) echo "Sistema sin prueba: $(uname -s)"; exit 1 ;;
esac

t0=$(date +%s)
dotnet publish Fase0.Uno/Fase0.Uno.csproj -c Release -r "$rid" -p:PublishAot=true -p:TreatWarningsAsErrors=false -o salida/app > salida/publish.log 2>&1
publicar=$?
segundos_publicar=$(( $(date +%s) - t0 ))
tail -n 15 salida/publish.log
avisos_aot=$(grep -cE 'warning IL[0-9]+' salida/publish.log || true)
if [ "$publicar" -ne 0 ]; then echo "La publicación AOT falló."; exit 1; fi
exe=salida/app/fase0-uno
bytes_exe=$(wc -c < "$exe" | tr -d ' ')
bytes_carpeta=$(find salida/app -type f ! -name '*.dbg' ! -path '*.dSYM/*' -exec cat {} + | wc -c | tr -d ' ')

esperar() { # esperar <patrón> <segundos>
  for _ in $(seq 1 $(( $2 * 10 ))); do
    grep -q "$1" salida/registro.txt 2>/dev/null && return 0
    kill -0 "$pid" 2>/dev/null || return 1
    sleep 0.1
  done
  return 1
}

proyecto="$raiz/tests/Rodaje.Core.Tests/Serialization/ejemplos/proyecto-v16.rodaje"
LIBGL_ALWAYS_SOFTWARE=1 "$exe" --proyecto "$proyecto" --registro "$PWD/salida/registro.txt" > salida/app.log 2>&1 &
pid=$!
estado=0
clics=0
salida_app=-1
if ! esperar 'ventana_lista_ms' 60; then echo "La ventana no llegó a estar lista."; estado=1; fi

if [ "$estado" -eq 0 ] && [ "$rid" = linux-x64 ]; then
  ventana=$(xdotool search --onlyvisible --pid "$pid" 2>/dev/null | head -n 1)
  [ -n "$ventana" ] || ventana=$(xdotool search --onlyvisible --name 'Rodaje fase 0' | head -n 1)
  read -r _ x y < <(grep '^boton_contar' salida/registro.txt)
  for n in 1 2 3; do
    xdotool mousemove --window "$ventana" "$x" "$y" click 1
    esperar "clic: $n" 5 || { echo "El clic $n no llegó."; estado=1; break; }
    clics=$n
  done
  import -window root salida/captura.png 2>/dev/null || true
  read -r _ x y < <(grep '^boton_cerrar' salida/registro.txt)
  xdotool mousemove --window "$ventana" "$x" "$y" click 1
  for _ in $(seq 1 100); do kill -0 "$pid" 2>/dev/null || break; sleep 0.1; done
  if kill -0 "$pid" 2>/dev/null; then echo "La app no cerró con el botón."; estado=1; kill -9 "$pid"; else wait "$pid"; salida_app=$?; [ "$salida_app" -eq 0 ] || estado=1; fi
else
  kill "$pid" 2>/dev/null
fi

cat salida/registro.txt 2>/dev/null
cp salida/registro.txt salida/registro-prueba.txt 2>/dev/null

# Arranque en caliente: tres vueltas más, se anota la mejor.
caliente=
for _ in 1 2 3; do
  rm -f salida/registro.txt
  LIBGL_ALWAYS_SOFTWARE=1 "$exe" --proyecto "$proyecto" --registro "$PWD/salida/registro.txt" > /dev/null 2>&1 &
  pid=$!
  esperar 'ventana_lista_ms' 60
  kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null
  ms=$(sed -n 's/^ventana_lista_ms: //p' salida/registro.txt 2>/dev/null | head -n 1)
  if [ -n "$ms" ] && { [ -z "$caliente" ] || [ "$ms" -lt "$caliente" ]; }; then caliente=$ms; fi
done
cp salida/registro-prueba.txt salida/registro.txt 2>/dev/null
[ "$estado" -eq 0 ] || tail -n 40 salida/app.log
lista_ms=$(sed -n 's/^ventana_lista_ms: //p' salida/registro.txt 2>/dev/null | head -n 1)
cargo=$(grep -c '^proyecto: Proyecto de prueba; esquema 16' salida/registro.txt 2>/dev/null || true)
[ "$cargo" = 1 ] || estado=1
cat > salida/resultado.json <<EOF
{
  "rid": "$rid",
  "publicar_segundos": $segundos_publicar,
  "avisos_aot": $avisos_aot,
  "exe_bytes": $bytes_exe,
  "carpeta_bytes": $bytes_carpeta,
  "proyecto_cargado": $([ "$cargo" = 1 ] && echo true || echo false),
  "ventana_lista_ms": ${lista_ms:-null},
  "ventana_lista_caliente_ms": ${caliente:-null},
  "clics_recibidos": $clics,
  "codigo_de_salida": $salida_app,
  "pasa": $([ "$estado" -eq 0 ] && echo true || echo false)
}
EOF
cat salida/resultado.json
exit "$estado"
