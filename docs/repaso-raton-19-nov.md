# Repaso con ratón del 19-20 nov (Claude con el ratón)

**Build:** `exe-repaso\dist\Rodaje\app\Rodaje.exe`, 1.0.3+487b4ca8 (`main` del 19-nov). **Equipo:** Windows 11, RTX 3050 Laptop, pantalla de 1920×1080. **Duración:** ~30 min de pantalla (23:47-00:17).
**Material:** videos sintéticos de ffmpeg (testsrc2 de 30 s, barras con ruido, una música de 30 s, un Mandelbrot de 10 s y un video con voz de Windows de 12 s con «Subtítulos automáticos» ya instalado). Proyectos y archivos de intercambio que dejé: `prueba-raton\proyectos\` (sin los videos).

## Cómo leer esto (límites del control de pantalla)

- Los controles de WinUI del inspector, los combos, los sliders y los botones pequeños **solo respondieron al clic con «mover el ratón encima, esperar ~0,5 s y pulsar»**; los arrastres, con pulsar, mover y soltar. Un clic directo se pierde. Con un ratón real esto no debería pasar; por eso lo trato como límite de la herramienta y no como fallo.
- **Ctrl/Mayús+clic no llegó a la app** (no multiselecciona): usé recuadro de selección y Ctrl+A. Por eso «Montaje al ritmo» y «Sincronizar por el sonido» quedan sin concluir.
- El clic y el arrastre sobre **las palabras del panel Transcripción** no funcionaron con ninguna de las 6 formas que probé (clic, doble clic, Mayús+clic, hover+clic, pulsar y soltar a mano, arrastre). Los demás controles del panel (búsqueda, botones) sí. No sé si es un fallo de la app o del control; lo dejo como ✘ probable.
- No puedo oír: lo del audio (chasquidos, distorsión, «va a saltos») queda sin juzgar.

## Resultado por apartado

| Apartado | Resultado | Notas |
|---|---|---|
| Editar por el texto | **✘** | Pasos 1, 2, 3, 6, 7 y 10 (parte) ✔; paso 4 ✘ (F2, F3); pasos 5, 8, 9 no probados (sin selección con ratón; F2 no abre el editor, ver F6) |
| Exportar entre la entrada y la salida | ✔ | Los 6 pasos; el archivo dura 4,167 s |
| Cabecera de pista (solo) | ✔ | Pasos 1, 2, 4, 5 (Ctrl+Z); el 3 (Ctrl+clic) no concluyente; 5 (guardar y abrir) y 6 (atajo) no probados |
| Asas de fundido de audio | ✔ | Globo «Entrada 6,4 s», Ctrl+Z y Ctrl+Y en un paso, «Fundido de entrada hasta el cursor» (4,4 s); pasos 2, 4, 6 y 7 no probados |
| Montaje al ritmo | no probado | Sin multiselección fiable (ver R1) |
| Sincronizar por el sonido | no probado | Solo vi el aviso de lugar ocupado (ver R2) |
| Portapapeles | ✔ | Pasos 2 (insertando y Ctrl+Z en un paso) y 4 (a un proyecto vacío); 1, 3 y 5 no probados |
| CinemaDNG (GPU y HDR) | no probado | No hay material DNG |
| Intercambio con otros editores | ✔ | Exportar OTIO, FCPXML y EDL; importar OTIO y deshacer en un paso; sin Resolve, sin importar EDL ni FCPXML (R3, R4) |
| Render en caché | no probado | Solo vi que existe, que se calcula solo («14 calculados») y la ficha de Preferencias (R5) |
| Plantillas de movimiento | ✔ (parcial) | No hay paquete (`rodaje-plantillas` es privado): probé «Guardar como plantilla…» y su uso |
| Cuadros intermedios (mezcla y flujo óptico) | ✔ (parcial) | Una pasada a 0,5×; sin 0,25×, 4K ni cambio de plano |
| **Extra: título con muchos keyframes** | sin reproducir | Ver la sección propia |

Total: **7 ✔** (varios parciales), **1 ✘**, **4 no probados**, más el extra.

## Fallos y rarezas

**F1 · Eliminar por texto: el clip de subtítulos que crea «Subtítulos automáticos» bloquea el Supr de una frase** · *molesta (parece bloquear el flujo del guion)*
1. Clip de voz con «Subtítulos automáticos» → Aplicar (4 subtítulos). 2. Ver › Transcripción; escribir «el camion pasa por la calle principal» en la búsqueda y Enter (7 palabras seleccionadas); clic en una zona vacía del panel y Supr.
Esperaba: la frase y su video se van y la línea se cierra (el subtítulo coincide con la frase). Pasó: aviso «No se puede cerrar el hueco porque en la pista «Subtítulos» hay un clip que atraviesa ese tramo». Vuelve a pasar con otra frase (el corte llega unos 0,05 s más allá y toca el subtítulo siguiente).

**F2 · Eliminar por texto: borró mucho más que lo seleccionado** · *posible bloquea la versión (borrado de datos)*, a confirmar con ratón real
1. Con la voz partida en dos (cortar en 6,3 s) y los subtítulos 1-3 quitados, buscar «el camion pasa por la calle principal» (7 seleccionadas) → Supr.
Esperaba: perder solo esas 7 palabras (~2,3 s). Pasó: el texto bajó de 29 a 14 palabras (se fueron también «Hola, esto es una prueba del editor» y el «Eh,») y la secuencia de 11,925 s a 5,592 s: desapareció la mitad entera. Ctrl+Z lo devuelve. Pasó las dos veces que lo probé. Sin captura (la herramienta no guarda las de pantalla completa; los archivos de prueba están en `prueba-raton\proyectos\texto.rodaje`).

**F3 · Panel Transcripción: la palabra seleccionada o que suena se pinta ilegible** · *molesta*
Búsqueda o reproducción: la palabra resaltada sale con fondo oscuro y texto oscuro (en una frase de 7 palabras seleccionadas solo se lee «Eh,»). Captura: `C:\Users\danie\.claude\projects\D--Proyectos-Programas-de-uso-libre-Rodaje--claude-worktrees-hoja-ruta\0b57270c-d000-4d8b-89a5-e3c97e58b07a\tool-results\mcp-computer-use-blob-1795150251113-r95m96.jpg`.

**F4 · Panel Transcripción: clic y arrastre sobre las palabras no seleccionan ni mueven el cursor** · *molesta (probable; ver los límites arriba)*
Sin esto, el paso a paso del guion (clic y Mayús+clic, Retroceso, Ctrl+X/V, F2, Alt+1) no se puede hacer con ratón; solo la búsqueda selecciona.

**F5 · Una excepción en un hilo de exportación cierra la app entera** · *molesta en un PC normal; grave si pasa en el de Daniel*
La app se lanzó desde un shell con `TEMP=C:\WINDOWS\TEMP` (sin permiso). Al exportar, `ExportOutputGuard.DeleteTemporary` (desde `ExportJob.Run`, hilo propio) lanzó `UnauthorizedAccessException` y la app se cerró («La app se cerró: sí», diálogo de Windows de «saturación de un búfer basado en pilas», aviso «Rodaje no se cerró bien 2 veces» al volver a abrir). Esperaba: que fallara solo el trabajo de exportación. Informe: `%LOCALAPPDATA%\Rodaje\fallos\2026-11-19_23-54-06-980.txt`. Una carpeta TEMP sin permiso es rara, pero cualquier error de E/S ahí debería dar «Falló» en Trabajos.
Nota: el otro instante de `rodaje-error.txt` (23:55:50) es de la autoprueba que corrió el otro agente con el mismo exe, no mía.

**F6 · Teclado en el inspector** · *cosmético*
Con el foco en el botón de keyframe, **Espacio** alterna la reproducción en vez de pulsar el botón (Intro sí lo pulsa). Con una palabra seleccionada, **F2** no abrió el editor de palabra (lo que tecleé después fue a los atajos de la línea de tiempo: añadió una región y cambió la herramienta).

**R1 · Detectar ritmo con todo seleccionado (Ctrl+A)**
Analizó la voz (93,8 BPM, «4 pulsos»; la música sintética es de 120) y «Ajustar al ritmo los otros 6 clips» dejó la **música en un trozo de ~0,3 s**; Ctrl+Z lo devolvió. Falta saber si, con solo la música como referencia, hace lo esperado.

**R2 · Sincronizar por el sonido** con 4 clips de audio: «No se pudo hacer. El lugar está ocupado por el clip «clip-d4b6cf9395e34d54ae6a83ce1938e30e» en la pista «Audio 1»»: el aviso muestra el id interno en vez del nombre («voz»). · *cosmético*

**R3 · Informe de exportación a FCPXML:** cada aviso sale sin su TC de la secuencia (el guion dice que lo trae). · *cosmético*
**R4 · EDL:** además de `texto.edl` escribe `texto_V2.edl` y `texto_V3.edl` de 43 bytes (pistas sin clips). · *cosmético*

**R5 · Preferencias › Rendimiento:** en una ventana de 1440×900 solo veo «Calcular en segundo plano» y «Carpeta»; no aparecen el tope de GB ni «Borrar el render en caché» (¿el cuadro no se desplaza?). · *molesta, a confirmar*

**R6 · Exportar con solo la entrada marcada:** la opción dice «(0:04 – 0:11)» en vez de «(0:04 – final)». · *cosmético*

**R7 · Asa de fundido:** el cuadrito sigue siendo de ~5 px y está en la esquina del clip; mis dos primeros arrastres rápidos recortaron el clip (historial «Recortar inicio») o crearon un recuadro de selección. Con el cursor encima ~1 s y pulsar-arrastrar-soltar sí funcionó (el asa está al final de la curva del fundido, no en la esquina, cuando ya hay fundido). Ya estaba en la revisión del 1-nov. · *molesta*

**R8 · Avisos que no se van:** «No se pudo hacer…» (amarillo) se quedó puesto al pasar a un proyecto nuevo; el ✕ del aviso «Rodaje no se cerró bien» no respondió al clic (probablemente por el control, ver arriba). · *cosmético*

**R9 · Al abrir otro proyecto, la pista Video 2 se pintó un rato con una tira de miniaturas del proyecto anterior** (desapareció sola al repintar). · *cosmético*

## Extra: título «tambaleando» con muchos keyframes

No pude dar las ~60 altas a mano: los diamantes de keyframe del inspector solo se activaron por teclado (Tab hasta el botón y Intro) y cada alta exige un paso más de teclado; **medí lo que se podía sin ellas**:

- **Una alta a mano** (valor 10° con Intro y diamante activo, inicio del título): quedó en «1 keyframe» en menos de 2 s, sin trabas visibles.
- **Proyectos hechos con un script** (título «Texto» de 5 s con keyframes lineales de posición X y rotación alternando ±40 px y ±12°): `temblor60` (60 keyframes), `temblor300` y `temblor1000` (1000). Se abren sin avisos, el inspector dice «1000 keyframes» y «500 keyframes» por parámetro.
- **Reproducción** del título en 720p con la GPU: 1,3 s de CPU en 3 s con 60 keyframes y 1,5 s con 1000 (poco más de media vuelta de un núcleo); la barra de estado sigue en «sin fotogramas perdidos»; el visor y el inspector siguen al valor (se ve el temblor). Con 1000 keyframes tampoco se traba el inspector ni la línea de tiempo.
- **Una alta entre dos keyframes con 1000 ya puestos** (mover el deslizador de rotación de −7,4 a 81,4 a los 2,65 s): el valor cambió al instante, pero el contador siguió en «500 keyframes» por parámetro y el título quedó en «Sin guardar»: no vi que apareciera un keyframe nuevo. Conviene que quien arregle esto compruebe si esa vía crea o descarta el cambio.
- **Audio distorsionado:** no lo puedo juzgar (no oigo, y el título no lleva audio).
- Lo que sí falta para reproducir el reporte: la satura­ción pudo venir de **alta tras alta con la reproducción en marcha**, de **keyframes de varios parámetros a la vez con el ratón sobre el visor** (se queda dibujando el recorrido) o del **historial** (cada alta es un paso); desde el ratón no lo pude montar.

## Lo más grave

1. **F2: Supr en el panel Transcripción borró 15 palabras y 6 s con 7 seleccionadas** (y F1 lo bloquea del todo cuando hay subtítulos): revisar `TranscriptEdits` con un clip partido.
2. **F5: un error de E/S en el hilo de exportación tumba la app** (excepción no controlada en `ExportOutputGuard.DeleteTemporary`).
3. **F4/F3: el panel Transcripción no se puede usar con ratón sobre las palabras y el resaltado es ilegible** (si F4 se confirma con ratón real, el flujo principal de «Editar por el texto» queda cojo).

## Cómo dejé el PC

Rodaje cerrado (sin guardar los cambios de prueba), en la carpeta de datos de Rodaje quedan los informes de fallo del 23:54 (el de F5) y del 23:58 (el aviso de cierre incorrecto) en `fallos\`; borrada la plantilla de prueba `zz-prueba-raton` de `complementos\propios`; el aviso del sistema que dejó el cierre se cerró solo. La línea «Rodaje: repaso con ratón» de `medicion-en-curso.txt` quitada (el archivo se borró por quedar vacío) y avisado «libre» a la sesión de Unicomp.
