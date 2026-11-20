# Publicar la 1.0.4: pasos que quedan

Nada de esto está hecho: publicar es irreversible y lo decide Daniel. Orden de la 1.0.3 (`CLAUDE.md`, «v1.0.3 publicada»; `docs/arranque-y-distribucion.md`). Las cifras de la verificación van en la sección final cuando se corran.

## Antes (sin clave)

1. `main` con la CI verde en los tres sistemas y sin PR de la versión pendientes (el #461 sigue siendo «NO UNIR»; no se publica nada desde él).
2. El PR «Rodaje 1.0.4» unido: sube `Version` en `Directory.Build.props` (1.0.3 → 1.0.4), cambia «Sin publicar» por «1.0.4 - AAAA-MM-DD» en `CHANGELOG.md` y deja `README.md`, `build/instalador/notas-version.md` y `docs/guia/instalar.md` con la 1.0.4.
3. `docs/pendientes-legales.md`: nada que bloquee (la 1.0.3 lo revisó; repasar solo lo que añadió Linux/macOS: FFmpeg de Unix ya está en su release y no entra en esta versión).
4. Repaso con ratón de `docs/pruebas-a-mano.md` hecho y sus hallazgos graves arreglados o anotados como conocidos en las notas.
5. En un `main` limpio y al día, sin otra sesión midiendo (`medicion-en-curso.txt` ausente):
   - `build\app.ps1 -Publicar -Autoprueba` (exe AOT; debe salir 0 y sin `rodaje-error.txt`).
   - `build\dotnet-local.cmd build Rodaje.slnx` sin advertencias y `build\probar.ps1` (o la CI de `main`).

## Armar y subir (sin la clave del catálogo)

6. `powershell -NoProfile -ExecutionPolicy Bypass -File build\distribuir.ps1` deja en `dist\distribucion\` el instalador, el portable, el completo, `SHA256SUMS.txt` y `Rodaje-1.0.4-notas.md`. Al final corre `build\probar-instalador.ps1`, que exige desinstalar antes el Rodaje de `D:\testting\Rodaje`; si no se quiere, `-SinComprobar` y usar `distribucion.yml` en la CI como en la 1.0.3.
   - Sin `RODAJE_CERTIFICADO` los ejecutables salen sin firma de código (SmartScreen avisa; está dicho en `docs/guia/instalar.md`).
   - El completo lleva los módulos de `dist\modulos` con su última versión (`modulo-raw` 1.1.0 incluido): no armar el índice solo con `-SoloIndice`.
7. `build\publicar-release.ps1 -Borrador` crea la release `v1.0.4` como borrador sobre el commit del exe (debe estar en `origin/main`). Revisar en GitHub (assets, tamaños, notas) y publicar el borrador; o repetirlo sin `-Borrador`.
8. Comprobar los hashes de la release con `gh release view v1.0.4 --json assets` frente a `SHA256SUMS.txt`.

## Con la clave privada del catálogo (solo Daniel)

9. **No hace falta catálogo nuevo** si no cambia ningún complemento: la 1.0.4 no añade `.paquete` al catálogo v9. Si se decide publicar algo (por ejemplo `Dathinel/rodaje-plantillas`, hoy privado, o una versión de módulo), siguen los pasos 2 a 5 de `docs/descarga.md` («Publicar una versión nueva»): `distribuir.ps1 -SoloIndice -Complementos ...`, `build\firmar-indice.ps1 -Indice dist\distribucion\indice.json` (usa `%USERPROFILE%\.rodaje-claves\catalogo-privada.pem`; no copiar ni imprimir esa clave), `gh release create v10 indice.json indice.json.sig -R Dathinel/rodaje-catalogo` y `Categoria=Red`.
10. Si en algún momento sube la versión mínima de la app de una entrada (`minApp`), usar «1.0.4» solo cuando la release ya exista.

## Después

11. `CLAUDE.md`: «v1.0.4 publicada», cifras y el siguiente paso; `docs/descarga.md` si cambió el catálogo.
12. Limpiar: `dist\distribucion` grande y worktrees de la versión.
