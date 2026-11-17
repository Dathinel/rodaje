#!/usr/bin/env bash
# La app de Linux (Uno, src/Rodaje.App.Desktop): la publica con Native AOT para linux-x64 y, con --autoprueba, la corre
# bajo X (Xvfb en la CI) con Vulkan por software (lavapipe) si no hay GPU. Lo usa el trabajo «Interfaz Uno en Linux» de ci.yml.
#   build/app-linux.sh [--autoprueba]
# Deja en salida-linux/: publish.log, la app en app/ y, con --autoprueba, registro.txt, autoprueba.txt, app.log y capturas.
# La autoprueba usa el clip de TestResults/media-cache (h264-1080p-23976-aac-*.mp4); si no está, lo genera con el ffmpeg del sistema.
set -uo pipefail
cd "$(dirname "$0")/.."
raiz="$PWD"
salida="$raiz/salida-linux"
rm -rf "$salida"
mkdir -p "$salida"

t0=$(date +%s)
dotnet publish src/Rodaje.App.Desktop/Rodaje.App.Desktop.csproj -c Release -r linux-x64 -p:PublishAot=true -p:RestoreLockedMode=true -m:2 \
  -o "$salida/app" > "$salida/publish.log" 2>&1
estado=$?
echo "publicar: $(( $(date +%s) - t0 )) s, código $estado"
grep -E ' error ' "$salida/publish.log" | sed 's/^.*src\/Rodaje\.App\///' | sort -u | head -n 200
echo "errores: $(grep -cE ' error ' "$salida/publish.log" || true)"
echo "avisos IL: $(grep -oE 'warning IL[0-9]+' "$salida/publish.log" | sort | uniq -c | tr '\n' ' ')"
echo "avisos CA1416 (solo Windows): $(grep -E 'warning CA1416' "$salida/publish.log" | sort -u | wc -l)"
echo "avisos Uno0001 (sin implementar en Uno): $(grep -E 'warning Uno0001' "$salida/publish.log" | sort -u | wc -l)"
[ "$estado" -eq 0 ] || exit "$estado"
echo "exe: $(wc -c < "$salida/app/rodaje") bytes; carpeta: $(du -sb "$salida/app" | cut -f1) bytes"

[ "${1:-}" = "--autoprueba" ] || exit 0

clips="$raiz/TestResults/media-cache"
if ! ls "$clips"/h264-1080p-23976-aac-*.mp4 > /dev/null 2>&1; then
  mkdir -p "$clips"
  ffmpeg -v error -f lavfi -i testsrc2=size=1920x1080:rate=24000/1001:duration=5 -f lavfi -i sine=frequency=440:duration=5 \
    -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$clips/h264-1080p-23976-aac-ci.mp4" || exit 1
fi

# Datos de la corrida aparte: ajustes y cachés de fábrica, nada del usuario.
export RODAJE_DATOS="$salida/datos"
export LIBGL_ALWAYS_SOFTWARE=1
( for n in $(seq -w 1 30); do sleep 4; import -window root "$salida/captura-$n.png" 2>/dev/null || true; done ) &
capturas=$!
t0=$(date +%s%3N)
timeout 600 "$salida/app/rodaje" --autoprueba --registro "$salida/registro.txt" > "$salida/app.log" 2>&1
estado=$?
echo "autoprueba: $(( $(date +%s%3N) - t0 )) ms, código $estado"
kill "$capturas" 2>/dev/null
# Solo las capturas distintas de la anterior (la ventana quieta no aporta).
anterior=
for c in "$salida"/captura-*.png; do
  [ -e "$c" ] || continue
  suma=$(md5sum < "$c" | cut -d' ' -f1)
  if [ "$suma" = "$anterior" ]; then rm -f "$c"; fi
  anterior=$suma
done

cp "$RODAJE_DATOS/autoprueba.txt" "$salida/" 2>/dev/null

# Arranque en caliente (--arranque cierra en cuanto la interfaz responde): tres vueltas, cuenta la mejor.
if [ "$estado" -eq 0 ]; then
  for n in 1 2 3; do
    rm -f "$salida/arranque-$n.txt"
    timeout 120 "$salida/app/rodaje" --arranque --registro "$salida/arranque-$n.txt" > /dev/null 2>&1 || { echo "--arranque $n falló"; estado=1; }
  done
  echo "interfaz usable en caliente (ms): $(grep -h 'interfaz_usable_ms' "$salida"/arranque-*.txt | sed 's/.*: //' | sort -n | tr '\n' ' ')"
fi
grep -E 'autoprueba:|ventana_lista|interfaz_usable|primer_cuadro|cadena' "$salida/autoprueba.txt" "$salida/registro.txt" 2>/dev/null | head -n 40
if [ "$estado" -ne 0 ]; then
  tail -n 60 "$salida/app.log"
  cat "$salida/app/rodaje-error.txt" 2>/dev/null
  find "$RODAJE_DATOS" -path '*fallos*' -type f -exec sh -c 'echo "--- $1"; head -n 80 "$1"' _ {} \; 2>/dev/null
fi
exit "$estado"
