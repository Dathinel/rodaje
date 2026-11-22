# Repaso con ratón de Rodaje, vuelta 2 (20-nov)

Exe: `exe-repaso2\dist\Rodaje\app\Rodaje.exe` (`1.0.3+1acc594e50be`), RTX 3050 Laptop, 1920×1080. Sesión de pantalla de 04:35 a ~05:00. Material regenerado (testsrc2, barras, Mandelbrot, voz sintética de Windows, un clip de 120 fps) en `TestResults\prueba-raton2\`; proyectos de `_repro-repaso\proyectos`. No se corrigió nada.

## Cómo leerlo

- **Límites del control de pantalla** (no son de la app, pero limitan lo que probé): Mayús/Ctrl+clic no se entregan; el arrastre dentro del panel Transcripción no llega (ni con `left_click_drag` ni con pulsar-mover-soltar; con sliders y la línea de tiempo sí funciona); los clics sueltos necesitan un movimiento previo del ratón; no se puede oír el audio.
- ✔ = comprobado; ✘ = fallo; NP = no probado o no concluyente.
- Me deniegan la primera petición de acceso; la segunda se concedió (aviso de «No se cerró bien» al arrancar: es de mi cierre forzado previo, no de la app).

## 1. Correcciones de la vuelta 1

| Punto | Resultado | Detalle |
|---|---|---|
| F4 clic en palabra | ✔ | Un clic selecciona la palabra y el clip (antes no). |
| F4 arrastre / varias palabras | NP | El arrastre no extiende la selección con este control; no sé si es de la app. Enter en el buscador selecciona solo el primer resultado. Hay que probarlo con ratón real. |
| F2 Supr con una palabra («calle») | ✔ | Quita solo esa palabra: 27→26 palabras, clips 9→11 (partido), duración de la secuencia igual; Ctrl+Z vuelve a 27/9. |
| F2 Supr con una frase entera | NP | Es lo que importaba (la vuelta 1 quitaba media secuencia) y no pude seleccionar varias palabras. **Lo primero que debe probar Daniel con ratón real.** |
| F1 Supr con subtítulo creado | ✔ | Borrar «el» de la última frase ya no da el error de «clip que atraviesa»; el subtítulo se desplaza. |
| F3 palabra seleccionada / que suena | ✔ | Texto oscuro sobre naranja, legible; la que suena cambia de palabra al reproducir. |
| F5 exportar | ✔ | Tres exportaciones normales (3-4 s, 154-220 fps), sin cerrarse; aviso «Exportación terminada». |
| F5 cancelar | NP | El trabajo termina en 3-4 s, antes de poder cancelarlo. |
| F6 F2 abre el editor de palabra | ✔ | Sobre una palabra seleccionada abre el cuadro «editor.». Desde el buscador: NP. |
| F6 Espacio sobre botón con foco | NP | Solo indirecto (Espacio activó un botón del raíl con foco de teclado). |
| R1 Detectar ritmo con Ctrl+A | ✔ | «Ajustar al ritmo los otros 6 clips»: la música sigue en 30 s; clips ajustados; marcadores «Pulso»; Ctrl+Z lo deshace todo en un paso. El tono puro solo da 4 pulsos (83,9 BPM). |
| R2 nombre del clip en el aviso | ✔ | «El lugar está ocupado por el clip «voz» en la pista «Video 1»». |
| R3 FCPXML con TC | ✔ | El informe lista cada aviso con su TC (01:00:00:00, 01:00:02:19…). |
| R4 EDL sin pistas vacías | ✔ | Una sola `v2.edl` (1,5 KB). |
| R5 Preferencias › Rendimiento se desplaza | ✔ | Se llega a «Tope en GB» y «Borrar el render en caché». |
| R6 «– final» | NP | «Lo seleccionado» muestra «(0:05 – 0:25)»; no probé el caso de solo entrada. |
| R7 asa del fundido | ✔ | Pulsar a 5 px del asa la arrastra («Entrada 5,85 s»); a 10 px agarra el clip y lo mueve (Ctrl+Z lo devuelve). |
| R8 avisos | ✘ leve | Tras deshacer la importación EDL sigue el aviso «Faltan medios…» (queda obsoleto). |
| R9 miniaturas | ✔ | Al abrir otro proyecto no vi miniaturas del anterior. |

## 2. Keyframes y gráficos

| Prueba | Resultado | Tiempos / observación |
|---|---|---|
| `temblor300` y `temblor1000` abren | ✔ | Abre en ≤2 s; inspector «300/1000 keyframes». |
| Gráfica del inspector (Rotación) | ✔ | Se dibuja entera (150 y 500 rombos) en <0,5 s, sin tirones. El zoom con la rueda no respondió: NP (probablemente necesita Ctrl). |
| Arrastrar un rombo en la gráfica | ✔ | Sube/baja al instante y se puede deshacer con Ctrl+Z. |
| Rombos de la línea de tiempo (1000) | ✔ | Fila densa, sin lag. |
| Reproducir con 300 y 1000 | ✔ | Tiempo real (16,3→19,3 s en 3 s; 0 → 3,13 s en 3 s); «sin fotogramas perdidos». |
| Mover el cabezal (clic y arrastre en la regla) | ✔ | El visor actualiza en menos de ~0,8 s (mi espera mínima). |
| Arrastrar el texto en el visor | ✔ | Cambia X/Y al instante. |
| Alta manual de keyframes | ✔ | 34 altas a mano (cabezal + valor de Rotación): las 34 quedaron, 12 altas por tanda de 50 acciones en ~20 s, sin ralentización; la contador sube uno a uno. |
| Alta con la reproducción en marcha | NP | Mi control tarda >5 s en teclear una alta y el cabezal sale del clip. |
| Recorrido del visor | NP | No vi una línea de recorrido con `temblor1000` (solo hay X animada); probablemente solo aparece con X e Y. |

## 3. Textos animados nuevos

| Prueba | Resultado | Detalle |
|---|---|---|
| Texto › Animación, entrada y salida | ✔ | 12 preajustes + «Ninguna», con miniaturas que se animan en bucle (algunas aparecen vacías en la fase de entrada: esperable). |
| Movimiento continuo | ✔ parcial | Tambaleo se ve (rotación distinta en cada instante); Flotar varía poco; Pulso/Temblor: se aplican, no los distinguí con certeza de los keyframes del título. |
| «Palabra a palabra» | ✔ | Cada palabra aparece y se pinta la que suena. |
| «Karaoke» | ✔ | Barrido de color amarillo que avanza palabra a palabra (Modo «Barrido de color»). |
| «Pop» | ✔ | Entrada elástica por palabra, la que suena en amarillo. |
| «Líneas» | ✔ | Entra la línea entera con desvanecido. |
| Exportar = visor | ✔ | Cuadro a 10,786 s del MP4 exportado (extraído con ffmpeg) idéntico al visor (Karaoke): «…de la» amarillo y «grabación.» blanco. |

## 4. Lo que la vuelta 1 no pudo probar

| Prueba | Resultado | Detalle |
|---|---|---|
| Montaje al ritmo | ✔ | Ver R1. |
| Sincronizar por el sonido | ✔ parcial | Con un solo clip: «Selecciona dos clips con sonido o más: la referencia es el que empieza antes». Lugar ocupado: avisa con el nombre y no mueve nada. La alineación positiva (dos clips con la misma voz): NP. |
| Render en caché | ✔ parcial | Secuencia › Calcular de entrada a salida: franja verde en la regla, «Nada pendiente · 55 calculados», fila en Trabajos. «Borrar el render en caché»: NP (borraría la caché real de Daniel). |
| Cuadros intermedios 0,25× | ✔ | 0,25× + Flujo óptico: la duración pasa de 4,9 s a 19,7 s y se reproduce en tiempo real sin fotogramas perdidos. Exportación: NP esta vuelta. |
| Plantillas de movimiento | ✔ parcial | «Guardar como transición…» (necesita 2 keyframes): guarda en `complementos\propios\zz-prueba-raton.json`; aplicarla: NP. |
| Importar EDL | ✔ | «7 clips en un clip compuesto en pistas nuevas. 2 avisos» y «Faltan 2 medios» (rutas absolutas de la carpeta anterior); Ctrl+Z lo deshace en un paso. Al enlazar a mano: «Ese archivo ya es el de «voz»…» (coherente: ya existía ese medio). «Buscar en carpeta…»: «Se encontraron 0; faltan 2» por la misma razón. |
| Importar FCPXML | NP | Solo exporté. |

## 5. Reproducción

- Clip de 120 fps (8 s, H.264): reproduce con la barra «RTX 3050 Laptop · sin fotogramas perdidos» a ojo fluido; no medí con `--medir`.
- Línea de tiempo con 5 capas (título animado, subtítulo, vídeo, voz, música): fluido, sin fotogramas perdidos.
- Audio: no lo puedo oír; lo comprobará Daniel.
- Carga de fondo: la suite de Unicomp coincidía en parte con mi sesión (dotnet al 60-95 % de CPU al arrancar), así que la fluidez es una cota conservadora.

## Lo más grave que queda por comprobar

1. **F2/F4 con ratón real**: selección por arrastre y Supr sobre una frase entera en el panel Transcripción (lo que rompía la secuencia en la vuelta 1). No lo pude probar.
2. **Alta de keyframes con la reproducción en marcha** y con los 30-100 en una gráfica con zoom: no concluyente con este control.
3. Aviso «Faltan medios» obsoleto tras deshacer una importación (leve).

## Cómo dejé el PC

Rodaje cerrado sin guardar; borrados los medios y exportaciones de `prueba-raton2\` y la plantilla de prueba; quitada mi línea de `medicion-en-curso.txt`; sin commits ni cambios en repositorios salvo este archivo.
