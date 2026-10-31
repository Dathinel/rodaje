using System.Globalization;
using Rodaje.App.Services.Settings;

namespace Rodaje.App.Services.Shortcuts;

/// <summary>
/// Las acciones que registran la línea de tiempo, el reproductor, los subtítulos y la ventana, con sus teclas de fábrica.
/// Viven aquí y no en las vistas para que el catálogo entero se arme sin WinUI: la guía <c>docs/guia/atajos.md</c> y la
/// hoja de atajos de Ayuda salen de <see cref="ShortcutCatalog.All"/>, y una prueba comprueba que la guía esté al día.
/// Las vistas registran su lista con <see cref="ShortcutRegistry.RegisterRange"/> y unen cada acción con lo que hace.
/// </summary>
public static class TimelineShortcuts
{
    public const string AutoCrossfade = "timeline.autoCrossfade";
    public const string LockEnvelopes = "timeline.lockEnvelopes";
    public const string IgnoreGroups = "timeline.ignoreGroups";
    public const string CreateCompound = "edit.compound";
    public const string DecomposeCompound = "edit.decompose";
    public const string LeaveCompound = "timeline.leaveCompound";
    public const string MarkIn = "edit.markIn";
    public const string MarkOut = "edit.markOut";
    public const string ClearMarks = "edit.clearMarks";
    public const string InsertSource = "edit.insertSource";
    public const string OverwriteSource = "edit.overwriteSource";
    public const string ToggleSource = "view.sourceProgram";
    public const string Group = "clip.group";
    public const string Ungroup = "clip.ungroup";
    public const string NudgeLeft = "clip.nudgeLeft";
    public const string NudgeRight = "clip.nudgeRight";
    public const string CloseGaps = "timeline.closeGaps";
    public const string NextGap = "timeline.nextGap";
    public const string PreviousGap = "timeline.previousGap";
    public const string ReplaceSource = "edit.replaceSource";
    public const string FitToFillSource = "edit.fitToFillSource";
    public const string SuperimposeSource = "edit.superimposeSource";
    public const string AnglePrefix = "multicam.angle";

    public static string Angle(int number) => AnglePrefix + number.ToString(CultureInfo.InvariantCulture);

