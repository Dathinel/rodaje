# Prototipo Vulkan Video (Rodaje - D-066 Fase 0b)

Valida la decodificación por hardware con el hwaccel Vulkan de FFmpeg en un `VkDevice` propio de la aplicación, muestreo compute YUV->RGBA en GPU, verificación exacta bit a bit contra decodificación software CPU, y respaldo por subida de cuadros software (`--forzar-subida`).

## Cómo se ejecuta

### Windows (.NET local del repositorio)
```powershell
$env:RODAJE_ENTORNO = "<repositorio>\.entorno"
build\dotnet-local.cmd build experimentos\linux-fase0\vulkan-video -c Release
build\dotnet-local.cmd run --project experimentos\linux-fase0\vulkan-video -c Release -- --segundos 120 --salida experimentos\linux-fase0\vulkan-video\resultado.json
build\dotnet-local.cmd run --project experimentos\linux-fase0\vulkan-video -c Release -- --segundos 20 --forzar-subida
```

### CI / Linux
```bash
./experimentos/linux-fase0/vulkan-video/ci.sh
```

## Cifras medidas en RTX 3050 Laptop GPU (120 s, `vulkan-video`)

| Métrica | Valor medido |
|---|---|
| GPU / Driver Vulkan | NVIDIA GeForce RTX 3050 Laptop GPU / 610.62.0.0 |
| Cuadros decodificados | 11.026 NV12 + 10.999 P010 = 22.025 cuadros |
| Rendimiento medio | 183,41 fps (p50: 2,23 ms, p95: 5,70 ms) |
| Transferencias CPU normales | 0 (cero copias GPU->CPU en decodificación) |
| Memoria proceso (MB) | Inicio: 90,62 / Mitad: 392,00 / Final: 373,25 |
| Recursos vivos al final | VkImage = 0, AVFrame = 0 (0 fugas) |
| Verificación RGBA CPU | 740 cuadros, MAE/255 = 0,00565 (umbral <= 0,00784), PSNR = 33,41 dB |
| Comparación exacta NV12 | 370 comparados, 370 idénticos (0 bytes distintos, dif máx 0) |
| Comparación exacta P010 | 370 comparados, 370 idénticos (0 bytes distintos, dif máx 0) |
| Resultado final | ÉXITO (Código de salida: 0) |

## Enlace Vulkan elegido
Se seleccionó `TerraFX.Interop.Vulkan` (1.4.328.1).
Proporciona enlaces C puros (`delegate* unmanaged`) que interoperan directamente con las estructuras C de FFmpeg sin sobrecoste ni tipos intermedios.
Garantiza compatibilidad total con Native AOT (.NET 10) sin reflexión ni advertencias de recorte.

## Lo no comprobado
- Decodificación Vulkan Video en distribuciones Linux (probado en Windows con RTX 3050).
- Códecs AV1 y VP9 mediante Vulkan Video (solo se validaron H.264 y HEVC).
- GPUs de otros fabricantes (AMD, Intel) y controladores de código abierto (Mesa RADV/NVK).
