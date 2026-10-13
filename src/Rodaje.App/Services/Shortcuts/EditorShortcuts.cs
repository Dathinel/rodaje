namespace Rodaje.App.Services.Shortcuts;

/// <summary>Identificadores estables de las acciones del armazón; se guardan en <c>atajos.json</c>, no se renombran.</summary>
public static class ActionIds
{
    public const string NewProject = "file.new";
    public const string OpenProject = "file.open";
    public const string Save = "file.save";
    public const string SaveAs = "file.saveAs";
    public const string Import = "file.import";
    public const string Export = "file.export";
    public const string ExportFrame = "file.exportFrame";

    public const string Undo = "edit.undo";
    public const string Redo = "edit.redo";
    public const string Split = "edit.split";
    public const string SplitAllTracks = "edit.splitAll";
    public const string Delete = "edit.delete";
    public const string TrimStart = "edit.trimStart";
    public const string TrimEnd = "edit.trimEnd";
    public const string SelectAll = "edit.selectAll";

    public const string ClipSpeed = "clip.speed";
    public const string FreezeFrame = "clip.freeze";
    public const string SpeedUp = "clip.faster";
    public const string SlowDown = "clip.slower";
    public const string Reverse = "clip.reverse";
    public const string AddText = "clip.addText";
    public const string AddSubtitleTrack = "timeline.subtitleTrack";

    public const string TogglePlay = "playback.togglePlay";
    public const string PreviousFrame = "playback.previousFrame";
    public const string NextFrame = "playback.nextFrame";
    public const string GoToStart = "playback.start";
    public const string GoToEnd = "playback.end";

    // Las registra el reproductor; aquí para que la vista esencial de la página de atajos las nombre.
    public const string ShuttleReverse = "playback.shuttleReverse";
    public const string ShuttleStop = "playback.shuttleStop";
    public const string ShuttleForward = "playback.shuttleForward";

    public const string AddMarker = "timeline.marker";
    public const string AddRegion = "timeline.region";
    public const string ZoomIn = "timeline.zoomIn";
    public const string ZoomOut = "timeline.zoomOut";
    public const string ZoomToFit = "timeline.zoomFit";
    public const string ZoomToSelection = "timeline.zoomSelection";
    public const string ToggleSnapping = "timeline.snapping";
    public const string ToggleAutoRipple = "timeline.autoRipple";
    public const string ToggleMagneticMain = "timeline.magneticMain";

    public const string NormalTool = "tool.normal";
    public const string EnvelopeTool = "tool.envelope";
    public const string SelectionTool = "tool.selection";
    public const string ZoomTool = "tool.zoom";
    public const string RollTool = "tool.roll";
    public const string SlideTool = "tool.slide";

    public const string ShowShortcuts = "view.shortcuts";
    public const string ShowPlugins = "view.plugins";
    public const string ShowMixer = "view.mixer";
}

/// <summary>Grupos de la página de atajos, en el orden en que se muestran.</summary>
public static class ShortcutGroups
{
    public const string Playback = "Reproducción";
    public const string Edit = "Edición";
    public const string Timeline = "Línea de tiempo";
    public const string Tools = "Herramientas";
    public const string View = "Vista";
    public const string File = "Archivo";

    /// <summary>Páginas y paneles: ahora forman parte de <see cref="View"/>.</summary>
    public const string Windows = View;

    /// <summary>Orden de la página; los grupos que registren las vistas y no estén aquí van al final.</summary>
    public static IReadOnlyList<string> Order { get; } = [Playback, Edit, Timeline, Tools, View, File];
}

