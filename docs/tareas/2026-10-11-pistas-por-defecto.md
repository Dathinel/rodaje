# Pistas por defecto en un proyecto nuevo (tarea 7)

Rama `claude/pistas-por-defecto`.

## Hecho

- `AppSettings.DefaultVideoTracks` y `DefaultAudioTracks` (1–8, por defecto 2 y 2, los valores de hoy) en `ajustes.json`; `Normalize` los acota si alguien edita el archivo a mano. `AppSettings.NewProjectTracks` los entrega juntos.
- `Services/NewProjectTracks.cs` (nuevo): crea el proyecto con `ProjectFactory.CreateDefault` y añade o quita pistas por el final con las ediciones existentes `Edits.AddTrack` / `Edits.RemoveTrack`, de modo que «Video 1» sigue siendo la pista principal. No se tocó `Rodaje.Core`.
- `EditorSession` recibe las cantidades en el constructor (el proyecto inicial ya las usa) y las expone como `NewProjectTracks` para «Proyecto nuevo».
- Menú Ver → «Pistas de video / de audio de un proyecto nuevo» (1–8, con marca). Al elegir se guarda en los ajustes y rige desde el siguiente «Proyecto nuevo». No cambia el proyecto abierto.
- Pruebas nuevas: `tests/Rodaje.App.Tests/Settings/NewProjectTracksTests.cs` (12 casos: valores de hoy, 1–8, acotado, `Normalize`, guardar y leer, sesión y «Proyecto nuevo»). Corren en Linux con `-p:EnableWindowsTargeting=true`: 50 de 50 en los grupos Settings y EditorSession.

## Pendiente / decisiones

- La segunda mitad (de qué pistas de video sale el audio) NO se hizo; queda en `docs/pendientes.md`. No toqué `pendientes.md` (fuera de los caminos): quien una el PR puede quitar la primera viñeta de Personalización.
- No hay página de Ajustes en `Views`; los ajustes viven en el menú Ver (TitleBar), así que ahí quedó.
- Un proyecto nuevo con la secuencia de más de 2 pistas no cambia el formato `.rodaje`.

## Pruebas omitidas

Ninguna omitida ni excluida: 0 antes, 0 después.

## Choques

Ninguno conocido. `TitleBar.xaml(.cs)` y `MainWindow.xaml.cs` los tocan también otras tareas de interfaz: posible conflicto trivial.

## Qué comprobar en Windows

1. La compilación de WinUI (`TitleBar.xaml.cs`, `MainWindow.xaml.cs`) no se pudo verificar en Linux; sí las pruebas de lógica.
2. Ver → elegir 3 de video y 1 de audio → Archivo → Proyecto nuevo: aparecen 3 pistas de video y 1 de audio; cerrar y abrir la app: la elección se conserva y el proyecto inicial ya las trae.
3. Que los submenús marquen la cantidad vigente al abrirlos.
