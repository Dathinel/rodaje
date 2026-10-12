# Exportación: la orquestación (`Rodaje.Export`)

Junta las piezas de la fase 6 ([D-028](../decisiones.md), [fase-6.md](../fase-6.md)): el motor fuera de línea ([motor.md](motor.md)), el conversor de salida ([render.md](render.md)) y el sink de `ffmpeg.exe` ([medios.md](medios.md)). Sin interfaz; la app lo usa por `IExportRunner` ([app-vistas.md](app-vistas.md)).

## Archivos

| Pieza | Qué hace |
|---|---|
| [`ExportSettings`](../../src/Rodaje.Export/ExportSettings.cs), [`ExportPreset`](../../src/Rodaje.Export/ExportPreset.cs), [`ExportSettingsValidator`](../../src/Rodaje.Export/ExportSettingsValidator.cs), [`FfmpegArguments`](../../src/Rodaje.Export/FfmpegArguments.cs) | El contrato del paso 0: ajustes, preajustes de fábrica (con Instagram 1:1), validación honesta y los argumentos de cada familia de codificadores |
| [`ExportPlan`](../../src/Rodaje.Export/Running/ExportPlan.cs) | Lo que se decide sin GPU ni procesos: codificador de cada pista (el pedido, o el primero de los de por defecto que pasó la sonda), formato de píxel y color, tamaño, cuadros del rango alineados a la rejilla de la tasa de salida (video y audio duran lo mismo), argumentos (el tamaño objetivo usa la duración del rango), capítulos desde los marcadores y lo que el verificador debe encontrar |
| [`ExportRunner`](../../src/Rodaje.Export/Running/ExportRunner.cs) / [`ExportJob`](../../src/Rodaje.Export/Running/ExportJob.cs) | Un trabajo por exportación en su hilo; `CleanupStale` al arrancar la app |
| [`ExportPerformanceProfile`](../../src/Rodaje.Export/Running/ExportPerformanceProfile.cs) | Los dos modos: prioridades (en bajo también la de la GPU, con [`ExportGpuPriority`](../../src/Rodaje.Render/Export/ExportGpuPriority.cs)), lectura por adelantado, decodificadores y qué cede la vista previa (`IExportPreview`) |
| [`ExportErrors`](../../src/Rodaje.Export/Running/ExportErrors.cs) | Cualquier excepción a `ExportErrorCode` (`RenderException` y GPU perdida → `GpuFailed`, disco lleno, sin permiso…) con el cuadro |
| [`ExportEncoderSource`](../../src/Rodaje.Export/Encoders/ExportEncoderSource.cs), [`ExportEncoderCandidates`](../../src/Rodaje.Export/Encoders/ExportEncoderCandidates.cs) | `IExportEncoderSource` y la sonda provisional: codifica 0,2 s con cada candidato (primer píxel y perfil, argumentos fijos). El catálogo de la tarea 13 de la nube entra por la misma interfaz |

## Un trabajo

1. **Preparar.** Sonda (en el hilo del trabajo si no se dio), `ExportPlan`, `GpuDevice` propio, prioridad del hilo, temporales en `%TEMP%\rodaje-exportar-<id>.rodaje-tmp` anotados en `ExportJournal`, y la vista previa cede.
2. **Audio.** `OfflineAudio`: con LUFS, `MeasureLoudness` y `GainTo`; luego `WriteWave` del rango exacto al WAV temporal, float a la frecuencia y canales de salida.
3. **Video.** Un `TitleRasterizer` sobre el dispositivo de la exportación con las fuentes de los paquetes (`ExportRunnerOptions.FontFiles`), en `OfflineTimeline` y en los compositores. `OfflineTimeline.Prepare` (medios y LUT; `ContentMissing` salvo «exportar sin él»), luego `FindMissingFonts` sobre los títulos del rango (`ContentMissing` con `Culprit` «fuente «X»», o la fuente de respaldo si se aceptó), y por cada cuadro `Render(n)` (o `RenderAt` y mezcla de dos compositores si cambia la tasa) → `Compositor.RenderStrict` → `ExportFrameConverter.Submit` → `TryRead` → `FfmpegPipeSink.WriteVideo`, con el anillo lleno como contrapresión. Dos pasadas componen dos veces.
4. **Final.** `Complete` renombra el temporal; `ExportVerifier` comprueba cuadros, tasa, color, duración del audio, `moov` y capítulos (si falla, el trabajo falla y el archivo queda). Sin `ffprobe.exe` exporta sin verificar.

Progreso cada 100 ms como mucho: estado, cuadros, fps de los últimos 2 s, restante (con las pasadas que faltan), pasada y la miniatura de 320 px del conversor. Cancelar mata `ffmpeg.exe` desde el token y borra todo.

## Cifras (RTX 3050 Laptop, 11-oct, con otros agentes en la máquina)

- **1 min 1080p30**, dos pistas (H.264 1080p y HEVC en PiP con transformación y color) a H.264 NVENC 12 Mbps: **9,0 s en alto rendimiento y 8,8 s en bajo** (~200 fps los dos).
- **7 s con dos pistas, color, fundido, 2× y reversa**, más AAC a −14 LUFS y capítulos: 8–10 s (la reversa hace un seek por cuadro hasta que llegue el lector hacia atrás); PSNR contra la composición de la vista previa ≥38,5 dB en los cuadros comprobados a CQ 16.
- Formatos comprobados con ffprobe: H.264 NVENC + AAC (MP4, `moov` al principio, `bt709/tv/left`, 210 de 210 cuadros, audio 7,02 s), HEVC NVENC 10 bits Main 10, ProRes 422 HQ + PCM 24 en MOV, AV1 SVT + Opus en MKV, 9:16 con barras, VP9 en dos pasadas, conversión a 25 (mezcla) y 23,976 (más cercano), solo audio M4A.
- **Títulos** (`ExportTitleTests`): título con borde y sombra sobre las dos pistas a H.264 NVENC CQ 16, contra la vista previa asentada (sesión, rasterizador y compositor como el reproductor): PSNR de luma 40,6–51,3 dB (el croma 4:2:0 de un borde azul junto a un relleno amarillo baja el PSNR de RGB a ~33 dB). La fuente que falta para con `ContentMissing` en 0,2 s.
- **Cancelar** a mitad: 128 ms, sin archivo, temporal ni `ffmpeg.exe`.
- `Rodaje.Export`: 94,5 % de líneas cubiertas.

## Pruebas

[`tests/Rodaje.Export.Tests/Running`](../../tests/Rodaje.Export.Tests/Running): el plan, errores y perfiles sin hardware; con `ffmpeg.exe` y la GPU, los formatos de extremo a extremo, conversión de tasa, dos pasadas, LUT que falta, cancelación y los tiempos (`TiempoReal`). [`Acceptance`](../../tests/Rodaje.Export.Tests/Acceptance) (con `RODAJE_PRUEBAS_LARGAS=1`) mide los criterios de terminado sobre 10 min de 3 capas; las cifras están en [fase-6.md](../fase-6.md#cifras-de-la-aceptación-agente-f-11-oct).
