# Hoja de ruta de Rodaje: de la 1.0.3 a la colaboración (Daniel, 19-nov-2026)

Orden de trabajo de Rodaje, con el mismo método que Unicomp: bloques en orden, cada uno con sus pasos, su «Hecho cuando» y su estado. Lo abierto por prioridad está en `pendientes.md`; lo que solo Daniel puede juzgar con el ratón, en `pruebas-a-mano.md`; lo cerrado, en `CHANGELOG.md`.

**Daniel, 19-nov:** sacar la 1.0.4 de Windows y, después, la colaboración. **Sin Linux de momento** (queda aparcado al final de este documento; Mac también: exige registro de Apple).

## Cómo se trabaja

1. **Un bloque, una rama.** Cada bloque va en su rama y su worktree (`.claude\worktrees\`), con un PR por tema, CI verde y squash. Nada de auto-merge, `--admin` ni push forzado. No se toca el PR #461 («NO UNIR»).
2. **Un jefe por bloque.** El jefe reparte lo mecánico con la **Cuadrilla** (ChatGPT o Devin para lo que bloquea; Gemini para documentos, guías y revisiones que corren al lado; nunca Gemini en lo que bloquea) y revisa cada diff. No se hace a mano lo que la Cuadrilla puede hacer.
3. **Revisión independiente por bloque, no por pieza.** Al cerrar un bloque, otro modelo lo revisa entero (lógica, estructura, ahorro de tokens, preguntas). Lo corto se arregla en el momento; lo largo entra en `pendientes.md`.
4. **Estado de cada paso:** ☐ sin empezar · ◐ en curso (con jefe, rama o PR) · ✔ hecho (con PR). Se actualiza al unir, en el mismo PR.
5. **Al cerrar un bloque:** se quitan sus puntos de `pendientes.md`, entra una línea en `CHANGELOG.md` (`[Sin publicar]`) y lo que necesita el ratón de Daniel va a `pruebas-a-mano.md`. **No se para por eso**: se sigue con el bloque siguiente; lo que Daniel encuentre entra como corrección.
6. **PC compartido con otra sesión que toma datos.** Todo lo local va en segundo plano, con prioridad baja, `DOTNET_PROCESSOR_COUNT=2`, `-m:2` y una sola compilación o tanda a la vez. Si existe `D:\Programas de uso libre\medicion-en-curso.txt`, no se lanza nada local hasta que desaparezca (si lleva más de 40 min sin cambios, se pregunta a la sesión de Unicomp). **Nunca se abre Rodaje a la vista**: solo `--autoprueba`, `--arranque` y `--medir` en segundo plano con un exe publicado desde `main` actual.
7. **Solo se para por** publicar, licencias, decisiones irreversibles o claves. La clave privada del catálogo nunca se imprime ni se copia.
8. **El orden de abajo no se salta.** Un bloque que depende de otro no empieza antes de que el otro se una.

## Versión 1.0.4 (Windows)

### B1 · Integración con Sala
El protocolo de módulos vive en `Dathinel/sala` y Rodaje lo usa por el submódulo `externo/sala` (fase 1d de `propuestas/colaboracion.md` §8).
1. ◐ `Dathinel/sala#13`: aviso de `chrono` en `cargo deny` y lectura de la identidad con reintentos en Windows.
2. ☐ `Dathinel/sala#12`: protocolo con su historial. Se une **sin squash**; el historial prueba la autoría que usa `docs/legal.md`.
3. ◐ `rodaje#618`: submódulo y referencias repuntadas. La CI del trabajo Uno en Linux falló en «Publicar y autoprueba»: se mira el registro antes de unir. No se une antes del paso 2.
**Hecho cuando:** un clon limpio con `--recurse-submodules` compila y pasa las pruebas en Windows, Linux y macOS, con el submódulo en un commit que está en `main` de Sala.

### B2 · Rendimiento: keyframes de texto y material de muchos fps
Reporte de un usuario real: con un texto «tambaleando» y muchos keyframes, el editor se satura (a veces «el sonido explota o la imagen se congela») y un video de muchos fps se traba.
1. ◐ Prueba de consola que reproduce 50, 200, 1000 y 5000 keyframes en un título y mide añadir, deshacer, evaluar por cuadro, línea de tiempo, inspector y guardado.
2. ☐ Arreglo de la causa, con cifras antes y después.
3. ☐ Presets de texto «Tambaleo» y «Temblor», para no poner cientos de keyframes a mano.
4. ☐ Audio con la interfaz bloqueada: un vaciado de búfer no puede sonar como un estallido ni congelar la imagen.
5. ☐ Reproducción de material de 120 y 240 fps en una línea de 30 o 60.
**Hecho cuando:** `docs/rendimiento-keyframes.md` trae las cifras; con 1000 keyframes añadir, reproducir y guardar no dan tirones; la reproducción de 120 fps no se atasca.

### B3 · Defectos y cabos sueltos
1. ☐ La autoprueba del exe AOT se cierra a veces (2 de 5) con `0xC000027B`: causa y arreglo.
2. ☐ Pruebas intermitentes de la CI (`Modules.Protocol`, `RenderCacheJobTests`): estabilizarlas de verdad.
3. ◐ Filtro `&` de `build\huellas-pixeles.ps1`: `rodaje#619`.
4. ☐ Tareas cortas que dejaron las revisiones de bloque (por ejemplo la duplicación entre `FrameInterpolator` y `SurfaceInterpolator`), solo si son cortas.
**Hecho cuando:** 5 autopruebas seguidas sin cierre anómalo y la CI sin intermitentes conocidas.

### B4 · SDK de .NET 10.0.403
Aceptado por Daniel por un aviso de vulnerabilidad del 10.0.12. Empieza cuando B1 esté unido, por los `packages.lock.json`.
1. ☐ `global.json`, `build\preparar-entorno.ps1` (versión y hashes SHA-512 de los tres RID, de `releases.json` de Microsoft), ILCompiler y paquetes de runtime, locks regenerados, `docs/legal.md` y `LICENCIAS.md` si citan versiones.
2. ☐ Lo mismo en `Dathinel/sala`.
**Hecho cuando:** compila sin advertencias, pasan las pruebas y la autoprueba, y la CI sale en verde en los tres sistemas.

### B5 · Verificación de la versión candidata
En segundo plano y frente a la base de la 1.0.3.
1. ☐ Compilación sin advertencias y solución de pruebas de Windows (≈11 800).
2. ☐ Autoprueba del exe AOT publicado desde `main`, 5 corridas.
3. ☐ Arranque, ms de GPU por cuadro y tamaño frente a la 1.0.3 (el envío de trabajo a la GPU con 8 capas subió un 21 % en la fase 3: aceptado).
**Hecho cuando:** las cifras están en `CHANGELOG.md` y ninguna empeora sin explicación.

### B6 · Preparar la publicación (sin publicar)
1. ☐ Versión subida donde la subió la 1.0.3 (`Directory.Build.props`).
2. ☐ `CHANGELOG.md` de la 1.0.4 en lenguaje de usuario, README y guías al día.
3. ☐ `pruebas-a-mano.md` con la lista corta y ordenada de lo nuevo que Daniel debe probar con el ratón.
4. ☐ `docs/publicar-1.0.4.md` con los pasos exactos que quedan, marcando los que usan la clave privada.
**Hecho cuando:** `build\distribuir.ps1` arma instalador, zip y notas sin errores.

### B7 · Revisión final
1. ☐ Un modelo distinto revisa el diff `v1.0.3..main` completo; lo corto se corrige.
**Hecho cuando:** sin hallazgos graves abiertos.

### B8 · Publicación (Daniel)
1. ☐ Catálogo firmado, etiqueta y release. **Solo con el OK de Daniel.**

## Después de la 1.0.4 (sin Linux)

### B9 · Repaso con ratón, repetido hasta que quede bien
Lo nuevo desde la 1.0.3: render en caché, editar por texto, intercambio EDL/FCPXML/OTIO, flujo óptico, plantillas de movimiento, HDR de 12 bits y los arreglos de B2 y B3. **Daniel, 19-nov: se revisa varias veces hasta que quede bien.** Este bloque va **antes de publicar** (entre B7 y B8) y se repite en vueltas:
1. Vuelta: una sesión de Claude con control de pantalla recorre `pruebas-a-mano.md` con el exe de `main` actual y escribe `repaso-raton-<fecha>.md` (✔, ✘ y no probado, gravedad y pasos).
2. Se arreglan todos los ✘ «bloquea» y «molesta» (jefe + Cuadrilla, PR con CI verde) y se publica un exe nuevo de `main`.
3. Nueva vuelta, empezando por lo que falló y siguiendo con el recorrido completo.
**Hecho cuando:** una vuelta completa sin ✘ «bloquea» ni «molesta» y, como mínimo, dos vueltas hechas. Los ✘ «cosmético» van a `pendientes.md`. Daniel lo repasa al final con el ratón solo para confirmar.

### B10 a B16 · Colaboración (D-067, `propuestas/colaboracion.md`)
Rodaje usa `Dathinel/sala`, que ya trae lo central (red, roles, reservas, traspaso y entradas hostiles; pruebas de Sala en verde en los tres sistemas). Falta todo el lado de Rodaje, en este orden:

| Bloque | Fase del diseño | Qué | Hecho cuando |
|---|---|---|---|
| B10 | 1a | Parches en Core: `ContentKey`, `ContentHash` (v16 con lector y escritor), `PatchDiff`, `PatchApply`, `CollabHistory`, `WireSanitizer` | 10 000 secuencias aleatorias de parches convergen igual en dos réplicas |
| B11 | 1c | `CollabSession`, `RoomService`, `MediaMatch` y las vistas de Invitar y Unirse | Tres Rodaje en la misma red editan a la vez y ven a los demás |
| B12 | 2 | Pruebas hostiles en Rodaje | Ninguna entrada hostil tumba al anfitrión ni escribe fuera de la carpeta |
| B13 | 3 | Internet: relé, billetes con relé, «Solo por relé» y el cortafuegos de Windows explicado | Casa ↔ datos móviles conectan y dicen si es directo o por relé; latencia p95 < 250 ms por relé |
| B14 | 4 | Proxies del anfitrión: almacén por `ContentHash`, medio «solo proxy», «Pedir proxy» | Un invitado sin los originales edita y oye con proxies |
| B15 | 5 | Anfitrión que se va: traspaso, suplente, parches pendientes y «Cambios que no entraron» | Apagar el anfitrión a mitad: nadie pierde cambios y, al volver, converge |
| B16 | 6 | Render del anfitrión: «Exportar en el anfitrión» y caché compartido | Un invitado lanza la exportación y sale en el anfitrión |

La versión de cada una la decide Daniel al cerrar B9 (propuesta: 1.0.5 con B10 a B12, el resto después).

## Aparcado
- **Linux, fases 9 y 10** (GPU de punta a punta, AppImage): necesitan la prueba de Daniel con el USB y la tarjeta NVIDIA. Fallos de Uno ya conocidos: tiempo del transporte en 0:00, `LineGeometry`, tamaño de ventana.
- **Mac, fase 11:** exige registro de Apple (Daniel, 17-nov).
- `modulo-raw` en Linux y Mac: no hay binarios de LibRaw.
- Reducción sin niveles de detalle (mips) en la exportación con Vulkan: excepción abierta y medida en `mac-y-linux-fase4.md`.