/// <summary>Las acciones del armazón con sus teclas de fábrica en cada estilo, y su unión con la sesión.</summary>
public static partial class EditorShortcuts
{
    /// <summary>Acciones del armazón. Las vistas registran las suyas aparte (<see cref="ShortcutRegistry.Register"/>).</summary>
    public static IReadOnlyList<ShortcutAction> Catalog { get; } =
    [
        new(ActionIds.NewProject, "Proyecto nuevo", ShortcutGroups.File, ["Ctrl+N"]) { ShortLabel = "Nuevo", WorksInTextInput = true, Description = "Empieza un proyecto vacío; si hay cambios sin guardar, pregunta antes." },
        new(ActionIds.OpenProject, "Abrir proyecto", ShortcutGroups.File, ["Ctrl+O"]) { ShortLabel = "Abrir", WorksInTextInput = true, Description = "Abre un archivo .rodaje guardado." },
        new(ActionIds.Save, "Guardar", ShortcutGroups.File, ["Ctrl+S"]) { WorksInTextInput = true, Description = "Guarda el proyecto en su archivo." },
        new(ActionIds.SaveAs, "Guardar como", ShortcutGroups.File, ["Ctrl+Mayús+S"]) { ShortLabel = "Guardar como", WorksInTextInput = true, Description = "Guarda una copia con otro nombre o en otra carpeta." },
        new(ActionIds.Import, "Importar medios", ShortcutGroups.File, ["Ctrl+I"]) { ShortLabel = "Importar", Description = "Trae videos, audios o imágenes a la biblioteca." },
        new(ActionIds.Export, "Exportar", ShortcutGroups.File, ["Ctrl+E", "Ctrl+M"], agile: ["Ctrl+E"], classic: ["Ctrl+M"]) { WorksInTextInput = true, Description = "Convierte el proyecto en un video terminado." },
        new(ActionIds.ExportFrame, "Exportar fotograma", ShortcutGroups.File, ["Ctrl+Mayús+E"]) { ShortLabel = "Fotograma", Description = "Guarda como imagen PNG o JPEG el cuadro que está bajo el cursor." },

        new(ActionIds.Undo, "Deshacer", ShortcutGroups.Edit, ["Ctrl+Z"]) { Repeats = true, Description = "Vuelve atrás el último cambio." },
        new(ActionIds.Redo, "Rehacer", ShortcutGroups.Edit, ["Ctrl+Mayús+Z", "Ctrl+Y"], agile: ["Ctrl+Mayús+Z"], classic: ["Ctrl+Y", "Ctrl+Mayús+Z"]) { Repeats = true, Description = "Repite lo que acabas de deshacer." },
        new(ActionIds.Split, "Cortar en el cursor", ShortcutGroups.Edit, ["S", "Ctrl+B"], agile: ["Ctrl+B"], classic: ["S"]) { ShortLabel = "Cortar", Description = "Parte en dos el clip que está bajo el cursor de reproducción." },
        new(ActionIds.Delete, "Borrar", ShortcutGroups.Edit, ["Supr", "Retroceso"], classic: ["Supr"]) { Description = "Quita de la línea de tiempo los clips o la transición seleccionados." },
        new(ActionIds.SplitAllTracks, "Cortar en todas las pistas", ShortcutGroups.Edit, ["Ctrl+Mayús+B"], classic: ["Ctrl+Mayús+K"]) { ShortLabel = "Cortar todo", Description = "Parte en el cursor los clips de todas las pistas desbloqueadas a la vez." },
        new(ActionIds.TrimStart, "Borrar a la izquierda del cursor", ShortcutGroups.Edit, ["Q"], classic: ["Alt+["]) { ShortLabel = "Borrar izquierda", Description = "Quita del clip lo que hay antes del cursor; en la principal no deja hueco." },
        new(ActionIds.TrimEnd, "Borrar a la derecha del cursor", ShortcutGroups.Edit, ["W"], classic: ["Alt+]"]) { ShortLabel = "Borrar derecha", Description = "Quita del clip lo que hay después del cursor; en la principal no deja hueco." },
        new(ActionIds.SelectAll, "Seleccionar todo", ShortcutGroups.Edit, ["Ctrl+A"]) { ShortLabel = "Todo", Description = "Selecciona todos los clips de la secuencia." },
        new(ActionIds.ClipSpeed, "Velocidad del clip…", ShortcutGroups.Edit, ["Ctrl+R"]) { ShortLabel = "Velocidad", Description = "Abre la pestaña Velocidad del clip seleccionado o del que está bajo el cursor." },
        new(ActionIds.FreezeFrame, "Congelar cuadro", ShortcutGroups.Edit, ["Mayús+R"]) { ShortLabel = "Congelar", Description = "Detiene la imagen del cursor 3 segundos y corre lo que sigue." },
        new(ActionIds.SpeedUp, "Más rápido", ShortcutGroups.Edit, ["Ctrl+Alt+."]) { Repeats = true, Description = "Sube un escalón la velocidad del clip (1,25×, 1,5×, 2×…)." },
        new(ActionIds.SlowDown, "Más lento", ShortcutGroups.Edit, ["Ctrl+Alt+,"]) { Repeats = true, Description = "Baja un escalón la velocidad del clip (0,75×, 0,5×, 0,25×…)." },
        new(ActionIds.Reverse, "Invertir", ShortcutGroups.Edit, []) { Description = "Reproduce el clip hacia atrás; otra vez lo vuelve a poner hacia adelante." },
        new(ActionIds.AddText, "Añadir texto", ShortcutGroups.Edit, ["T"]) { ShortLabel = "Texto", Description = "Pone un título de 5 segundos en el cursor, en la pista de arriba que esté libre." },

        new(ActionIds.TogglePlay, "Reproducir o pausar", ShortcutGroups.Playback, ["Espacio"]) { ShortLabel = "Reproducir", Description = "Arranca o detiene la reproducción desde el cursor." },
        new(ActionIds.PreviousFrame, "Cuadro anterior", ShortcutGroups.Playback, ["←"], classic: ["Alt+←"]) { ShortLabel = "Cuadro atrás", Repeats = true, Description = "Retrocede una imagen; mantenla para ir más atrás." },
        new(ActionIds.NextFrame, "Cuadro siguiente", ShortcutGroups.Playback, ["→"], classic: ["Alt+→"]) { ShortLabel = "Cuadro adelante", Repeats = true, Description = "Avanza una imagen; mantenla para seguir avanzando." },
        new(ActionIds.GoToStart, "Ir al inicio", ShortcutGroups.Playback, ["Inicio"]) { ShortLabel = "Inicio", Description = "Lleva el cursor al principio de la secuencia." },
        new(ActionIds.GoToEnd, "Ir al final", ShortcutGroups.Playback, ["Fin"]) { ShortLabel = "Final", Description = "Lleva el cursor al final del último clip." },

        new(ActionIds.AddMarker, "Añadir marcador", ShortcutGroups.Timeline, ["M"]) { ShortLabel = "Marcador", Description = "Deja una marca en el cursor para volver a ese punto." },
        new(ActionIds.AddRegion, "Añadir región", ShortcutGroups.Timeline, ["R"], agile: []) { ShortLabel = "Región", Description = "Marca un tramo de la secuencia con nombre." },
        new(ActionIds.ToggleSnapping, "Ajuste", ShortcutGroups.Timeline, ["N"], classic: ["F8"]) { Description = "Activa o quita el imán que pega los clips a bordes y al cursor." },
        new(ActionIds.ToggleAutoRipple, "Ripple automático", ShortcutGroups.Timeline, ["Ctrl+L"], agile: []) { ShortLabel = "Ripple", Description = "Al borrar o mover, corre lo que viene detrás para no dejar huecos." },
        new(ActionIds.AddSubtitleTrack, "Añadir pista de subtítulos", ShortcutGroups.Timeline, []) { ShortLabel = "Subtítulos", Description = "Crea encima de todas una pista para los subtítulos." },
        new(ActionIds.ToggleMagneticMain, "Pista principal magnética", ShortcutGroups.Timeline, []) { ShortLabel = "Magnética", Description = "Mantiene los clips de la pista principal pegados unos a otros." },

        new(ActionIds.NormalTool, "Herramienta normal", ShortcutGroups.Tools, ["V"], agile: ["A"], classic: ["Ctrl+D"]) { ShortLabel = "Normal", Description = "Seleccionar, mover y recortar clips con el ratón." },
        new(ActionIds.EnvelopeTool, "Herramienta de envolvente", ShortcutGroups.Tools, ["E"], agile: []) { ShortLabel = "Envolvente", Description = "Dibuja curvas de volumen u opacidad sobre los clips." },
        new(ActionIds.RollTool, "Herramienta de roll", ShortcutGroups.Tools, ["Y"], agile: ["Mayús+Y"], classic: ["N"]) { ShortLabel = "Roll", Description = "Arrastra un corte: un clip crece lo que el otro pierde (o Ctrl+Alt en un borde)." },
        new(ActionIds.SlideTool, "Herramienta de slide", ShortcutGroups.Tools, ["U"], agile: ["Mayús+U"], classic: ["U"]) { ShortLabel = "Slide", Description = "Corre un clip entre sus vecinos, que se recortan (o Ctrl+Alt en el cuerpo)." },
        new(ActionIds.SelectionTool, "Herramienta de selección", ShortcutGroups.Tools, []) { ShortLabel = "Selección", Description = "Arrastra un recuadro para seleccionar varios clips." },
        new(ActionIds.ZoomTool, "Herramienta de zoom", ShortcutGroups.Tools, ["Z"], agile: []) { ShortLabel = "Zoom", Description = "Haz clic o arrastra en la línea de tiempo para acercar ese tramo." },

        new(ActionIds.ZoomIn, "Acercar", ShortcutGroups.View, ["Ctrl++", "Ctrl+Num +"], classic: ["↑"]) { Repeats = true, Description = "Agranda la línea de tiempo para ver más detalle." },
        new(ActionIds.ZoomOut, "Alejar", ShortcutGroups.View, ["Ctrl+-", "Ctrl+Num -"], classic: ["↓"]) { Repeats = true, Description = "Achica la línea de tiempo para ver más tiempo a la vez." },
        new(ActionIds.ZoomToFit, "Ajustar a la ventana", ShortcutGroups.View, ["Mayús+Z"]) { ShortLabel = "Ver todo", Description = "Ajusta el zoom para que todo el proyecto quepa en la vista." },
        new(ActionIds.ZoomToSelection, "Zoom a la selección", ShortcutGroups.View, ["Ctrl+Alt+Z"]) { ShortLabel = "Ver selección", Description = "Acerca hasta que los clips seleccionados o el tramo marcado llenen la vista." },
        new(ActionIds.ShowShortcuts, "Atajos de teclado", ShortcutGroups.View, ["Ctrl+Alt+K"]) { ShortLabel = "Atajos", Description = "Abre esta página para ver y cambiar las teclas." },
        new(ActionIds.ShowPlugins, "Complementos", ShortcutGroups.View, []) { Description = "Abre el gestor de paquetes y módulos de IA." },
        new(ActionIds.ShowMixer, "Mezclador", ShortcutGroups.View, ["Alt+M"]) { Description = "Cambia la línea de tiempo por el mezclador de pistas y buses; otra vez vuelve." },
    ];

