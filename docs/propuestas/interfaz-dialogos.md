# Revisión: menús y diálogos

Revisión de lectura, sin cambios de código, de la barra de menús (Archivo, Editar, Ver, Complementos y también Atajos y Ayuda, que comparten la barra), las páginas modales (Exportar, Complementos) y los diálogos y avisos que cuelgan de ellas: formato del proyecto, ajustes, recuperación y avisos. Base: `main` en 2aac9bd. La app no se ejecutó: es WinUI y la revisión se hizo en Linux, así que las rutas y líneas salen del código.

## 1. Incongruencias, de la que más molesta a la que menos

1. **No hay Preferencias: los ajustes están repartidos entre Ver, Archivo y Atajos.** Ver mezcla cosas de mirar (pestañas, zoom) con ajustes que se guardan en `ajustes.json`: proxies, pistas de un proyecto nuevo, audio enlazado, calidad de la vista previa y calidad de la IA (`src/Rodaje.App/Shell/TitleBar.xaml:77-97`). La carpeta de medios generados está en Archivo (`TitleBar.xaml:41`) y el estilo de teclado tiene un menú de primer nivel propio (`TitleBar.xaml:105-111`). El propio código dice «se elige en Ajustes» (`src/Rodaje.App/Services/NewProjectTracks.cs:6`, `Services/Settings/LinkedAudioTracks.cs:6`, `Views/Timeline/Logic/ClipDrag.cs:51`), pero ese lugar no existe. Quien busca «cuántas pistas trae un proyecto nuevo» no lo encuentra en Ver.

2. **Los avisos tienen un solo hueco y el siguiente borra el anterior.** `ShowNotification` sustituye título, mensaje y botón (`src/Rodaje.App/Shell/MainWindow.xaml.cs:667-695`), y no hay cola ni historial. Al arrancar compiten en ese hueco el aviso de ajustes dañados (`MainWindow.xaml.cs:268-274`), la oferta de recuperar un autoguardado con su botón «Recuperar» (`src/Rodaje.App/Services/Autosave/AutosaveService.cs:145-147`), el de plantillas (`TitleBar.xaml.cs:273`) y el de proxies al importar (`MainWindow.Workflow.cs:165`). Si llega otro aviso, la oferta de recuperación desaparece y no vuelve hasta el siguiente arranque. Es lo más grave para el usuario: puede perder el trabajo recuperable sin enterarse.

3. **Hay dos maneras de ofrecer una recuperación.** El autoguardado perdido se ofrece en la barra de avisos (`AutosaveService.cs:145`) y las exportaciones a medias en un `ContentDialog` modal con «Reanudar» y «Descartar» (`src/Rodaje.App/Shell/MainWindow.Workflow.cs:401-412`). La primera, que es la más importante, es la que menos se ve. Además, «Descartar» borra la cola sin pedir confirmación (`MainWindow.Workflow.cs:432`) y es el botón Cerrar, el mismo que dispara Esc.

4. **«Proyecto» y «secuencia» se usan para lo mismo.** La interfaz dice «Formato del proyecto» (`src/Rodaje.App/Views/Player/PlayerView.Format.cs:24,39,215`; `Views/Export/ExportView.xaml:79,121,181`), pero el paso del historial se llama «Formato de la secuencia» (`src/Rodaje.App/Services/SequenceFormatCommand.cs:28`), de modo que Editar muestra «Deshacer «Formato de la secuencia»». La guía dice «la forma de la secuencia» (`docs/guia/exportar.md:35`).

5. **El formato del proyecto no está en ningún menú.** La acción está registrada en el grupo Ver (`PlayerView.Format.cs:24`), pero solo se llega por el botón del reproductor o por los enlaces de Exportar, que aparecen tres veces en la misma página (`ExportView.xaml:79,121,181`). Ver y Archivo no la ofrecen.

6. **Editar es en realidad «Herramientas».** Quitar silencios, ritmo, ducking, ruido, texto a voz y traducir (`TitleBar.xaml:58-63`) no son ediciones. La guía los llama «Herramientas» (`docs/guia/audio.md:80,102`; `docs/guia/primer-proyecto.md:43`), que es la barra de `Views/Tools/ToolsBar.xaml`, pero en el menú están bajo Editar. «Añadir pista de subtítulos» (`TitleBar.xaml:56`) pertenece a la línea de tiempo.

7. **La misma palabra significa dos cosas.**
   - «Actualizar» en la cabecera de Complementos recarga la lista (`src/Rodaje.App/Views/Plugins/PluginsView.xaml:68`); en cada fila instala una versión nueva (`Views/Plugins/PluginRow.cs:72`).
   - «Pistas» son pistas de la línea de tiempo en Ver (`TitleBar.xaml:84-86`) y consejos en Ayuda › «Volver a mostrar las pistas» (`TitleBar.xaml:114`).
   - «Trabajos» es la pestaña y el texto de los avisos («El avance está en Trabajos», `MainWindow.xaml.cs:476`), pero el panel se titula «Exportaciones» (`Views/Export/ExportJobsView.cs:47`).

