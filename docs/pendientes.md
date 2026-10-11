# Pendientes pedidos por Daniel

Ideas aprobadas que todavía no tienen fase. Cuando una se implementa, se quita de aquí y se anota en `decisiones.md`.

## Personalización

- **Pistas por defecto en un proyecto nuevo** (pedido el 10-oct): en Configuración se elige cuántas pistas de video y de audio aparecen al crear un proyecto.
- **Audio enlazado por pista de video:** elegir de qué pistas de video sale el audio a su propia pista al importar o arrastrar: de todas, o solo de algunas (por ejemplo de Video 1 y 2, pero no de Video 3).

## Complementos profesionales (pedido el 11-oct)

Daniel prefiere que lo profesional vaya como complemento aparte en su GitHub. Recomendación por pieza:

| Pieza | Dónde | Por qué |
|---|---|---|
| **10 bits** (decodificar, componer, exportar) | **En la app** | Ya es el camino interno: P010 por D3D11VA y composición en RGBA16F. Quitarlo costaría más que dejarlo; un complemento no podría cambiar el compositor. |
| **Looks de cámara** (S-Log3, V-Log, C-Log, BMDFilm, Log-C… a Rec.709) | Complemento `.paquete` (`rodaje-camaras`) | Son LUT `.cube`; se generan con fórmulas propias a partir de las curvas publicadas por los fabricantes, sin copiar sus LUT. |
| **Preajustes profesionales de exportación** (maestros ProRes/DNxHR, broadcast, cine) | Complemento `.paquete` (`rodaje-preajustes-pro`) | Son datos; la app ya los lee. |
| **RAW** (BRAW, R3D, CinemaDNG, ProRes RAW) | Módulo aparte (`modulo-raw`), proceso propio | Cada formato usa el SDK de su fabricante con su licencia; como proceso separado (igual que los módulos de IA) entrega cuadros al editor sin mezclar licencias. Necesita el protocolo de módulos de la fase 7 con video. |
| **Codificadores GPL** (x264, x265) | Módulo aparte (`rodaje-codificadores-gpl`) | Otro `ffmpeg.exe` por la misma tubería de la fase 6; frontera de proceso. |
| **Video 360°** (equirectangular: reencuadre a plano, metadatos esféricos, exportar 360) | **Decisión pendiente** | Necesita un shader de proyección en el compositor y los paquetes no traen shaders. Opciones: el motor lo trae y el complemento solo lo activa con sus preajustes, o se permite que complementos firmados por Dathinel traigan shaders. |
