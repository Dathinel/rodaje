# Pendientes pedidos por Daniel

Ideas aprobadas que todavía no tienen fase. Las funciones numeradas se tachan al implementarse (se conserva el número porque Daniel las cita así); lo demás se quita y se anota en `decisiones.md`.

## Personalización

- **Pistas por defecto en un proyecto nuevo** (pedido el 10-oct): en Configuración se elige cuántas pistas de video y de audio aparecen al crear un proyecto.

## Complementos profesionales (pedido el 11-oct)

Daniel prefiere que lo profesional vaya como complemento aparte en su GitHub. Recomendación por pieza:

| Pieza | Dónde | Por qué |
|---|---|---|
| **10 bits** (decodificar, componer, exportar) | **En la app** | Ya es el camino interno: P010 por D3D11VA y composición en RGBA16F. Quitarlo costaría más que dejarlo; un complemento no podría cambiar el compositor. |
| **Looks de cámara** (S-Log3, V-Log, C-Log, BMDFilm, Log-C… a Rec.709) | Complemento `.paquete` (`rodaje-camaras`) | Son LUT `.cube`; se generan con fórmulas propias a partir de las curvas publicadas por los fabricantes, sin copiar sus LUT. |
| **Preajustes profesionales de exportación** (maestros ProRes/DNxHR, broadcast, cine) | Complemento `.paquete` (`rodaje-preajustes-pro`) | Son datos; la app ya los lee. |
| **RAW** (BRAW, R3D, CinemaDNG, ProRes RAW) | Módulo aparte (`modulo-raw`), proceso propio | Cada formato usa el SDK de su fabricante con su licencia; como proceso separado (igual que los módulos de IA) entrega cuadros al editor sin mezclar licencias. Necesita el protocolo de módulos de la fase 7 con video. |
| **Codificadores GPL** (x264, x265) | Módulo aparte (`rodaje-codificadores-gpl`) | Otro `ffmpeg.exe` por la misma tubería de la fase 6; frontera de proceso. |
| **Video 360°** (equirectangular: reencuadre a plano, metadatos esféricos, exportar 360) | El motor trae el código, oculto; el complemento `rodaje-360` lo activa con sus preajustes y controles (decidido por Daniel el 11-oct) | Necesita un shader de proyección en el compositor y los paquetes no traen shaders; así no entra código de terceros en la GPU. |

## Texto a voz (pedido el 11-oct)

- **Módulo `modulo-voz`** (complemento aparte en el GitHub de Dathinel, proceso propio como los demás módulos de IA de la fase 7): convertir texto en voz para videos tipo TikTok, con **varias voces y varios idiomas** (al menos español, inglés y portugués), ajuste de velocidad, tono y pausas, y vista previa antes de insertar.
- **En la app:** escribir o pegar el texto (o tomarlo de un clip de texto de la 5b o de un subtítulo de la fase 7) y generar el audio como clip en una pista de audio, alineado al cursor o al texto; volver a generar si se cambia el texto o la voz.
- **Local y sin coste:** modelos que corren en el PC (CPU o GPU), descargados con el módulo; elegir modelos con licencia que permita uso comercial y redistribución, y anotar cada voz y su licencia en el repo del módulo.

## Funciones de calidad de vida para elegir (11-oct)

Lista de funciones habituales en los editores populares (sin copiar diseño ni marcas: solo la idea). **Daniel las aprobó todas el 11-oct** y se reparten en la fase indicada, a agentes locales (L) o a la nube (N). Las que necesitan IA van como módulos de la fase 7.