    public static IReadOnlyList<ShortcutAction> Catalog { get; } =
    [
        new(AutoCrossfade, "Fundido cruzado automático", ShortcutGroups.Timeline, []) { ShortLabel = "Fundido auto", Description = "Fundido cruzado en cada corte entre clips pegados de la pista principal." },
        new(LockEnvelopes, "Bloquear envolventes", ShortcutGroups.Timeline, []) { ShortLabel = "Bloq. envolv.", Description = "Las envolventes viajan con su clip y no se editan por accidente." },
        new(IgnoreGroups, "Ignorar grupos", ShortcutGroups.Timeline, []) { ShortLabel = "Sin grupos", Description = "Mover y recortar solo el clip agarrado, sin su audio o video enlazado." },
        new(CreateCompound, "Crear clip compuesto", ShortcutGroups.Edit, ["Alt+G"], agile: ["Ctrl+G"]) { ShortLabel = "Compuesto", Description = "Junta los clips seleccionados en un solo clip; doble clic para editar lo de dentro." },
        new(DecomposeCompound, "Descomponer clip compuesto", ShortcutGroups.Edit, ["Alt+Mayús+G"], agile: ["Ctrl+Mayús+G"]) { ShortLabel = "Descomponer", Description = "Devuelve a la línea de tiempo los clips que había dentro del compuesto seleccionado." },
        new(LeaveCompound, "Salir del clip compuesto", ShortcutGroups.Timeline, []) { ShortLabel = "Salir", Description = "Vuelve a la secuencia que contiene el compuesto que se está editando." },
        new(Group, "Agrupar", ShortcutGroups.Edit, ["Ctrl+G"], agile: []) { Description = "Junta los clips elegidos: se seleccionan y se mueven juntos, aunque estén en la misma pista." },
        new(Ungroup, "Desagrupar", ShortcutGroups.Edit, ["Ctrl+Mayús+G"], agile: []) { Description = "Separa los clips del grupo; los enlaces de audio y video se mantienen." },
        new(NudgeLeft, "Mover clip un fotograma a la izquierda", ShortcutGroups.Edit, ["Alt+←"], classic: ["Ctrl+Alt+←"]) { ShortLabel = "Empujar ←", Repeats = true, Description = "Corre la selección (con sus enlazados y su grupo) un fotograma, sin pisar nada." },
        new(NudgeRight, "Mover clip un fotograma a la derecha", ShortcutGroups.Edit, ["Alt+→"], classic: ["Ctrl+Alt+→"]) { ShortLabel = "Empujar →", Repeats = true, Description = "Corre la selección (con sus enlazados y su grupo) un fotograma, sin pisar nada." },
        new(CloseGaps, "Cerrar todos los huecos", ShortcutGroups.Timeline, []) { ShortLabel = "Sin huecos", Description = "Junta los clips de la pista elegida (o de la principal) sin desincronizar los enlazados." },
        new(NextGap, "Ir al hueco siguiente", ShortcutGroups.Timeline, []) { ShortLabel = "Hueco →", Description = "Lleva el cursor al próximo tramo vacío de cualquier pista." },
        new(PreviousGap, "Ir al hueco anterior", ShortcutGroups.Timeline, []) { ShortLabel = "← Hueco", Description = "Lleva el cursor al tramo vacío anterior de cualquier pista." },
        new(ReplaceSource, "Reemplazar desde el origen", ShortcutGroups.Edit, ["Mayús+."]) { ShortLabel = "Reemplazar", Description = "Cambia el clip bajo el cursor por el origen, en su lugar y con su duración, alineando los dos cursores." },
        new(FitToFillSource, "Ajustar a rellenar desde el origen", ShortcutGroups.Edit, ["Ctrl+Mayús+."]) { ShortLabel = "Ajustar", Description = "El tramo del origen llena el clip bajo el cursor (o las marcas de la secuencia) cambiando la velocidad." },
        new(SuperimposeSource, "Superponer desde el origen", ShortcutGroups.Edit, ["Mayús+,"]) { ShortLabel = "Superponer", Description = "Pone la imagen del origen encima, en la primera pista de video libre." },
        .. Enumerable.Range(1, 9).Select(i => new ShortcutAction(Angle(i), $"Ángulo {i} de la multicámara", ShortcutGroups.Edit, [i.ToString(CultureInfo.InvariantCulture)]) { ShortLabel = $"Ángulo {i}", Description = "Corta en el cursor al ángulo con ese número; también mientras se reproduce." }),
        new(MarkIn, "Marcar entrada", ShortcutGroups.Edit, ["I"]) { ShortLabel = "Entrada", Description = "Marca la entrada en el fotograma que se ve, en el origen o en la secuencia." },
        new(MarkOut, "Marcar salida", ShortcutGroups.Edit, ["O"]) { ShortLabel = "Salida", Description = "Marca la salida: el fotograma que se ve es el último que entra." },
        new(ClearMarks, "Quitar entrada y salida", ShortcutGroups.Edit, ["Alt+X"], classic: ["Ctrl+Mayús+X"]) { ShortLabel = "Quitar marcas", Description = "Borra las marcas del visor que se ve." },
        new(InsertSource, "Insertar desde el origen", ShortcutGroups.Edit, [","]) { ShortLabel = "Insertar", Description = "Pone el tramo marcado del origen en la secuencia y corre lo que sigue; con un borde elegido, lo recorta un fotograma a la izquierda." },
        new(OverwriteSource, "Sobrescribir desde el origen", ShortcutGroups.Edit, ["."]) { ShortLabel = "Sobrescribir", Description = "Pone el tramo marcado del origen encima de lo que haya; con un borde elegido, lo recorta un fotograma a la derecha." },
        new(ToggleSource, "Visor de origen o de programa", ShortcutGroups.View, ["Mayús+2"]) { ShortLabel = "Origen", Description = "Cambia el reproductor entre el medio del origen y la secuencia." },
    ];
}

/// <summary>Las acciones del reproductor (además de las de reproducción del armazón).</summary>
public static class PlayerShortcuts
{
    public const string FullScreen = "view.playerFullScreen";
    public const string SafeAreas = "view.safeAreas";
    public const string ProjectFormat = "project.format";
    public const string SecondScreen = "view.secondScreen";