8. **Al borrar algo propio, cada lista sigue un criterio distinto.** Una plantilla propia: «Borrar plantilla propia», pide confirmación (`TitleBar.xaml.cs:299,340`). Un espacio de trabajo: «Quitar un espacio propio», se borra al instante, sin confirmación ni deshacer (`src/Rodaje.App/Shell/TitleBar.Workflow.cs:480-488`). Un preajuste de exportación: «Quitar», sin confirmación, sin aviso y sin deshacer (`ExportView.xaml:226`, `ExportView.xaml.cs:877-883`). Un complemento: «Desinstalar» con confirmación (`PluginsView.xaml.cs:274-283`).

9. **El menú Ver muestra 5 de las 9 pestañas.** Faltan Velocidad, Máscaras, Seguimiento y Dinámica (`TitleBar.xaml:66-70` frente a `Views/Inspector/InspectorView.xaml.cs:19-30`), sin que se vea un criterio.

10. **Menús con una sola entrada y títulos que no coinciden.** «Complementos › Gestor de complementos...» es la única entrada del menú (`TitleBar.xaml:102-104`) y abre una página titulada «Complementos» (`MainWindow.xaml.cs:401`). El menú «Atajos» mezcla una página con un ajuste (`TitleBar.xaml:105-111`).

11. **Exportar es una página, pero se comporta como un diálogo, y la de Complementos no.** Exportar tiene «Cancelar» en el pie además de la X de la cabecera, «Cerrar (Esc)» (`ExportView.xaml:270`, `MainWindow.xaml:593`); Complementos solo tiene la X. Con cualquiera de las dos páginas abierta siguen vivos los atajos del grupo Archivo (`MainWindow.xaml.cs:408`), así que Ctrl+N o Ctrl+O abren otra pregunta debajo de la página.

12. **«Cuadro» y «fotograma».** El menú y el atajo dicen «Exportar fotograma» (`TitleBar.xaml:38`), pero su descripción dice «el cuadro que está bajo el cursor» (`Services/Shortcuts/EditorShortcuts.cs:102`). En el resto de la interfaz se dice «cuadros por segundo», «cuadro clave» y «cuadros perdidos» (`ExportView.xaml:156,164`, `TitleBar.xaml.cs:71`).

13. **Puntos suspensivos con dos grafías.** Hay 38 textos con «...» y 66 con «…» en `src/Rodaje.App`. En el mismo pie de Exportar conviven «Cambiar…» y «Varias versiones...» (`ExportView.xaml:57,271`). Tampoco se sigue un criterio sobre cuándo se ponen: «Exportar...» abre una página y «Carpeta de medios generados...» un diálogo que solo informa.

14. **Faltan estados vacíos y hay estados poco claros.**
    - Complementos no dice nada si «Contenido» o «Módulos de IA» quedan vacíos, por ejemplo sin conexión y sin nada instalado (`PluginsView.xaml:86-91`).
    - El aviso de pausa dice «"Instalar" sigue desde ahí» (`PluginsView.xaml.cs:205`) aunque el botón de esa fila diga «Actualizar».
    - La oferta de recuperación escribe «copia(s)» y fija la fecha con `InvariantCulture` y el formato `dd/MM/yyyy` (`AutosaveService.cs:143-144`).
    - Los avisos de desinstalación y de modelos listos tienen el mensaje vacío (`PluginsView.xaml.cs:229,268`).

15. **Ajustes con dos mandos y nombres distintos.** La calidad de la vista previa está en Ver y en el reproductor; el menú dice «Automática» y el botón muestra «Auto» (`TitleBar.xaml:88`, `Views/Player/PlayerView.xaml:35`). Los proxies son «Proxies en la vista previa» en Ver y «Proxy» en el reproductor (`PlayerView.xaml:31`).

**Lo que está bien hecho y conviene conservar:** `ConfirmAsync` pone Cancelar como botón por defecto en lo que no se deshace (`ShellDialogs.cs:44`); los avisos de cambios hechos llevan «Deshacer» (`PlayerView.Format.cs:145`); `DialogGate` evita los diálogos dobles; y las descargas de complementos muestran avance y se pueden cancelar.

## 2. Propuesta de organización

Criterios:

- Un menú agrupa verbos sobre un mismo objeto.
- Los ajustes persistentes van todos en una ventana de Preferencias.
- Un nombre por concepto: «proyecto», «cuadro», «Trabajos», «consejos».
- «…» solo cuando hace falta algo más antes de actuar.
- Toda supresión de algo propio pide confirmación o ofrece «Deshacer».

