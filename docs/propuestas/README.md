# Propuestas

Diseños antes de construir. Cada uno trae fases con criterio de terminado, el reparto con la Cuadrilla y sus preguntas con la opción recomendada.

## Plan aprobado por Daniel el 15-nov-2026 (después de la 1.0.3)

Daniel prefiere números de versión seguidos: lo que se llamaba 1.1 sale como 1.0.3. Orden aprobado:

| Orden | Bloque | Diseño | Decisión |
|---|---|---|---|
| 1 | Publicar la 1.0.3 | — | — |
| 2 | Render en caché de la línea de tiempo | [render-en-cache.md](render-en-cache.md) | D-060 |
| 3 | Editar por el texto de la transcripción | [editar-por-texto.md](editar-por-texto.md) | D-061 |
| 4 | Intercambio con otros editores (EDL, FCPXML, OpenTimelineIO) | [intercambio.md](intercambio.md) | D-062 |
| 5 | Interpolación por flujo óptico | [flujo-optico.md](flujo-optico.md) | D-063 |
| 6 | Plantillas de movimiento | [plantillas-movimiento.md](plantillas-movimiento.md) | D-064 |
| 7 | HDR fase D parte 2 (intermedio del RAW) | [hdr-12-bits.md](hdr-12-bits.md) | D-065 |
| 8 | Linux primero, después Mac | [mac-y-linux.md](mac-y-linux.md) | D-066 |
| 9 | Trabajo en grupo (sala con iroh, capa común `Dathinel/sala` con Unicomp) | [colaboracion.md](colaboracion.md) | D-067 |

## Acuerdos entre diseños

- **Una sola subida del formato del proyecto a v16** para editar por texto, plantillas y trabajo en grupo; quien llegue primero la abre y los demás añaden sus campos, cada uno solo escrito cuando se usa.
- **La huella de la caché de render usa el `ContentHash` del medio**, no `SourceFingerprint.Key` (que incluye ruta y fecha), para que sirva entre equipos en el trabajo en grupo.
- **El flujo óptico, el HDR de 12 bits y la caché comparten intermedios**: la caché se diseña para guardar también los cuadros interpolados y el intermedio del RAW.
- **Unicomp** comparte con Rodaje la capa `sala` y la `IGpu` con su motor Vulkan; consume el protocolo de módulos, el formato `.paquete`, `KeyframeMath` y el tiempo en ticks por `externo/rodaje`. Coordinación en `D:\Programas de uso libre\coordinacion-rodaje-unicomp.md`.

Las anteriores (`interfaz-*.md`) son las propuestas de organización de la interfaz del 24-oct.
