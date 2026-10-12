# Tarea 43: NU1004 intermitente al restaurar en la CI de Windows

Rama `claude/ci-restore-rid`, PR #34.

## Causa
No era una carrera de la restauración en paralelo. Había dos lockfiles cuyo contenido dependía de dónde y cómo se restauraron la última vez:

- **Echo, Voice y Subtitles** publican con AOT (`PublishAot`) sin `RuntimeIdentifier`, así que el SDK usa el RID del anfitrión: `linux-x64` en la nube y `win-x64` en Windows. Un lockfile regenerado en Linux rompe Windows. Así falló el job 114665862040 (PR #30).
- **Rodaje.Modules.Protocol** no declaraba RID. `herramientas/app.ps1` publica el eco con `-r win-x64`, que llega como propiedad global al proyecto referenciado y añade `"net10.0/win-x64": {}` a su lockfile. Una restauración normal lo quita. En `main` entró y salió varias veces (589ed5d, 30b0a73, 76bcdf6, 4795266). Como la CI de un PR corre sobre `refs/pull/N/merge`, que cambia cuando avanza `main`, el mismo PR pasaba unas veces y otras no (job 114670188931, PR #31).

## Arreglo
- `RuntimeIdentifier=win-x64` en los tres módulos, igual que en AudioAi. Sus lockfiles no cambian.
- `RuntimeIdentifiers=win-x64` en el protocolo: el RID es el mismo con y sin `-r win-x64`. Su lockfile gana `"net10.0/win-x64": {}`, sin paquetes nuevos.
- Paso de CI en Linux «Lockfiles deterministas»: restaura la solución entera en modo bloqueado con `EnableWindowsTargeting` y el eco con `-r win-x64`, como lo hace app.ps1. `--locked-mode` se mantiene.

Por qué no vuelve: el conjunto de RID de cada proyecto ya no depende del sistema operativo ni de que se pase `-r win-x64`, así que cualquier restauración escribe el mismo lockfile. Si un lockfile vuelve a depender del anfitrión, falla la CI de Linux.

## Verificado
En Linux, borrando `obj` entre corridas, tres veces seguidas: la solución en modo bloqueado y el eco con `-r win-x64` pasan sin errores, y también los restores del trabajo de Linux. Sobre `main`, los dos comandos reproducen el NU1004.

## Pendiente y posibles choques
- Los PR abiertos que traen un lockfile de módulo regenerado en Linux (por ejemplo el #30 con `linux-x64` en el eco) deben volver a tomar el lockfile de `main` después de unir este PR.
- Fuera de los caminos de la tarea: el `-r win-x64` de `herramientas/app.ps1` (línea 159) ya sobra.
- **Daniel en Windows:** `Rodaje.bat` y `app.ps1 -Autoprueba` deben seguir publicando el eco y los módulos. Después de un `dotnet restore`, `git status` no debe mostrar ningún `packages.lock.json` cambiado.

## Resultado de la CI (run 38207280099)
- **Linux:** en verde, también el paso nuevo «Lockfiles deterministas».
- **Windows:** «Restaurar» y «Compilar» pasan. «Pruebas» falla por pruebas ajenas a esta tarea que también fallan en `main` (4795266, en rojo en Windows), que es lo que ataca la tarea 40 (`claude/ci-main-verde`):
  - `TitleEditorTests.BordesYConvertirEnKeyframes`: cultura, «0.5» frente a «0,5».
  - `TitleRasterizerTests.Composing_a_ready_title_costs_little_gpu_time`: 2,3 ms con Basic Render Driver.
  - `CpuExportCancellationTests` (dnxhd/mxf y prores/mov): la cancelación tarda.
  - `ExportEncoderAvailabilityTests.Probes_run_in_parallel_up_to_the_limit`.
- Que la restauración pase una vez no demuestra nada por sí solo. El arreglo vale porque elimina la dependencia del anfitrión y del `-r`, y la CI de Linux lo comprueba.