```
Archivo
  Proyecto nuevo                     Ctrl+N
  Proyecto nuevo desde plantilla   ▸ (fábrica · propias · Administrar plantillas…)
  Abrir proyecto…                    Ctrl+O
  Abrir reciente                   ▸
  ─
  Guardar / Guardar como… / Guardar como plantilla…
  ─
  Importar                         ▸ Medios… · CinemaDNG… · Subtítulos…
  Carpetas vigiladas…
  ─
  Exportar…                          Ctrl+E
  Exportar cuadro…  (antes «fotograma»)
  Exportar subtítulos…
  ─
  Salir

Editar
  Deshacer «…» / Rehacer «…»
  ─
  Cortar en el cursor · Recortar inicio · Recortar final · Borrar · Seleccionar todo

Secuencia   (nuevo; reúne la línea de tiempo y el formato)
  Formato del proyecto…
  Añadir texto · Añadir pista de subtítulos
  Acercar · Alejar

Herramientas   (mismo nombre que la barra; mismas entradas)
  Quitar silencios… · Detectar ritmo… · Bajar música bajo la voz… · Reducir ruido…
  ─
  Texto a voz… · Traducir pista…

Ver
  Paneles   ▸ las 9 pestañas, en el orden del panel derecho
  Espacio de trabajo ▸ (… · Guardar la distribución actual… · Administrar…)
  Mezclador · Pantalla completa · Visor en otra pantalla
  Calidad de la vista previa ▸   (el mismo control que el reproductor, con los mismos nombres)

Complementos
  Administrar complementos…   (la página se titula igual)
  Instalar desde archivo…
  Buscar actualizaciones

Ayuda
  Atajos de teclado y ratón…   (sale del menú «Atajos»)
  Recorrido inicial · Volver a mostrar los consejos
  ─
  Acerca de Rodaje… · Repositorio oficial

Preferencias…   (Archivo › Preferencias o Ctrl+,; una página modal como Complementos)
  General      Carpeta de medios generados · Pistas de un proyecto nuevo · Audio de las pistas de video
  Rendimiento  Proxies (uso y generar al importar) · Calidad de la IA
  Teclado      Estilo (Rodaje, Ágil, Clásico) · enlace a Atajos de teclado y ratón
```

Avisos y recuperación:

```
Arranque     Recuperar autoguardado   → diálogo (como las exportaciones), antes que cualquier otra cosa
             Exportaciones sin terminar → el mismo diálogo, después; «Descartar» confirma
Avisos       cola por prioridad: Error y los que tienen acción no los pisa un Informativo
             historial breve (los últimos 20), accesible desde la barra de avisos
Páginas      Exportar, Complementos, Atajos y Preferencias: solo la X y Esc cierran;
             Exportar conserva su «Exportar» principal; con una página abierta no
             responden Ctrl+N ni Ctrl+O
Borrar       plantilla, espacio y preajuste propios: confirmación con Cancelar por defecto
             (como ConfirmAsync) o aviso con «Deshacer»
```

## 3. Qué probar con pruebas de lógica (sin WinUI)

`tests/Rodaje.App.Tests` ya lee XAML como texto (`Support/InvariantGlobalizationTests.cs`, `Inspector/ComboDeferralTests.cs`). Con ese mismo método:

- **Glosario de la interfaz:** recorrer los `Text`, `Content`, `Title` y `*ButtonText` de `src/Rodaje.App` (XAML y literales en C#) y fallar si aparece una palabra vetada: «fotograma», «secuencia» en un texto visible, «...» en lugar de «…», «pistas» con el sentido de consejos. La lista de términos vive en un archivo de la prueba.
- **Menú contra registro de atajos:** cada `Tag` de `TitleBar.xaml` con forma de id de acción existe en `ShortcutRegistry`, y toda acción de los grupos Archivo y Ver aparece en algún menú (detectaría el formato del proyecto, punto 5).
- **Ver › Paneles completo:** el conjunto de `inspector:*` del menú coincide con el enum `InspectorView.Tab`.
- **Prioridad de avisos:** sacar a una clase sin WinUI la decisión de qué aviso se muestra (hoy dentro de `ShowNotification`) y probar que uno Informativo no tapa uno con acción ni un Error, y que la oferta de recuperación sigue disponible después.
- **Recuperación:** que `AutosaveService.OfferRecoveryAsync` formatea la fecha con la cultura española y escribe el plural sin «(s)»; y que descartar la cola de exportaciones solo ocurre después de confirmarlo (con `IEditorDialogs` falso, como ya hacen las pruebas de `Autosave`).
- **Borrados propios:** con un `IEditorDialogs` falso, quitar un espacio de trabajo, una plantilla o un preajuste pasa por `ConfirmAsync` (o deja un aviso con «Deshacer»), y cancelar no borra nada (`WorkspaceProfiles`, `ProjectTemplateStore` y `ExportDialogModel.RemovePreset` no dependen de WinUI).
- **Preferencias:** cada propiedad pública de `AppSettings` que se cambia desde un menú tiene exactamente una entrada en el modelo de Preferencias (un modelo puro, igual que `ExportDialogModel`).