    public static IReadOnlyList<ShortcutAction> Catalog { get; } =
    [
        new(ActionIds.ShuttleReverse, "Lanzadera hacia atrás (más rápido con cada pulsación)", ShortcutGroups.Playback, ["J"]) { ShortLabel = "Atrás" },
        new(ActionIds.ShuttleStop, "Lanzadera: detener", ShortcutGroups.Playback, ["K"]) { ShortLabel = "Detener" },
        new(ActionIds.ShuttleForward, "Lanzadera hacia adelante (más rápido con cada pulsación)", ShortcutGroups.Playback, ["L"]) { ShortLabel = "Adelante" },
        new(FullScreen, "Reproductor en pantalla completa", ShortcutGroups.View, ["F"]) { ShortLabel = "Pantalla completa" },
        new(SafeAreas, "Mostrar u ocultar las zonas seguras", ShortcutGroups.View, []) { ShortLabel = "Zonas seguras" },
        new(ProjectFormat, "Formato del proyecto…", ShortcutGroups.View, []) { ShortLabel = "Formato" },
        new(SecondScreen, "Visor en otra pantalla (abrir o cerrar)", ShortcutGroups.View, []) { ShortLabel = "Otra pantalla" },
    ];
}

/// <summary>Importar y exportar archivos de subtítulos. Sin tecla de fábrica.</summary>
public static class SubtitleShortcuts
{
    public const string Import = "file.importSubtitles";
    public const string Export = "file.exportSubtitles";

    public static IReadOnlyList<ShortcutAction> Catalog { get; } =
    [
        new(Import, "Importar subtítulos", ShortcutGroups.File, []) { Description = "Trae un archivo .srt o .vtt a una pista de subtítulos nueva." },
        new(Export, "Exportar subtítulos", ShortcutGroups.File, []) { Description = "Guarda la pista de subtítulos como archivo .srt o .vtt." },
    ];
}

/// <summary>Las acciones de la ventana: recorrido inicial, pistas, carpetas vigiladas y los espacios de trabajo de fábrica.</summary>
public static class ShellShortcuts
{
    public const string Tour = "help.tour";
    public const string ResetHints = "help.resetHints";
    public const string WatchedFolders = "file.watchedFolders";
    public const string ShortcutSheet = "help.shortcutSheet";
    public const string WorkspacePrefix = "view.workspace.";

    /// <summary>El identificador de la acción que aplica un espacio de trabajo de fábrica.</summary>
    public static string Workspace(WorkspaceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return WorkspacePrefix + profile.Id["fabrica.".Length..];
    }

    public static IReadOnlyList<ShortcutAction> Catalog { get; } =
    [
        new(Tour, "Recorrido inicial", ShortcutGroups.View, []) { ShortLabel = "Recorrido" },
        new(ResetHints, "Volver a mostrar las pistas", ShortcutGroups.View, []) { ShortLabel = "Pistas" },
        new(ShortcutSheet, "Hoja de atajos (imprimible)", ShortcutGroups.View, []) { ShortLabel = "Hoja", Description = "Guarda en Documentos una página con todos los atajos del estilo actual, lista para imprimir." },
        .. WorkspaceProfiles.Factory.Select(profile => new ShortcutAction(Workspace(profile), $"Espacio de trabajo «{profile.Name}»", ShortcutGroups.View, []) { ShortLabel = profile.Name }),
        new(WatchedFolders, "Carpetas vigiladas…", ShortcutGroups.File, []) { ShortLabel = "Vigiladas" },
    ];
}

/// <summary>Todas las acciones con teclado de Rodaje, en el orden en que la app las registra.</summary>
public static class ShortcutCatalog
{
    public static IReadOnlyList<ShortcutAction> All { get; } =
    [
        .. EditorShortcuts.Catalog,
        .. TimelineShortcuts.Catalog,
        .. PlayerShortcuts.Catalog,
        .. SubtitleShortcuts.Catalog,
        .. ShellShortcuts.Catalog,
    ];

    /// <summary>Un registro con el catálogo entero y las teclas de fábrica del estilo pedido.</summary>
    public static ShortcutRegistry CreateRegistry(ShortcutPreset preset = ShortcutPreset.Rodaje)
    {
        var registry = new ShortcutRegistry();
        registry.RegisterRange(All);
        registry.Preset = preset;
        return registry;
    }
}
