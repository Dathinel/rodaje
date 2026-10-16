# Rendimiento por fabricante

Rodaje mira qué GPU tiene el equipo al arrancar (fabricante, modelo, generación, VRAM y qué formatos decodifica de verdad su controlador) y decide con eso: qué se decodifica por hardware, quién codifica los proxies y con qué codificador se exporta por defecto. No hay nada que configurar.

## Qué esperar

| Equipo | Decodificación | Proxies | Exportar H.264/HEVC | AV1 |
|---|---|---|---|---|
| **NVIDIA** (GTX 10 en adelante) | H.264, HEVC 8 y 10 bits, VP9 y, desde RTX 30, AV1 por la GPU | NVENC | NVENC | NVENC desde RTX 40; antes, SVT-AV1 por CPU |
| **AMD** (RX 400 en adelante) | H.264, HEVC; VP9 y AV1 según la generación (AV1 desde RX 6000) | AMF | AMF | AMF desde RX 7000; antes, SVT-AV1 |
| **Intel** (UHD 600 en adelante, Iris Xe, Arc) | H.264, HEVC, VP9; AV1 desde Iris Xe; HEVC 4:2:2 se intenta por hardware | Quick Sync | Quick Sync | Quick Sync desde Arc y Core Ultra; antes, SVT-AV1 |
| **Sin GPU** (o la de software de Windows) | Todo por la CPU | Media Foundation (CPU) | Media Foundation (CPU) | SVT-AV1 |

En un portátil con dos GPU (Intel integrada y NVIDIA o AMD dedicada) manda la dedicada; la integrada queda de segunda opción.

AMD e Intel se eligen por tabla y por la prueba de codificadores que hace el diálogo de exportar, pero no se han probado en un equipo real: si un codificador falla en la prueba, simplemente no aparece y se usa el siguiente.

## Formatos de cámara pesados

ProRes, DNxHR, H.264 de 10 bits o 4:2:2 y HEVC 4:2:2 (Sony, Canon, Panasonic, DJI) no se decodifican por hardware en NVIDIA ni AMD. Rodaje los manda directamente a la CPU con todos los núcleos. En una RTX 3050 de portátil con 16 hilos, en 4K: ProRes 422 HQ a unos 26–33 cuadros por segundo, DNxHR HQ a 47–68, H.264 4:2:2 a 33–48 y HEVC 4:2:2 a 30–39 (antes de este cambio, 3–8). Es tiempo real justo para una capa: para editar suelto, los clips 4K generan proxies solos al importar (**Archivo › Preferencias...** › pestaña **Rendimiento** › «Generar proxies al importar videos pesados»).

## Exportar

- **Alto rendimiento** y **bajo** usan el preajuste de velocidad afinado de cada codificador si no eliges uno en Avanzado: NVENC p4, AMD AMF «speed» o «balanced», Intel Quick Sync «faster» o «medium», SVT-AV1 9 u 8.
- **Copia directa**: si exportas un tramo sin cambios de un clip que ya tiene el formato de la salida y los cortes caen en cuadros clave, el video se copia sin recodificar ([Exportar](exportar.md#copia-directa)). En la RTX 3050, 19 s de 1080p H.264 salen en 1,7 s en lugar de 5 s, con la calidad del original.
- **Dos a la vez**: la cola corre una exportación por la GPU (H.264 por NVENC, por ejemplo) junto a otra por la CPU (un maestro ProRes) si el equipo tiene 6 hilos y 3 GB de VRAM.

## Sin GPU

Funciona todo, más despacio: la vista previa decodifica en la CPU (en 4K hacen falta proxies), los proxies y H.264/HEVC los codifica Media Foundation de Windows, y AV1 lo hace SVT-AV1. En Windows N sin el Media Feature Pack no hay H.264: Rodaje lo dice y ofrece AV1 o VP9.
