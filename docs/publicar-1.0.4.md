# Publicar la 1.0.4: pasos que quedan

Nada de esto está hecho: publicar es irreversible y lo decide Daniel. Orden de la 1.0.3 (`CLAUDE.md`, «v1.0.3 publicada»; `docs/arranque-y-distribucion.md`). Las cifras de la verificación van en la sección final cuando se corran.

## Antes (sin clave)

1. `main` con la CI verde en los tres sistemas y sin PR de la versión pendientes (el #461 sigue siendo «NO UNIR»; no se publica nada desde él). El submódulo externo `externo/sala` (repositorio `Dathinel/sala`) debe estar inicializado y al día (`git clone --recurse-submodules` o `git submodule update --init externo/sala`).
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
   `distribuir.ps1` también compila `dathinel-red` (colaboración) con `build\red.ps1` y lo deja en `app\modulos\red` de las tres versiones: necesita `git submodule update --init externo/sala` y tarda ~10 min la primera vez (Rust release con LTO; el Rust sale de `externo\sala\.entorno` o de `SALA_ENTORNO`). Pasa `cargo deny`, rechaza un binario con el feature `prueba` y la autoprueba del exe publicado comprueba que arranca (sin abrir red). `-SinRed` solo para pruebas del script.
7. `build\publicar-release.ps1 -Borrador` crea la release `v1.0.4` como borrador sobre el commit del exe (debe estar en `origin/main`). Revisar en GitHub (assets, tamaños, notas) y publicar el borrador; o repetirlo sin `-Borrador`.
8. Comprobar los hashes de la release con `gh release view v1.0.4 --json assets` frente a `SHA256SUMS.txt`.

## Con la clave privada del catálogo (solo Daniel)

9. **No hace falta catálogo nuevo** si no cambia ningún complemento: la 1.0.4 no añade `.paquete` al catálogo v9. Si se decide publicar algo (por ejemplo `Dathinel/rodaje-plantillas`, hoy privado, o una versión de módulo), siguen los pasos 2 a 5 de `docs/descarga.md` («Publicar una versión nueva»): `distribuir.ps1 -SoloIndice -Complementos ...`, `build\firmar-indice.ps1 -Indice dist\distribucion\indice.json` (usa `%USERPROFILE%\.rodaje-claves\catalogo-privada.pem`; no copiar ni imprimir esa clave), `gh release create v10 indice.json indice.json.sig -R Dathinel/rodaje-catalogo` y `Categoria=Red`.
10. Si en algún momento sube la versión mínima de la app de una entrada (`minApp`), usar «1.0.4» solo cuando la release ya exista.

## Después

11. `CLAUDE.md`: «v1.0.4 publicada», cifras y el siguiente paso; `docs/descarga.md` si cambió el catálogo.
12. Limpiar: `dist\distribucion` grande y worktrees de la versión.

## Cifras de la verificación (20-nov, `main` e9918174, RTX 3050 Laptop, PC sin otra carga)

- **Pruebas de Windows** (sin TiempoReal ni Rendimiento): todas pasan salvo tres de reloj, ya marcadas `TiempoReal`. **Autoprueba del exe publicado:** 10 de 10 en verde (68 a 99 s).
- **Base:** la 1.0.3 más #505 (la autoprueba y las medidas de fondo; sin él la 1.0.3 abriría ventana), compilada con el SDK 10.0.403. Mismo clip H.264 1080p y `--sin-audio`.
- **Arranque** (`--arranque`, medianas de 3 rondas de 5 vueltas, alternando base y candidata): en caliente, ventana lista 709 → 788 ms, primer render 841 → 924, interfaz usable 898 → 981, cola libre 229 → 230, GPU lista 1001 → 1115. En frío (archivos fuera de la caché de Windows): ventana lista 1092 → 1161, primer render 1227 → 1300, interfaz usable 1290 → 1364.
- **Reproducción** (`--medir 8 --escenario capas`, 5 corridas de cada una): CPU del proceso 118 % → 144 % de un núcleo (mediana; +22 %); edición a pantalla 2,7 → 2,4 ms y deslizador 2,2 → 2,8 ms (medianas). Composición en la GPU 0,11 ms por cuadro y CPU de render 0,15 ms por cuadro en la candidata; la base no escribe esas dos medidas.
- **Tamaño:** exe 37,6 → 43,8 MB; carpeta completa 243,7 → 264,7 MB; el instalador y el portable salen de `distribuir.ps1`, sin medir aquí.
- **Sin afinar:** el aumento de CPU viene de pasar el compositor a `IGpu` (fase 3) y lo aceptó Daniel; el arranque más lento es de la suma de B2 a B13, sin investigar.
