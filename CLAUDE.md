# FotoToma: estado del proyecto

Editor de video nativo para Windows, inspirado en la experiencia de uso de CapCut, con todo el código propio. Repositorio privado `Dathinel/fototoma`, licencia MIT. Las reglas generales están en `D:\Programas de uso libre\CLAUDE.md`; este archivo cuenta dónde va este programa.

## Cómo se trabaja aquí

- **Entorno propio.** El SDK de .NET 10.0.401 vive en `.entorno\dotnet` (ignorado por git) y lo instala `herramientas\preparar-entorno.ps1`, verificando su SHA-512. Para cualquier comando de .NET se usa `herramientas\dotnet-local.cmd <args>`, nunca un `dotnet` global. La variable `FOTOTOMA_ENTORNO` permite apuntar a otro `.entorno`, por ejemplo desde un worktree de agente.
- **Probar.** `ABRIR.bat` corre todas las pruebas. Acepta tres argumentos:
  - `ABRIR.bat cobertura`: pruebas más cobertura.
  - `ABRIR.bat aot`: publica la prueba de humo con Native AOT y la ejecuta.
  - `ABRIR.bat preparar-entorno`: rehace `.entorno`.
- **GitHub.** Git se autentica a través de `gh` (`gh auth setup-git`), así que `git push` funciona directo.
- **Worktrees de agentes.** Los agentes en paralelo trabajan en `.claude\worktrees\` (ignorado). Su trabajo se une a `main` con `git merge`.
- **Choques de espacios de nombres.** Las pruebas viven en espacios como `FotoToma.Core.Tests.Timing`, que tapan a `FotoToma.Core.Timing` cuando se escribe `Timing.Time`. Por eso, en las pruebas, los tipos del núcleo se escriben con su nombre completo o con `using`.

## Estado al 9 de octubre de 2026

**Fase 1 terminada: modelo de línea de tiempo sin interfaz.** Contiene:
- **Tiempo exacto** en ticks de 1/705 600 000 s (`Timing/`).
- **Modelo:** proyecto → medios y secuencias → pistas → clips, con efectos y keyframes en tiempo del origen, más `ModelValidator` (`Model/`).
- **Operaciones de edición** (`Editing/TimelineEdits.*`):
  - Cortes y recortes: cortar, recortar, recorte ripple, roll, slip y slide.
  - Colocación: mover (estricto, sobrescribir, insertar), insertar, sobrescribir, añadir al final, levantar, borrado ripple y cerrar hueco.
  - Pistas: gestión de pistas, y enlazar o desenlazar clips.
- **Comandos con instantáneas de secuencia** (`Commands/`):
  - `UndoStack` con grupos, fusión por clave, `BreakMerge`, punto limpio y límite.
  - `Edits`, las fábricas que usará la interfaz.
- **Formato `.ftoma`** (`Serialization/`): JSON versionado con migraciones encadenadas, guardado atómico con `.bak` y esquema en `schema/`.
- **Pruebas:** 685, entre unitarias, de propiedades con CsCheck (10 000 escenarios aleatorios) y la prueba de humo con Native AOT (ejecutable de 3,37 MB, 0 advertencias). Cobertura del núcleo: 99,6 % de líneas y de ramas.
- **Revisión independiente:** encontró 5 defectos, todos corregidos con su prueba de regresión. El guardado reintenta ante bloqueos breves del antivirus.
- **Estilo:** README corto con evidencias y comentarios mínimos en el código; el detalle vive en `docs/guia-tecnica.md`.
- **Documentación:** `docs/investigacion.md`, `docs/decisiones.md`, `docs/legal.md` y `docs/formato-proyecto.md`, más `LICENCIAS.md`, `THIRD-PARTY-NOTICES.txt`, `SECURITY.md` y `CONTRIBUTING.md`.

## Decisiones tomadas

- **Arquitectura modular** (`docs/propuesta-modulos.md`, aprobada el 9-oct-2026):
  - núcleo y motor en el exe;
  - contenido en paquetes `.ftpack` sin código;
  - IA como módulos C# en procesos aparte;
  - formato de paquete y protocolo compartidos con otros programas;
  - distribución en zip base y zip completo, más el gestor.

## Decisiones pendientes para Daniel


- **Ripple con clips enlazados de distinto largo.** Pasa, por ejemplo, después de sobrescribir solo el audio. En `RippleTrim` con alcance de pista, hoy todas las pistas se corren desde el final original del clip principal. La alternativa es que cada pista se corra desde el final de su propio clip. Conviene decidirlo al probar la interfaz (fase 4).
- **Enlaces huérfanos.** Sobrescribir o cortar parcialmente puede dejar un `LinkId` que ya no comparte ningún otro clip. No rompe nada; falta decidir si se limpia automáticamente.
- **Código compartido entre programas.** Cuando exista el segundo programa habrá que decidir cómo compartir el formato de proyecto y el lanzador: paquete NuGet desde su propio repo, submódulo u otra opción.

## Siguiente paso: fase 2, reproducción de un clip

- **Montaje del entorno.** Las DLL de FFmpeg LGPL-shared de BtbN van en `.entorno\ffmpeg`, con URL y hash fijados en `preparar-entorno.ps1`. Se usan con FFmpeg.AutoGen (MIT).
- **Decodificación.** D3D11VA en un hilo propio, con una caché de frames.
- **Audio.** WASAPI propio con `[GeneratedComInterface]`; el audio hace de reloj maestro (posición + QPC).
- **Interop elegido** (`docs/sondeo-interop-aot.md`): `FFmpeg.AutoGen.Bindings.DynamicallyLinked` 9.0.1.1 y `TerraFX.Interop.Windows`, con 0 advertencias de AOT.
- **Criterio de terminado.** El seek debe ser exacto al cuadro en H.264 y HEVC de prueba, y la deriva entre audio y video debe quedar por debajo de 1 cuadro en 10 minutos.
- **Verificación de la fase.** Al cerrarla, la revisa un agente distinto.
- **Fase 3.** En la reproducción con varias capas hay que detenerse y decirle a Daniel exactamente qué probar.
