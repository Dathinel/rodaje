# Tarea 64: pruebas de carga, lo que quedó sin vigilar

Rama `claude/pruebas-carga-huecos`, PR #61. Completa la tarea 47 (`2026-10-12-app-pruebas-carga.md`) y la revisión en bloque 6.

## Hecho

1. **`ClipVisualsTests.Dispose_does_not_wait_for_a_held_gate_and_the_waveform_thread_leaves_afterwards`** (prueba nueva):
   - La prueba retiene `_gate` desde otro hilo, como un hilo de trabajo de prioridad baja que se queda sin turno con el candado tomado.
   - Llama a `Dispose` desde un hilo de prioridad alta y exige que vuelva en menos de 500 ms.
   - Después suelta el candado y exige que el hilo de ondas salga. Ese hilo está en `Monitor.Wait(_gate)` sin tope, así que solo sale con el `PulseAll` diferido al pool.
   - Con el `lock` de antes, el hilo de `Dispose` se espera 5 s; luego se suelta el candado para que la prueba falle en vez de colgarse.
   - Antes de tomar el candado, la prueba espera a que el hilo de ondas esté en `WaitSleepJoin` y siga ahí 100 ms. Así se evita confundirlo con un hilo que aún intenta entrar al candado y que no necesitaría el aviso.
   - Ganchos internos en `ClipVisuals.cs`: `GateForTests` y `WaveformThreadForTests`. Rodaje.App.Tests compila los fuentes de `src\Rodaje.App\Services`, así que no hace falta `InternalsVisibleTo` ni tocar el `.csproj`.
2. **`AudioAi ProcessTests.Cancel_is_confirmed_quickly_and_the_module_keeps_working`**:
   - Ahora lleva `[Trait("Categoria", "TiempoReal")]`.
   - El `return` silencioso cambia por `ModelsAndVoiceFactAttribute`, definido en el mismo `ProcessTests.cs` y parecido a `ModelFactAttribute` de Translate. Si faltan los modelos o `corpus\audio-ia\voz-48k.wav`, la prueba sale como omitida y da la causa (`Corpus.Missing`).
   - Se ajustó el comentario de `Bad_buffers_...`, que remitía a esta prueba para la latencia.
   - **La latencia del ping del módulo (menos de 1 s) ya no se mide en la CI.** Se mide solo en la pasada TiempoReal de `herramientas\probar.ps1`, con modelos en `.entorno`.

## Cómo se comprobó que la prueba nueva falla con el `lock`

La App no compila en Linux (WinUI). Monté un arnés en el directorio temporal, fuera del repo, con estas piezas:

- un proyecto de pruebas `net10.0` que referencia Rodaje.Core;
- todos los `.cs` de `src/Rodaje.App/Services/Media` menos `VisualsSources.cs`, de él solo sus interfaces;
- `ClipVisualsTests.cs`, `TempDirectory.cs` y `UiThread.cs` tal cual;
- apaños para `BgraImage`, `MediaException` y `AppDataPaths`;
- los mismos analizadores (`latest-recommended`, avisos como errores).

Resultados:

- Con el código actual pasan las 15 pruebas de ClipVisuals, 5 vueltas seguidas. Las dos de `Dispose` también pasan con la CPU llena (8 bucles ocupados en 4 núcleos).
- Con el `Dispose` anterior al commit 414d0e5 (`git show 414d0e5 -- … | git apply -R`), la prueba nueva falla con «Dispose esperó el candado retenido». La de antes sigue pasando: ese era el hueco.
- Sin el `ThreadPool.UnsafeQueueUserWorkItem(... PulseAll ...)`, la prueba nueva falla con «El hilo de ondas no salió tras soltar el candado», al cumplirse los 60 s.
- Después de cada prueba se dejó el código como estaba; `git diff` lo confirma.

## Pruebas omitidas

- **Antes:** `Cancel_is_confirmed_quickly_...` nunca salía como omitida. Sin modelos contaba como pasada sin comprobar nada.
- **Ahora:** sale como omitida cuando faltan los modelos o la voz (los de `herramientas\preparar-modelos.ps1 -Corpus`). En la CI no corre, porque la CI excluye `TiempoReal`.
- No se omite ninguna otra prueba.

En Linux, `ProcessTests` (con `-p:EnableWindowsTargeting=true`) da 1 pasada, 1 omitida (esta, sin modelos) y 1 fallida: `Bad_buffers_fail_the_call_without_closing_the_module`. Falla porque Linux no ejecuta el `.exe` de Windows («Exec format error»). Ya pasaba antes y no es de esta tarea.

## Decisiones

- Gancho interno y no reflexión: si se renombra un campo, la prueba deja de compilar en vez de fallar en tiempo de ejecución.
- El atributo de omisión está en `ProcessTests.cs` porque los caminos de la tarea no incluyen otro archivo de AudioAi.

## Posibles choques

- La tarea 57 (`claude/main-verde-2`) toca `tests/Rodaje.App.Tests/Support/UiThread.cs` y pruebas intermitentes de main. Aquí no se tocan.
- Puede haber choque si la tarea 57 también cambia `ClipVisualsTests.cs`: aquí solo se añade al final del archivo.

## Para Daniel en Windows

- `ABRIR.bat`: la prueba nueva de ClipVisuals debe pasar en menos de 1 s.
- Con modelos y `RODAJE_PRUEBAS_LARGAS=1`, la pasada TiempoReal de `probar.ps1` debe incluir `Cancel_is_confirmed_quickly_and_the_module_keeps_working` y pasar.
