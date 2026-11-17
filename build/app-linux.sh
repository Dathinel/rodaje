#!/usr/bin/env bash
# La app de Linux (Uno, src/Rodaje.App.Desktop): la publica con Native AOT para linux-x64 y, con --autoprueba, la corre
# bajo Xvfb con Vulkan por software (lavapipe). Lo usa el trabajo «Interfaz Uno en Linux» de ci.yml.
#   build/app-linux.sh [--autoprueba]
# Deja en salida-linux/: publish.log, la app en app/ y, con --autoprueba, registro.txt, resultado.json y capturas.
set -uo pipefail
cd "$(dirname "$0")/.."
salida="$PWD/salida-linux"
rm -rf "$salida"
mkdir -p "$salida"

t0=$(date +%s)
dotnet publish src/Rodaje.App.Desktop/Rodaje.App.Desktop.csproj -c Release -r linux-x64 -p:PublishAot=true -m:2 \
  -o "$salida/app" > "$salida/publish.log" 2>&1
estado=$?
echo "publicar: $(( $(date +%s) - t0 )) s, código $estado"
grep -E ' error |warning (IL|CA1416|Uno0001)' "$salida/publish.log" | sed 's/^.*src\/Rodaje\.App\///' | sort -u | head -n 400
echo "errores: $(grep -cE ' error ' "$salida/publish.log" || true)"
echo "avisos IL: $(grep -oE 'warning IL[0-9]+' "$salida/publish.log" | sort | uniq -c | tr '\n' ' ')"
echo "avisos CA1416: $(grep -cE 'warning CA1416' "$salida/publish.log" || true)"
[ "$estado" -eq 0 ] || exit "$estado"
ls -la "$salida/app" | head -n 5
du -sh "$salida/app"

[ "${1:-}" = "--autoprueba" ] || exit 0
exe="$salida/app/rodaje"
"$exe" --autoprueba --registro "$salida/registro.txt" --salida "$salida" > "$salida/app.log" 2>&1
estado=$?
tail -n 60 "$salida/registro.txt" 2>/dev/null || tail -n 60 "$salida/app.log"
exit "$estado"
