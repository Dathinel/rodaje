# Tarea: trabajo de Windows de la CI en verde (11 de octubre de 2026)

Rama `claude/ci-windows-verde`, creada desde `origin/main` en `20b4a22`. PR en borrador: https://github.com/Dathinel/rodaje/pull/2.

**Unida el 11-oct** tras cuatro revisiones independientes. La última corrida del PR (38118021444, `1c3cb10`) dio Render 196 / 192 / 4 omitidas / 0 fallos; su único rojo eran dos pruebas de `main` con límite de 30 s (fuzz del proyecto), que pasaron a `TiempoReal` al unir. Render: 2 → 4 omitidas (`DecoderCompositionTests` sin D3D11VA y la guardia NV12 del PR #4), sin exclusiones por nombre.

Parte de `docs/tareas/2026-10-10-ci.md`, que anotó 13 fallos del runner `windows-latest` (sin GPU de verdad, sin NVENC y con 2 núcleos).

## Qué cambió

### 1. Media: HEVC de 10 bits sin `hevc_nvenc` → omitidas

`TestClips` solo genera `Hevc10` con `hevc_nvenc`: el build LGPL no trae HEVC de 10 bits por software.

- `testing/Rodaje.TestMedia/Skip.cs`:
  - `Skip.Encoder(nombre)` da el motivo si ese codificador concreto no funciona.
  - Nueva propiedad `Encoder` en `[FfmpegLibraryFact]` y `[HardwareFact]`, que se combina con sus otros motivos.
- `tests/Rodaje.Media.Tests/Support/TestClips.cs`: constante `TenBitEncoder = "hevc_nvenc"`, la misma que usa el generador.
- `tests/Rodaje.Media.Tests/Support/TenBitClipDataAttribute.cs`, nuevo:
  - aporta las filas de `Hevc10` de las teorías, con `Skip` si no hay ese codificador;
  - `VideoDecoderAccuracyTests` lo usa junto al `MemberData` de los demás clips.
  - Los nombres de los casos no cambian.
- Pruebas con `Encoder = TestClips.TenBitEncoder`:
  - `MediaProbeTests.Hevc_main10_reports_ten_bits`
  - `ThumbnailDecoderTests.Ten_bit_hevc_gives_a_thumbnail`
  - `FrameLifetimeTests.Ten_bit_hardware_frames_are_p010`
  - `FrameLifetimeTests.Uploaded_frames_release_their_own_textures`

  Las dos de `FrameLifetimeTests` no fallaban en el runner porque allí se omiten por hardware. Las añadí porque en un equipo con D3D11VA y sin NVIDIA (AMD o Intel) fallarían igual.

### 2. Media: `Corrupted_packets_are_skipped_without_throwing` → solo con `h264_nvenc`

**La causa no es la que se supuso** (H.264 sin cuadros B que desplace la zona dañada). Reproducido en Linux con el mismo FFmpeg 9.0.2 de BtbN (build compartido, cargado con enlaces `.dll` → `.so`):

- Sin NVENC, `TestClips` genera el H.264 con **libopenh264**. Tiene solo I y P, con IDR reales cada 48 cuadros.
- La pasada secuencial sobre el archivo dañado es correcta.
- La prueba falla en el **cuadro 95**, fuera de la zona dañada, cuando se pide de nuevo después del 150.
- Aislado con decodificadores nuevos:
  - Tras pedir el **150**, un cuadro dañado con basura en el centro, el primer GOP que se decodifica después del siguiente seek sale distinto de la referencia:
    - 150 → 3: mal; 150 → 60: mal;
    - 150 → 95 con los hilos por defecto: mal (es el fallo del runner);
    - 150 → 48: bien con los hilos por defecto y mal con un solo hilo.
  - El seek siguiente ya sale bien. Ejemplo: 150 → 3 (mal) → 95 (bien).
  - Un decodificador nuevo da esos mismos cuadros bien. Después de pedir 140, 144, 149, 121, 165 o 105 tampoco aparece el error.
  - En el archivo limpio nunca falla.
  - Es determinista con el mismo número de hilos y cambia según los hilos, pero falla también con un solo hilo.
  - La basura no contiene ningún NAL SPS ni PPS nuevo. `VideoDecoder` abre el códec con las opciones por defecto, y su `ResetDecoder` llama a `avcodec_flush_buffers`.
- **Conclusión:** un paquete dañado deja en el decodificador un estado que sobrevive al `avcodec_flush_buffers` del seek. Es un **defecto pendiente de `Rodaje.Media`, o de FFmpeg**, y la prueba lo detecta bien. Con el clip de NVENC no aparece porque la basura cae sobre otro contenido.
- **Cambio:** `[FfmpegLibraryFact(Encoder = "h264_nvenc")]`, con un comentario que explica por qué. Con NVENC la prueba comprueba exactamente lo mismo que antes.
- **Añadido:** el último bucle de la prueba ahora dice qué cuadro falla, con `recovered` y el número de seeks. Antes era un `Assert.Equal` sin mensaje.

### 3. TestMedia: presets con cuadros B → omitidos si el codificador no los da

En el runner gana `h264_mf`, que solo produce I y P aunque se le pida `-bf 2`.

- `Skip.BFrames(códec)`:
  - codifica una vez por códec 2 s de `testsrc2` con `-bf 2`, con las opciones de control de `TestMediaGenerator` para cada familia (NVENC, `_mf`, libx264, libx265 y el resto); difiere del generador en la entrada (`testsrc2`, no rawvideo yuv420p), el bitrate, el GOP y las opciones de color, y la rama «resto» añade `-bf 2`;
  - si hay un NVENC de más preferencia en el build y existe `nvcuda.dll` pero se eligió otro codificador, devuelve `null` (la sonda de NVENC pudo fallar por el límite de sesiones): la prueba corre y falla en vez de omitirse con un motivo engañoso;
  - cuenta los tipos de cuadro con ffprobe.
  - libopenh264 no se omite, como ya hacía la prueba.
- `[FfmpegFact(RequiresBFrames = true)]` en los cuatro presets con `BFrames = 2`:
  - `H264Ntsc30BFrames`, `Hevc25Mkv` y `H264Ntsc24Aac`;
  - `Drift720p10Min`, larga: el motivo de prueba larga va primero y no se comprueba nada sin `RODAJE_PRUEBAS_LARGAS`.
- El mensaje dice qué codificador es, qué tipos dio y que el preset los exige.

### 4. Audio: pruebas de concurrencia sin depender del pool de hilos

- **`LevelMathTests.LeerMientrasSeEscribeNuncaDaBloquesMezclados`:**
  - El lector iba en `Task.Run`. Con pocos núcleos, y el escritor ocupando el suyo, la tarea del pool no arrancaba en los 400 ms (reproducido en Linux con `taskset -c 0`: «solo 0 lecturas» 2 de 3 veces).
  - Ahora el lector es un `Thread` propio. El escritor sigue al menos 400 ms **y** hasta tener 100 lecturas, con un límite de 30 s.
  - Lo que comprueba (ningún bloque mezclado) no cambia.
- **`DeviceAudioClockTests.LecturasConcurrentesSonMonotonas`:**
  - Mismo defecto, al revés: el escritor iba en `Task.Run` y, si no arrancaba, `last` seguía en cero. En el runner pasó, pero aquí con 2 núcleos (`taskset -c 0,1`) fallaba 6 de 6 veces.
  - Mismo arreglo, en el mismo commit. Ahora el escritor en un hilo propio, y se lee al menos 200 000 veces y hasta que el reloj avance.
- Resultado en Linux: pasan 8 de 8 corridas de todo `Rodaje.Audio.Tests` con 1 y 2 núcleos.

### 5. `ci.yml`: Windows bloquea

- Sin `continue-on-error` y sin exclusiones por nombre: el filtro es `Categoria!=TiempoReal&Categoria!=Rendimiento`, igual que en Linux. El PR #4 (ya en main) dejó Render sin fallos en WARP: guardia NV12, `EffectPerformanceTests` en `Rendimiento` y tolerancia de Sharpen solo en WARP.
- Se conserva el `exit $LASTEXITCODE` del paso de pruebas.

## Pruebas omitidas, antes y después (runner de Windows)

| Proyecto | Antes | Después | Causa |
|---|---|---|---|
| `Rodaje.Media.Tests` | 25 | 32 | +6 casos de HEVC 10 bits (sin `hevc_nvenc`, el build LGPL no trae HEVC de 10 bits por software); +1 `Corrupted_packets_are_skipped_without_throwing` (solo con `h264_nvenc`, defecto de estado del decodificador, ver punto 2) |
| `Rodaje.TestMedia.Tests` | 3 | 5 | 2 presets con cuadros B: `h264_mf`, el codificador del runner, solo produce I y P aunque se le pida `-bf 2` |
| `Rodaje.Render.Tests` | 2 | 4 | `DecoderCompositionTests` (sin D3D11VA en el runner) más `Draws_the_requested_slice_of_a_texture_array` y `CompositorPerformanceTests` por la guardia NV12 del PR #4. Sin exclusiones por filtro |

`Skip.Encoder` omite solo si ffmpeg no trae el codificador o, siendo NVENC, falta `nvcuda.dll`; cualquier otro fallo de la sonda no omite.

Si la sonda de cuadros B falla (por ejemplo, por el límite de sesiones de NVENC), la prueba **no** se omite: corre y falla con su error real.

Sin pruebas de Render excluidas por filtro; la CI de main sin exclusiones da Render 196 casos, 192 pasan, 4 omitidas, 0 fallos.

## Pendiente

- **`Rodaje.Media`: estado del decodificador tras un paquete dañado** (punto 2). Arreglo posible: después de un error de decodificación, que el siguiente seek reabra el contexto del códec en vez de solo vaciarlo. Va con una prueba propia que no dependa del codificador:
  - generar el H.264 con libopenh264;
  - pedir 150 y después 3;
  - comparar con la referencia.

  Así lo reproduce el clip que deja `TestClips.Corrupted(TestClips.H264)` cuando no hay NVENC. Para verlo en Linux:
  1. Bajar `ffmpeg-n9.0.2-17-g2a571b6068-linux64-lgpl-shared-9.0` de BtbN.
  2. Enlazar `lib*.so.N` como `<lib>-N.dll` y `ffmpeg`/`ffprobe` como `.exe` en `<entorno>/ffmpeg/bin`.
  3. Correr con `RODAJE_ENTORNO=<entorno>`, `LD_LIBRARY_PATH=<lib>` y `-p:EnableWindowsTargeting=true`.
- **`FfmpegTool.RunExecutable` falla en Linux** con «Cannot access a closed pipe» en `ReadEncoders`, así que `Skip.Encoder` y `Skip.Encoders` no se pueden comprobar fuera de Windows. No afecta a la CI, que corre TestMedia en Windows.

## Qué comprobar en el PC de Daniel (RTX 3050)

1. `ABRIR.bat`: todas estas pruebas deben **correr**, no aparecer como omitidas:
   - las de HEVC de 10 bits: `VideoDecoderAccuracyTests` con `hevc10-25-g24-bf2`, `Hevc_main10_reports_ten_bits`, `Ten_bit_hevc_gives_a_thumbnail` y las dos de `FrameLifetimeTests`;
   - `Corrupted_packets_are_skipped_without_throwing`;
   - los tres presets de `PresetTests` (`H264Ntsc30BFrames`, `Hevc25Mkv`, `H264Ntsc24Aac`).

   Si alguna sale omitida, el motivo dice qué codificador no funcionó.
2. **Render:** `ci.yml` ya no excluye ninguna prueba de Render por nombre; en la RTX 3050 deben correr todas.
3. **El número total de pruebas omitidas no sube** respecto de la última corrida de Daniel, salvo por pruebas nuevas.
