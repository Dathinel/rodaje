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

    public const string Undo = "edit.undo";
    public const string Redo = "edit.redo";
    public const string Split = "edit.split";
    public const string Delete = "edit.delete";
    public const string TrimStart = "edit.trimStart";
    public const string TrimEnd = "edit.trimEnd";
    public const string SelectAll = "edit.selectAll";

    public const string TogglePlay = "playback.togglePlay";
    public const string PreviousFrame = "playback.previousFrame";
    public const string NextFrame = "playback.nextFrame";
    public const string GoToStart = "playback.start";
    public const string GoToEnd = "playback.end";

    public const string AddMarker = "timeline.marker";
    public const string AddRegion = "timeline.region";
    public const string ZoomIn = "timeline.zoomIn";
    public const string ZoomOut = "timeline.zoomOut";
    public const string ZoomToFit = "timeline.zoomFit";
    public const string ToggleSnapping = "timeline.snapping";
    public const string ToggleAutoRipple = "timeline.autoRipple";
    public const string ToggleMagneticMain = "timeline.magneticMain";

    public const string NormalTool = "tool.normal";
    public const string EnvelopeTool = "tool.envelope";
    public const string SelectionTool = "tool.selection";
    public const string ZoomTool = "tool.zoom";

    public const string ShowShortcuts = "view.shortcuts";
    public const string ShowPlugins = "view.plugins";
}

/// <summary>Grupos de la página de atajos.</summary>
public static class ShortcutGroups
{
    public const string File = "Archivo";
    public const string Edit = "Edición";
    public const string Playback = "Reproducción";
    public const string Timeline = "Línea de tiempo";
    public const string Tools = "Herramientas";
    public const string Windows = "Ventanas";
}

/// <summary>Las acciones del armazón con sus teclas de fábrica en cada estilo, y su unión con la sesión.</summary>
public static partial class EditorShortcuts
{
    /// <summary>Acciones del armazón. Las vistas registran las suyas aparte (<see cref="ShortcutRegistry.Register"/>).</summary>
    public static IReadOnlyList<ShortcutAction> Catalog { get; } =
    [
        new(ActionIds.NewProject, "Proyecto nuevo", ShortcutGroups.File, ["Ctrl+N"]) { WorksInTextInput = true },
        new(ActionIds.OpenProject, "Abrir proyecto", ShortcutGroups.File, ["Ctrl+O"]) { WorksInTextInput = true },
        new(ActionIds.Save, "Guardar", ShortcutGroups.File, ["Ctrl+S"]) { WorksInTextInput = true },
        new(ActionIds.SaveAs, "Guardar como", ShortcutGroups.File, ["Ctrl+Mayús+S"]) { WorksInTextInput = true },
        new(ActionIds.Import, "Importar medios", ShortcutGroups.File, ["Ctrl+I"]),
        new(ActionIds.Export, "Exportar", ShortcutGroups.File, ["Ctrl+E"]) { WorksInTextInput = true },

        new(ActionIds.Undo, "Deshacer", ShortcutGroups.Edit, ["Ctrl+Z"]) { Repeats = true },
        new(ActionIds.Redo, "Rehacer", ShortcutGroups.Edit, ["Ctrl+Mayús+Z", "Ctrl+Y"], capCut: ["Ctrl+Mayús+Z"], vegas: ["Ctrl+Y", "Ctrl+Mayús+Z"]) { Repeats = true },
        new(ActionIds.Split, "Cortar en el cursor", ShortcutGroups.Edit, ["S", "Ctrl+B"], capCut: ["Ctrl+B"], vegas: ["S"]),
        new(ActionIds.Delete, "Borrar", ShortcutGroups.Edit, ["Supr", "Retroceso"], vegas: ["Supr"]),
        new(ActionIds.TrimStart, "Recortar el inicio hasta el cursor", ShortcutGroups.Edit, ["Q"], vegas: ["Alt+["]),
        new(ActionIds.TrimEnd, "Recortar el final hasta el cursor", ShortcutGroups.Edit, ["W"], vegas: ["Alt+]"]),
        new(ActionIds.SelectAll, "Seleccionar todo", ShortcutGroups.Edit, ["Ctrl+A"]),

        new(ActionIds.TogglePlay, "Reproducir o pausar", ShortcutGroups.Playback, ["Espacio"]),
        new(ActionIds.PreviousFrame, "Cuadro anterior", ShortcutGroups.Playback, ["←"], vegas: ["Alt+←"]) { Repeats = true },
        new(ActionIds.NextFrame, "Cuadro siguiente", ShortcutGroups.Playback, ["→"], vegas: ["Alt+→"]) { Repeats = true },
        new(ActionIds.GoToStart, "Ir al inicio", ShortcutGroups.Playback, ["Inicio"]),
        new(ActionIds.GoToEnd, "Ir al final", ShortcutGroups.Playback, ["Fin"]),

        new(ActionIds.AddMarker, "Añadir marcador", ShortcutGroups.Timeline, ["M"]),
        new(ActionIds.AddRegion, "Añadir región", ShortcutGroups.Timeline, ["R"], capCut: []),
        new(ActionIds.ZoomIn, "Acercar", ShortcutGroups.Timeline, ["Ctrl++", "Ctrl+Num +"], vegas: ["↑"]) { Repeats = true },
        new(ActionIds.ZoomOut, "Alejar", ShortcutGroups.Timeline, ["Ctrl+-", "Ctrl+Num -"], vegas: ["↓"]) { Repeats = true },
        new(ActionIds.ZoomToFit, "Ver toda la secuencia", ShortcutGroups.Timeline, ["Mayús+Z"]),
        new(ActionIds.ToggleSnapping, "Ajuste", ShortcutGroups.Timeline, ["N"], vegas: ["F8"]),
        new(ActionIds.ToggleAutoRipple, "Ripple automático", ShortcutGroups.Timeline, ["Ctrl+L"], capCut: []),
        new(ActionIds.ToggleMagneticMain, "Pista principal magnética", ShortcutGroups.Timeline, []),

        new(ActionIds.NormalTool, "Herramienta normal", ShortcutGroups.Tools, ["V"], capCut: ["A"], vegas: ["Ctrl+D"]),
        new(ActionIds.EnvelopeTool, "Herramienta de envolvente", ShortcutGroups.Tools, []),
        new(ActionIds.SelectionTool, "Herramienta de selección", ShortcutGroups.Tools, []),
        new(ActionIds.ZoomTool, "Herramienta de zoom", ShortcutGroups.Tools, ["Z"], capCut: []),

        new(ActionIds.ShowShortcuts, "Atajos de teclado", ShortcutGroups.Windows, ["Ctrl+Alt+K"]),
        new(ActionIds.ShowPlugins, "Complementos", ShortcutGroups.Windows, []),
    ];

    /// <summary>
    /// Une las acciones de edición, reproducción y archivo con la sesión. Las de la ventana (exportar, páginas) y las de
    /// la línea de tiempo que todavía no existen en el modelo (marcadores, regiones, ver todo) las une quien las tiene.
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
            router.Bind(ActionIds.Delete, () => session.DeleteSelection(), () => session.SelectedClipIds.Count > 0),
            router.Bind(ActionIds.TrimStart, () => session.TrimStartToPlayhead()),
            router.Bind(ActionIds.TrimEnd, () => session.TrimEndToPlayhead()),
            router.Bind(ActionIds.SelectAll, session.SelectAll),

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
