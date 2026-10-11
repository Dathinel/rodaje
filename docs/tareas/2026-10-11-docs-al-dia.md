# Documentación al día con el código (tarea 6)

Rama `claude/docs-al-dia`. Solo documentos; no se compiló ni se corrieron pruebas. Pruebas omitidas: 0 antes, 0 después (no se tocó código).

## Correcciones y su respaldo en el código

**nucleo.md**
- `SchemaVersion` 2 → 4 y enlace al esquema v4: `src/Rodaje.Core/Model/Project.cs`.
- `Clip.Speed`, `ClipSpeed`, `ClipTimeMap`, `RetimeMath`: `Model/Clip.cs`, `Model/ClipSpeed.cs`, `Timing/ClipTimeMap.cs`, `Timing/RetimeMath.cs`.
- `Track.Effects` / `Track.Transitions`, `TransitionInstance`: `Model/Track.cs`, `Model/TransitionInstance.cs`.
- `BuiltInEffects`, `EffectRegistry` (transiciones, rangos, 13 mezclas): `Model/BuiltInEffects.cs`, `Effects/EffectRegistry.cs`.
- Validador con rangos y velocidad: `Model/ModelValidator.cs`.
- Ediciones de efectos, transiciones y velocidad (~50 operaciones): `Editing/*`, `Commands/Edits*.cs`.

**reproduccion.md**: `IDecodeExecutor`, `EffectResources`, campos nuevos de `LayerFrame`, `ClipTiming` a 1× con `ClipTimeMap`, `ResetStats` (`src/Rodaje.Playback/...`).

**README.md**: fases 1 a 5; ~50 operaciones.

**medios.md**: `MediaSupport` (`src/Rodaje.Media/Probe/MediaSupport.cs`).

**motor.md**: `LayerFrame.IsPlain` (`CompositionTypes.cs:79`); audio con velocidad `RetimedReader` (`AudioPlan.cs`, `TimelinePlan.Audio.cs`); `MeasureAudio` (`TimelineSession.cs:147`).

**render.md**: sobrecarga `Presenter.Draw(... PixelRect? placement)` (`Presenter.cs:35`); `DecoderCompositionTests`.

**app.md / app-vistas.md**: `DialogGate`, `MeasurementScenarios`, velocidad en el inspector (`SpeedEditor`, `SpeedCommands`, "Mantener tono" solo si suena y no es 1×), `MissingContent.Attach`, `ContentRefresh`, escenario `-Escenario velocidad` (`src/Rodaje.App/...`, `herramientas/app.ps1`).

**formato-paquete.md**: `MixParameters`/`MixOptions` ya viajan en `TransitionCurves`; `EffectContent` usa `ConcurrentDictionary` en vez de `ConditionalWeakTable` (`src/Rodaje.Engine/Timeline/IEffectContent.cs`, `src/Rodaje.App/Services/Effects/EffectContent.cs`).

**variables-entorno.md**: `RODAJE_REGENERAR_ESQUEMA` reescribe la v4 (`tests/Rodaje.Core.Tests/Serialization/ProjectSchemaTests.cs`).

## Pendiente / no verificado
Las cifras de rendimiento (ms) de render.md y app-vistas.md no se contrastaron.

## Choques con `origin/main`
D-026 (magnetismo siempre al cambiar la velocidad) estaba en `main` y la primera versión lo contradecía; corregido en la revisión (nucleo.md, app-vistas.md). `docs/arquitectura/` puede cambiar si 5c.3 (reversa) se une.

## Para Daniel en Windows
Nada; es documentación.

## Segunda ronda (revisión del 11-oct)
- Velocidad con magnetismo (D-026): `src/Rodaje.Core/Editing/TimelineEdits.Speed.cs`; `SpeedScope` en `EditorSession.Speed`.
- Transiciones completas y `Preset`: `EffectRegistry.cs:84-107`. Validador rechaza fuera de rango y ids con control: `ModelValidator.cs:76,256`.
- «Uso desde Rodaje» reescrito: `EffectContent.cs`, `AppEffectContent.cs:13`, `IContentLoader.cs:55,61`.
