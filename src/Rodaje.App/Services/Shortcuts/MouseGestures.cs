namespace Rodaje.App.Services.Shortcuts;

/// <summary>
/// Las acciones propias del ratón y lo que los botones izquierdo y derecho hacen siempre. A los botones central y
/// laterales también se les puede dar cualquier acción del teclado (<see cref="ShortcutAction"/>).
/// </summary>
public static class MouseGestures
{
    public const string Group = "Ratón";

    public const string Zoom = "mouse.zoom";
    public const string ScrollTime = "mouse.scrollTime";
    public const string ScrollTracks = "mouse.scrollTracks";
    public const string Frames = "mouse.frames";
    public const string ViewerZoom = "mouse.viewerZoom";
    public const string Pan = "mouse.pan";
    public const string TrackHeight = "mouse.trackHeight";

    public static IReadOnlyList<MouseGesture> All { get; } =
    [
        new(Zoom, "Zoom de la línea de tiempo", "Zoom", MouseGestureKind.Axis, "Arriba acerca y abajo aleja, alrededor del puntero.", MouseContext.Timeline),
        new(ScrollTime, "Desplazar en el tiempo", "Desplazar", MouseGestureKind.Axis, "Mueve la línea de tiempo a izquierda o derecha.", MouseContext.Timeline),
        new(ScrollTracks, "Desplazar las pistas", "Pistas", MouseGestureKind.Axis, "Sube o baja por las pistas.", MouseContext.Timeline),
        new(Frames, "Recorrer fotogramas", "Fotogramas", MouseGestureKind.Axis, "Cada paso mueve el cursor un fotograma: arriba atrás, abajo adelante.", MouseContext.Timeline, MouseContext.Viewer),
        new(ViewerZoom, "Zoom del visor", "Zoom visor", MouseGestureKind.Axis, "Pasa entre Ajustar, 50 % y 100 %.", MouseContext.Viewer),
        new(TrackHeight, "Alto de las pistas", "Alto", MouseGestureKind.Axis, "Arriba agranda y abajo achica la pista bajo el puntero.", MouseContext.Timeline),
        new(Pan, "Arrastrar la vista", "Arrastrar", MouseGestureKind.Drag, "Mientras lo mantienes, mueve la línea de tiempo con el ratón.", MouseContext.Timeline),
    ];

    public static MouseGesture? Find(string? id) => id is null ? null : All.FirstOrDefault(g => g.Id == id);

    public static bool IsGesture(string? id) => Find(id) is not null;

    /// <summary>
    /// Lo que hace de verdad la rueda en la línea de tiempo: sobre las cabeceras, desplazar en el tiempo pasa a bajar por
    /// las pistas; sobre las pistas, si todas se ven, bajar por ellas no haría nada y pasa a desplazar en el tiempo.
    /// </summary>
    public static string? EffectiveTimelineWheel(string? gesture, bool overHeaders, bool tilt, bool allTracksVisible)
    {
        if (tilt)
        {
            return gesture;
        }

        return gesture switch
        {
            ScrollTime when overHeaders => ScrollTracks,
            ScrollTracks when allTracksVisible && !overHeaders => ScrollTime,
            _ => gesture,
        };
    }

    /// <summary>
    /// Lo que hace siempre un botón que no se reasigna, o <c>null</c> si se puede cambiar. Izquierdo y derecho son para
    /// seleccionar y para el menú; la rueda de la biblioteca desplaza la lista.
    /// </summary>
    public static string? Fixed(MouseContext context, MouseChord chord) => (chord.Input, context) switch
    {
        (MouseInput.Left, MouseContext.Timeline) => chord.Modifiers switch
        {
            KeyModifiers.None => "Seleccionar y arrastrar clips",
            KeyModifiers.Control => "Sumar o quitar de la selección",
            KeyModifiers.Alt => "Arrastrar sin ajuste",
            _ => "Seleccionar",
        },
        (MouseInput.Left, MouseContext.Viewer) => "Mover y girar títulos",
        (MouseInput.Left, _) => chord.Modifiers == KeyModifiers.Control ? "Sumar a la selección" : "Elegir y arrastrar a la línea de tiempo",
        (MouseInput.Right, MouseContext.Timeline) => "Menú del clip",
        (MouseInput.Right, _) => "Menú",
        (MouseInput.Wheel or MouseInput.Tilt, MouseContext.Library) => "Desplazar la lista",
        _ => null,
    };

    /// <summary>Por qué esa parte no se puede cambiar, en español.</summary>
    public static string FixedReason(MouseContext context, MouseChord chord) => chord.Input switch
    {
        MouseInput.Left or MouseInput.Right => "Los clics izquierdo y derecho no se cambian: seleccionan, arrastran y abren el menú.",
        _ => $"En la {MouseNames.Display(context).ToLowerInvariant()} la rueda desplaza la lista y no se cambia.",
    };
}
