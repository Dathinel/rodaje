namespace Rodaje.App.Services.Shortcuts;

/// <summary>El ratón de fábrica de cada estilo y zona.</summary>
public static class MouseDefaults
{
    private static readonly Dictionary<(ShortcutPreset, MouseContext), Dictionary<MouseChord, string>> Maps = Build();

    public static IReadOnlyDictionary<MouseChord, string> For(ShortcutPreset preset, MouseContext context) =>
        Maps.TryGetValue((preset, context), out Dictionary<MouseChord, string>? map) ? map : new Dictionary<MouseChord, string>();

    private static Dictionary<(ShortcutPreset, MouseContext), Dictionary<MouseChord, string>> Build()
    {
        const KeyModifiers ctrl = KeyModifiers.Control;
        const KeyModifiers shift = KeyModifiers.Shift;
        const KeyModifiers alt = KeyModifiers.Alt;
        var wheel = new MouseChord(MouseInput.Wheel);
        var tilt = new MouseChord(MouseInput.Tilt);
        var middle = new MouseChord(MouseInput.Middle);
        var x1 = new MouseChord(MouseInput.X1);
        var x2 = new MouseChord(MouseInput.X2);
        MouseChord W(KeyModifiers m) => new(MouseInput.Wheel, m);

        // Rodaje y Clásico usan los laterales para ir cuadro a cuadro; Ágil, para deshacer y rehacer.
        (string back, string forward) frames = (ActionIds.PreviousFrame, ActionIds.NextFrame);
        (string back, string forward) history = (ActionIds.Undo, ActionIds.Redo);

        Dictionary<MouseChord, string> Side((string back, string forward) side, Dictionary<MouseChord, string> map)
        {
            map[x1] = side.back;
            map[x2] = side.forward;
            return map;
        }

        return new()
        {
            // Como Resolve, CapCut y Final Cut: la rueda baja por las pistas y Mayús la lleva al tiempo.
            [(ShortcutPreset.Rodaje, MouseContext.Timeline)] = Side(frames, new()
            {
                [wheel] = MouseGestures.ScrollTracks,
                [W(ctrl)] = MouseGestures.Zoom,
                [W(shift)] = MouseGestures.ScrollTime,
                [W(alt)] = MouseGestures.TrackHeight,
                [tilt] = MouseGestures.ScrollTime,
                [middle] = MouseGestures.Pan,
            }),
            [(ShortcutPreset.Agile, MouseContext.Timeline)] = Side(history, new()
            {
                [wheel] = MouseGestures.ScrollTime,
                [W(ctrl)] = MouseGestures.Zoom,
                [W(alt)] = MouseGestures.ScrollTracks,
                [W(shift)] = MouseGestures.ScrollTracks,
                [tilt] = MouseGestures.ScrollTime,
                [middle] = MouseGestures.Pan,
            }),
            [(ShortcutPreset.Classic, MouseContext.Timeline)] = Side(frames, new()
            {
                [wheel] = MouseGestures.Zoom,
                [W(ctrl)] = MouseGestures.ScrollTime,
                [W(shift)] = MouseGestures.ScrollTracks,
                [W(alt)] = MouseGestures.Frames,
                [tilt] = MouseGestures.ScrollTime,
                [middle] = MouseGestures.Pan,
            }),
            [(ShortcutPreset.Rodaje, MouseContext.Viewer)] = Side(frames, new() { [wheel] = MouseGestures.Frames, [W(ctrl)] = MouseGestures.ViewerZoom }),
            [(ShortcutPreset.Agile, MouseContext.Viewer)] = Side(history, new() { [wheel] = MouseGestures.Frames, [W(ctrl)] = MouseGestures.ViewerZoom }),
            [(ShortcutPreset.Classic, MouseContext.Viewer)] = Side(frames, new() { [wheel] = MouseGestures.Frames, [W(ctrl)] = MouseGestures.ViewerZoom }),
            [(ShortcutPreset.Rodaje, MouseContext.Library)] = Side(frames, []),
            [(ShortcutPreset.Agile, MouseContext.Library)] = Side(history, []),
            [(ShortcutPreset.Classic, MouseContext.Library)] = Side(frames, []),
        };
    }
}
