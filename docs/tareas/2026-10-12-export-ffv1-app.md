# Tarea 58: FFV1 y catálogo: que los argumentos fijos lleguen a la app

Rama `claude/export-ffv1-app`, PR #52.

## Comprobado en `origin/main` antes de cambiar

- `AppExportRunner.cs:43` arma los codificadores con `new ProbedEncoderSource(ffmpeg, ExportEncoderCandidates.All)`. `AcceptanceSupport.cs:33` hace lo mismo.
- El FFV1 de `ExportEncoderCandidates` (líneas 93-98) no tenía `BaseArguments`, así que el `-level 3 -slicecrc 1 -g 1` de `ExportEncoderCatalog.cs:287` no llegaba a la app.
- `FfmpegPipeSink.cs:728` pone `VideoEncoder.BaseArguments` justo tras `-c:v:0`, y la sonda (`ExportEncoderProbe.cs:148`) también los usa.
- Comparé uno por uno los candidatos que también están en el catálogo:

  | Candidato | Argumentos fijos en el catálogo | Antes en el candidato |
  |---|---|---|
  | `ffv1` | `-level 3 -slicecrc 1 -g 1` | ninguno |
  | `libvpx-vp9` | `-deadline good -cpu-used 2 -row-mt 1` | ninguno |
  | `h264_mf` | `-hw_encoding 0` | ninguno |
  | `h264_nvenc`, `hevc_nvenc`, `libsvtav1`, `prores_ks`, `dnxhd`, `gif`, `png` y todo el audio | ninguno | ninguno |

## Lo hecho (un commit por punto)

1. **Candidatos con los argumentos fijos** de los tres codificadores de la tabla, con el mismo valor que el catálogo.
2. **`InternalsVisibleTo` de `Rodaje.Media` para `Rodaje.Export.Tests`**, en el `.csproj` junto a la de `Rodaje.Media.Tests`. En `tests/Rodaje.Export.Tests/Cpu/CpuExportSupport.cs`:
   - Se quita la copia de `ParseEncoderList` y se usa `FfmpegExportEncoderProbe.ParseEncoderList`.
   - `SinkRuns.ProcessId` lee `FfmpegPipeSink.ProcessId` directamente, sin reflexión.
   - `RunBinary` vuelve a meter el proceso en `ChildProcessJob` (`TryAssign`), así que si el proceso de pruebas muere, Windows cierra también el `ffmpeg.exe`.
3. **Nuevo, `Cpu/CandidateSinkArgumentsTests.cs`:** son 2 pruebas que corren en Linux, sin `ffmpeg.exe`.
   - La primera arma ajustes de FFV1 en MKV con FLAC usando los specs de `ExportEncoderCandidates.All`. Los pasa por `ExportSettingsValidator`, `FfmpegArguments.Build` y luego `FfmpegPipeArguments.Build` (la línea completa del sink). Exige un único `-g` con valor `1`, `-level 3` y `-slicecrc 1`.
   - La segunda exige que cada candidato que también está en el catálogo tenga sus mismos `BaseArguments`. Si alguien vuelve a añadir un argumento fijo solo en un sitio, falla.

## Decisiones

- Solo se copian los **argumentos fijos**, como pide la tarea. Hay otras diferencias entre candidatos y catálogo que no toqué, porque cambiarían lo que el usuario ve o elige:
  - `h264_mf` usa la escala `-q:v` (1-100) en el candidato y `-quality` (0-100) en el catálogo.
  - El preset por defecto de NVENC es `p4` en el candidato y `p5` en el catálogo.
  - Cambian el orden de los perfiles de ProRes y DNxHR, los controles de tasa y las frecuencias de audio.
  - Lo de `-q:v` frente a `-quality` en `h264_mf` merece una tarea propia: conviene comprobar cuál de las dos respeta de verdad FFmpeg 9 con el MFT.
- La prueba de igualdad compara solo los candidatos que están en el catálogo (hoy son todos).

## Pruebas

- **Linux:** `Rodaje.Export.Tests` compila con `-p:EnableWindowsTargeting=true` sin avisos.
  - **Antes:** 103 pasan, 28 omitidas y 24 fallan.
  - **Después:** 105 pasan, 28 omitidas y 24 fallan. Las 24 son las mismas de `main`, que fallan al descubrirse por `d3d11` porque no hay GPU en Linux.
  - Las 2 nuevas pasan.
- **Pruebas omitidas:** las mismas 28 antes y después, por la misma causa: no hay `ffmpeg.exe` LGPL de Windows en Linux, o falta un codificador compilado. Las nuevas no se omiten nunca.

## Posibles choques

- **Tarea 44 (PR #48)** toca `FfmpegPipeSink.cs`, que esta rama no toca.
  - Si cambia la firma de `FfmpegPipeArguments.Build(request, temporaryOutput, metadataFile)`, `CandidateSinkArgumentsTests` no compilará.
  - Si cambia `ProcessId`, ahora lo dirá el compilador en vez de la reflexión.
- **Tarea 49 (PR #46)** toca `tests/Rodaje.Export.Tests/Still/`, que esta rama no toca. Con `InternalsVisibleTo`, los tipos internos de `Rodaje.Media` pasan a verse en `Rodaje.Export.Tests`, y una clase de prueba con el mismo nombre que uno de ellos podría dar ambigüedad. En `main` compila sin ninguna.

## Qué comprobar en Windows

- **FFV1 desde la app**, exportado en MKV: `ffprobe -show_frames -select_streams v -show_entries frame=key_frame salida.mkv` debe dar `key_frame=1` en todos los cuadros. Antes salía un cuadro clave cada 12.
- **VP9 desde la app:** ahora lleva `-deadline good -cpu-used 2 -row-mt 1`. Hay que ver que exporta bien y comparar el tiempo con una exportación anterior; con `-row-mt 1` debería ir igual o más rápido.
- **`h264_mf`:** con `-hw_encoding 0` explícito no debería cambiar nada, porque es el valor por defecto de FFmpeg.
- **`ABRIR.bat aceptacion`:** la de FFV1 debe seguir dando todos los cuadros exactos.