### Edición en la línea de tiempo
1. ✓ ~~**Edición por ondas de silencio:** quitar silencios de un clip con un umbral y margen (con vista previa de lo que se corta). — 7 · L~~ — hecho (11-oct)
2. ✓ ~~**Cortar en el cursor en todas las pistas** de una vez y «borrar a la izquierda/derecha del cursor» (Q/W). — 5c · N~~ — hecho (11-oct)
3. ✓ ~~**Enlazar y desenlazar audio y video** con un clic; separar el audio a su pista. — 5c · N~~ — hecho (11-oct)
4. **Grupos y clips compuestos** (anidar varios clips en uno que se edita aparte). — 8 · L
5. ✓ ~~**Marcadores con nombre y color, y lista de marcadores** para saltar entre ellos. — 5c · N~~ — hecho (11-oct)
6. ✓ ~~**Ajuste al ritmo:** detectar golpes de la música y poner marcadores para cortar a tiempo. — 7 · L~~ — hecho (11-oct)
7. ✓ ~~**Copiar y pegar atributos** (efectos, transformación, velocidad) de un clip a otros. — 5c · N~~ — hecho (11-oct)
8. ✓ ~~**Reemplazar medio** manteniendo duración, efectos y keyframes. — 5c · N~~ — hecho (11-oct)
9. ✓ ~~**Zoom a la selección y «ajustar a la ventana»** en la línea de tiempo. — 5c · N~~ — hecho (11-oct)
10. **Edición de tres puntos y de cuatro puntos** (entrada/salida en origen y en secuencia). — 8 · L

### Imagen
11. **Recorte (crop) con relación de aspecto** y encuadre por clip. — 5b · L
12. **Estabilizar** (análisis y suavizado). — 7 · L
13. **Reencuadre automático** a 9:16 o 1:1 siguiendo al sujeto. — 7 · L
14. **Quitar fondo** (ya en el catálogo como módulo). — 7 · L
15. **Croma (pantalla verde)** con rango, suavizado y supresión de derrame. — 5b · L
16. **Máscaras** (rectángulo, elipse, trazo) con difuminado y keyframes. — 8 · L
17. **Comparar antes/después** del color en el visor (dividido). — 5b · N
18. **Rueda de color y curvas** (además de los deslizadores actuales). — 5b · L
19. **Ajuste automático de color y de balance de blancos**. — 7 · L

### Audio
20. **Reducción de ruido** de voz. — 7 · L
21. **Normalizar sonoridad del clip** (LUFS) y de toda la mezcla. — 6 · N
22. ✓ ~~**Ducking automático:** bajar la música cuando hay voz. — 7 · L~~ — hecho (11-oct)
23. **Ecualizador y compresor sencillos** por clip y por pista. — 8 · L
24. **Grabar voz en off** desde el micrófono directo a una pista. — 8 · L
25. **Biblioteca de efectos de sonido** (complemento `.paquete`). — complemento · N

### Texto y subtítulos
26. **Plantillas de títulos animados** (complemento, con la 5b). — 5b · L
27. **Subtítulos automáticos** (ya en el catálogo) con estilos y edición por palabras. — 7 · L
28. **Traducir subtítulos** a otro idioma (local). — 7 · L
29. **Texto a voz** (ver arriba). — 7 · L

### Proyecto y flujo de trabajo
30. ✓ ~~**Autoguardado y recuperación** tras un cierre inesperado (con historial de versiones). — 5c · N~~ — hecho (11-oct)
31. **Proxies automáticos al importar** con indicador de progreso (ya existe en parte). — 6 · N
32. **Relink de medios** movidos o renombrados (buscar en una carpeta). — 6 · N
33. ✓ ~~**Plantillas de proyecto** (formatos 16:9, 9:16, 1:1 con pistas y estilos). — 5c · N~~ — hecho (11-oct)
34. **Exportar un fotograma** como PNG/JPEG. — 6 · N
35. **Cola de exportaciones múltiples** y exportar varias versiones a la vez. — 6 · L
36. **Atajos personalizables** (ya existe) y **perfiles de espacio de trabajo** (distribución de paneles). — 8 · N
37. **Modo de pantalla completa del visor con controles** y segunda pantalla. — 8 · L
38. **Biblioteca con carpetas, búsqueda, favoritos y metadatos** (resolución, duración, códec). — 6 · N
39. **Importar desde el teléfono o carpetas vigiladas.** — 8 · L
40. **Tutorial inicial y pistas contextuales** para quien empieza. — 8 · L