    /// <summary>
    /// Une las acciones de edición, reproducción y archivo con la sesión. Las de la ventana (exportar, páginas) y las de
    /// la línea de tiempo que todavía no existen en el modelo (marcadores, regiones, ver todo, ver la selección) las une quien las tiene.
    /// </summary>
    public static IDisposable Bind(ShortcutRouter router, EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(session);
        TimelineState timeline = session.Timeline;
        IDisposable[] bindings =
        [
            router.Bind(ActionIds.NewProject, () => Run(session, session.NewProjectAsync)),
            router.Bind(ActionIds.OpenProject, () => Run(session, () => session.OpenProjectAsync())),
            router.Bind(ActionIds.Save, () => Run(session, session.SaveAsync)),
            router.Bind(ActionIds.SaveAs, () => Run(session, () => session.SaveAsAsync())),
            router.Bind(ActionIds.Import, () => Run(session, session.ImportMediaAsync)),

            router.Bind(ActionIds.Undo, () => session.Undo(), () => session.CanUndo),
            router.Bind(ActionIds.Redo, () => session.Redo(), () => session.CanRedo),
            router.Bind(ActionIds.Split, () => session.SplitAtPlayhead()),
            router.Bind(ActionIds.Delete, () => session.DeleteSelection(), () => session.SelectedClipIds.Count > 0 || session.SelectedTransitionId is not null),
            router.Bind(ActionIds.SplitAllTracks, () => session.SplitAllTracksAtPlayhead()),
            router.Bind(ActionIds.TrimStart, () => session.DeleteLeftOfPlayhead()),
            router.Bind(ActionIds.TrimEnd, () => session.DeleteRightOfPlayhead()),
            router.Bind(ActionIds.SelectAll, session.SelectAll),
            router.Bind(ActionIds.ClipSpeed, () => session.ShowClipSpeed()),
            router.Bind(ActionIds.FreezeFrame, () => session.FreezeFrameAtPlayhead()),
            router.Bind(ActionIds.SpeedUp, () => session.StepSpeed(faster: true)),
            router.Bind(ActionIds.SlowDown, () => session.StepSpeed(faster: false)),
            router.Bind(ActionIds.Reverse, () => session.ToggleReverse()),
            router.Bind(ActionIds.AddText, () => session.AddTitleAtPlayhead()),
            router.Bind(ActionIds.AddSubtitleTrack, () => session.AddSubtitleTrack()),

            router.Bind(ActionIds.TogglePlay, session.TogglePlayPause),
            router.Bind(ActionIds.PreviousFrame, () => session.StepFrames(-1)),
            router.Bind(ActionIds.NextFrame, () => session.StepFrames(1)),
            router.Bind(ActionIds.GoToStart, session.SeekToStart),
            router.Bind(ActionIds.GoToEnd, session.SeekToEnd),

            router.Bind(ActionIds.ZoomIn, timeline.ZoomIn),
            router.Bind(ActionIds.ZoomOut, timeline.ZoomOut),
            router.Bind(ActionIds.ToggleSnapping, () => timeline.Snapping = !timeline.Snapping),
            router.Bind(ActionIds.ToggleAutoRipple, () => timeline.AutoRipple = !timeline.AutoRipple),
            router.Bind(ActionIds.ToggleMagneticMain, () => timeline.MagneticMainTrack = !timeline.MagneticMainTrack),

            router.Bind(ActionIds.NormalTool, () => timeline.Tool = TimelineTool.Normal),
            router.Bind(ActionIds.EnvelopeTool, () => timeline.Tool = TimelineTool.Envelope),
            router.Bind(ActionIds.SelectionTool, () => timeline.Tool = TimelineTool.Selection),
            router.Bind(ActionIds.ZoomTool, () => timeline.Tool = TimelineTool.Zoom),
            router.Bind(ActionIds.RollTool, () => timeline.Tool = TimelineTool.Roll),
            router.Bind(ActionIds.SlideTool, () => timeline.Tool = TimelineTool.Slide),
        ];
        return new BindingSet(bindings);
    }

    /// <summary>Lanza una operación asíncrona desde un atajo; un error inesperado se avisa en vez de perderse.</summary>
    private static async void Run(EditorSession session, Func<Task> operation)
    {
        try
        {
            await operation();
        }
#pragma warning disable CA1031 // Un atajo no debe tumbar la aplicación: el error se muestra en la ventana.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            session.Notify(NotificationSeverity.Error, "Algo falló", ex.Message);
        }
    }

    private sealed partial class BindingSet(IDisposable[] bindings) : IDisposable
    {
        public void Dispose()
        {
            foreach (IDisposable binding in bindings)
            {
                binding.Dispose();
            }
        }
    }
}
