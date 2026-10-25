using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.App.Shell;
using Rodaje.App.Views.Player;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using M = Rodaje.Core.Model.BuiltInEffects.MaskParameters;
using O = Rodaje.Core.Model.BuiltInEffects.MaskOptions;

namespace Rodaje.App.Views.Inspector.Masks;

/// <summary>Una máscara de la lista tal como se muestra.</summary>
internal sealed partial class MaskItem : IEquatable<MaskItem>
{
    public MaskItem(EffectInstance mask, string target, int number)
    {
        EffectId = mask.Id;
        Name = mask.Options.GetValueOrDefault(O.Name) is { Length: > 0 } name ? name : $"Máscara {number}";
        MaskShape shape = MaskGeometry.ParseShape(mask.Options.GetValueOrDefault(O.Shape));
        string invert = mask.Options.GetValueOrDefault(O.Invert) == "on" ? " · invertida" : "";
        string animated = MaskGeometry.IsShapeAnimated(mask) ? " · animada" : "";
        Note = EffectUi.MaskShapeName(MaskGeometry.ShapeName(shape)) + invert + animated + (target.Length > 0 ? " · en " + target : "");
        ModeLabel = EffectUi.MaskModeName(mask.Options.GetValueOrDefault(O.Mode) ?? "intersect");
        Color = mask.Options.GetValueOrDefault(O.Color) ?? MaskHandleColors.Default;
        Swatch = new SolidColorBrush(MasksPanel.ParseColor(Color));
        IsEnabled = mask.Enabled;
        IsHidden = !mask.Enabled;
        EnabledGlyph = mask.Enabled ? "" : "";
        EnabledTip = (mask.Enabled ? "Ocultar «" : "Mostrar «") + Name + "»";
        TextOpacity = mask.Enabled ? 1 : 0.5;
    }

    public string EffectId { get; }

    public string Name { get; }

    public string Note { get; }

    public string ModeLabel { get; }

    public string Color { get; }

    public Brush Swatch { get; }

    public bool IsEnabled { get; }

    public bool IsHidden { get; }

    public string EnabledGlyph { get; }

    public string EnabledTip { get; }

    public double TextOpacity { get; }

    public bool Equals(MaskItem? other) =>
        other is not null && EffectId == other.EffectId && Name == other.Name && Note == other.Note && ModeLabel == other.ModeLabel
        && Color == other.Color && IsEnabled == other.IsEnabled;

    public override bool Equals(object? obj) => Equals(obj as MaskItem);

    public override int GetHashCode() => HashCode.Combine(EffectId, Name, Note, ModeLabel, Color, IsEnabled);
}

/// <summary>
/// La pestaña «Máscaras»: herramientas del visor, la lista de máscaras del clip y la elegida en secciones. Todo cambio es un
/// comando deshacible; el análisis del movimiento vive en la pestaña Seguimiento.
/// </summary>
internal sealed partial class MasksPanel : UserControl, IEditorView
{
    private static readonly string[] BoxNames = [M.X, M.Y, M.Width, M.Height, M.Rotation];

    private static readonly (string Name, string Value)[] ColorNames =
    [
        ("Celeste", "#4FC3F7"), ("Naranja", "#FFB74D"), ("Verde", "#81C784"), ("Rosa", "#F06292"),
        ("Violeta", "#BA68C8"), ("Amarillo", "#FFF176"), ("Turquesa", "#4DB6AC"), ("Rojo", "#E57373"),
    ];

    private EditorServices? _services;
    private ClipPropertyEditor? _editor;
    private EffectRowGroup? _boxRows;
    private EffectRowGroup? _edgeRows;
    private string? _clipId;
    private string? _maskId;
    // Lo que muestra ahora el desplegable de destino (ver ComboBoxChoices).
    private ChoiceList<string> _targets = new();
    private readonly ClosedListRetry _targetsRetry = new();
    private Time _shownPosition;
    private bool _updating;
    private bool _weightDragging;

    public MasksPanel()
    {
        InitializeComponent();
        TargetBox.RetryWhenClosed(_targetsRetry, Refresh);
        // Los pasos del borde se funden por su clave; soltar o dejar el deslizador (también con el teclado) corta la fusión.
        WeightSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _weightDragging = true), true);
        WeightSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => OnWeightReleased()), true);
        WeightSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => OnWeightReleased()), true);
        WeightSlider.LostFocus += (_, _) => OnWeightReleased();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Publish());
        foreach (string mode in EffectRegistry.MaskModes)
        {
            ModeBox.Items.Add(EffectUi.MaskModeName(mode));
        }
    }

    public ObservableCollection<MaskItem> Items { get; } = [];

    internal static Windows.UI.Color ParseColor(string text) =>
        text is ['#', ..] && text.Length == 7 && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)
            ? ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : ColorHelper.FromArgb(255, 0x4F, 0xC3, 0xF7);

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        EditorSession session = services.Session;
        _editor = new ClipPropertyEditor(session);
        _boxRows = new EffectRowGroup(BoxRows, _editor, Editing, () => XamlRoot, Refresh, EditingTargets);
        _edgeRows = new EffectRowGroup(EdgeRows, _editor, Editing, () => XamlRoot, Refresh, EditingTargets);
        foreach (InspectorSection section in new[] { ListSection, ShapeSection, EdgeSection, AnimationSection, TrackSection, ViewSection })
        {
            section.Remember(services.Settings);
        }

        session.SelectionChanged += (_, _) => Refresh();
        session.ProjectEdited += (_, _) => Refresh();
        session.ProjectOpened += (_, _) => Refresh();
        session.MaskToolChanged += (_, _) => Refresh();
        session.MaskPicked += (_, id) =>
        {
            _maskId = id;
            Refresh();
        };
        session.TransportChanged += (_, _) =>
        {
            if (session.Position != _shownPosition && Visibility == Visibility.Visible)
            {
                Refresh();
            }
        };
        // Llega también desde los hilos de fondo que informan avance.
        Refresh();
        Bindings.Update();
    }

    private Clip? CurrentClip() =>
        _clipId is not null && _services?.Session.Sequence.FindClip(_clipId) is { } found ? found.Clip : null;

    private EffectInstance? CurrentMask() => CurrentClip()?.Effects.Find(e => e.Id == _maskId && e.Type == BuiltInEffects.Mask);

    private (Clip Clip, string Type, string EffectId)? Editing() =>
        CurrentClip() is { } clip && CurrentMask() is { } mask ? (clip, BuiltInEffects.Mask, mask.Id) : null;

    private (Track Track, Clip Clip)? Target() => _services is null ? null : InspectorTargets.First(_services.Session, IsVideoMaterial);

    private List<(Track Track, Clip Clip)> Targets() => _services is null ? [] : InspectorTargets.Selected(_services.Session, IsVideoMaterial);

    private static bool IsVideoMaterial((Track Track, Clip Clip) target) => target.Track.Kind == TrackKind.Video && target.Clip.Generator is null;

    private List<(Clip Clip, string? EffectId)> EditingTargets()
    {
        if (Targets().Count <= 1 || _maskId is null)
        {
            return Editing() is { } t ? [(t.Clip, (string?)t.EffectId)] : [];
        }

        return SameMask(_maskId).ConvertAll(t => (t.Clip, (string?)t.EffectId));
    }

    /// <summary>La máscara <paramref name="maskId"/> del clip de ahora y la que ocupa el mismo puesto en los demás seleccionados.</summary>
    private List<(Clip Clip, string EffectId)> SameMask(string maskId)
    {
        var list = new List<(Clip Clip, string EffectId)>();
        int index = CurrentClip()?.Effects.Where(e => e.Type == BuiltInEffects.Mask).ToList().FindIndex(e => e.Id == maskId) ?? -1;
        if (index < 0)
        {
            return list;
        }

        foreach ((Track _, Clip clip) in Targets())
        {
            var masks = clip.Effects.Where(e => e.Type == BuiltInEffects.Mask).ToList();
            if (index < masks.Count)
            {
                list.Add((clip, masks[index].Id));
            }
        }

        return list;
    }

    /// <summary>
    /// Un comando por clip seleccionado sobre su máscara equivalente, como un solo paso del historial. Si falla (pista
    /// bloqueada), el panel vuelve a mostrar el modelo.
    /// </summary>
    private bool RunOnSameMask(string maskId, Func<string, string, ICommand> command)
    {
        if (InspectorTargets.AsOneStep(SameMask(maskId).ConvertAll(t => command(t.Clip.Id, t.EffectId))) is { } step
            && _services?.Session.Execute(step) == true)
        {
            return true;
        }

        Refresh();
        return false;
    }

    private void Refresh()
    {
        if (_services is null || _editor is null)
        {
            return;
        }

        EditorSession session = _services.Session;
        ShowTools(session.MaskTool);
        if (Target() is not { } found)
        {
            _boxRows?.Finish();
            _edgeRows?.Finish();
            _clipId = null;
            Items.Clear();
            EmptyText.Visibility = Visibility.Visible;
            Header.Visibility = Visibility.Collapsed;
            Details.Visibility = Visibility.Collapsed;
            Publish();
            return;
        }

        Clip clip = found.Clip;
        if (clip.Id != _clipId)
        {
            _boxRows?.Finish();
            _edgeRows?.Finish();
        }

        _clipId = clip.Id;
        _shownPosition = session.Position;
        EmptyText.Visibility = Visibility.Collapsed;
        Header.Visibility = Visibility.Visible;
        var targets = Targets();
        bool multi = targets.Count > 1;
        ClipTitle.Text = multi ? $"{targets.Count} clips seleccionados" : ClipLabels.Name(session.Project, clip, "Clip");
        ShowList(clip);
        ShowDetails(clip);
        Publish();
    }

    private void ShowTools(MaskTool tool)
    {
        foreach (ToggleButton button in new[] { SelectTool, RectangleTool, EllipseTool, PenTool, StrokeTool, FreehandTool, StarTool })
        {
            button.IsChecked = (string)button.Tag == tool.ToString();
        }
    }

    // Con varios clips se listan todas las del primero: lo que se edita va también a la del mismo puesto en los demás.
    private void ShowList(Clip clip)
    {
        var next = new List<MaskItem>();
        int number = 0;
        foreach (EffectInstance effect in clip.Effects)
        {
            if (effect.Type != BuiltInEffects.Mask)
            {
                continue;
            }

            number++;
            string target = effect.Options.GetValueOrDefault(O.Target) is { Length: > 0 } id
                ? clip.Effects.Find(e => e.Id == id) is { } limited ? EffectUi.Name(limited.Type) : "un efecto que ya no está"
                : effect.Options.GetValueOrDefault(O.Use) == "effects" ? "la zona de los efectos" : "";
            next.Add(new MaskItem(effect, target, number));
        }

        if (_maskId is null || !next.Exists(i => i.EffectId == _maskId))
        {
            _maskId = next.Count > 0 ? next[^1].EffectId : null;
        }

        _updating = true;
        if (next.Count != Items.Count || !next.Zip(Items).All(p => p.First.Equals(p.Second)))
        {
            Items.Clear();
            foreach (MaskItem item in next)
            {
                Items.Add(item);
            }
        }

        List.SelectedItem = Items.FirstOrDefault(i => i.EffectId == _maskId);
        _updating = false;
        NoMasksText.Visibility = next.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = next.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ListSection.Summary = next.Count switch
        {
            0 => "ninguna",
            1 => "1 máscara",
            _ => $"{next.Count} máscaras",
        };
    }

    private void ShowDetails(Clip clip)
    {
        if (CurrentMask() is not { } mask || _services is null || _editor is null)
        {
            Details.Visibility = Visibility.Collapsed;
            return;
        }

        EditorSession session = _services.Session;
        Details.Visibility = Visibility.Visible;
        Time at = clip.ToEffectTime(session.Position);
        MaskSpec spec = MaskGeometry.Evaluate(mask, at);
        bool vertices = spec.Shape is MaskShape.Bezier or MaskShape.Stroke;
        bool image = spec.Shape == MaskShape.Image;
        _updating = true;
        ModeBox.SelectedIndex = (int)spec.Mode;
        InvertBox.IsChecked = spec.Invert;
        ShowTargets(clip, mask);
        _updating = false;

        ShapeSection.Title = "Forma · " + EffectUi.MaskShapeName(MaskGeometry.ShapeName(spec.Shape));
        ConvertButton.Visibility = spec.Shape is MaskShape.Rectangle or MaskShape.Ellipse or MaskShape.Path ? Visibility.Visible : Visibility.Collapsed;
        MaskVertex[] points = vertices ? MaskGeometry.Vertices(mask, at) : [];
        ShapeNote.Text = spec.Shape switch
        {
            MaskShape.Image => "El sujeto de «Quitar fondo» cambia en cada fotograma solo. Para editarlo a mano, «Añadir» › «Sujeto como Bézier».",
            MaskShape.Bezier or MaskShape.Stroke => $"{points.Length} puntos" + (session.SelectedMaskVertices.Count > 0
                ? $", {session.SelectedMaskVertices.Count} elegidos" : "") + ". La caja mueve, escala y gira toda la forma.",
            _ => "",
        };
        ShapeNote.Visibility = ShapeNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var context = new ParameterSpec.Context(session.Sequence.Width, session.Sequence.Height, clip.Duration.ToSeconds());
        _boxRows!.Build(BuiltInEffects.Mask, image ? [] : BoxNames, context);
        List<string> edge = image ? [M.Opacity] : [M.Feather, M.Expansion];
        if (spec.Shape == MaskShape.Rectangle)
        {
            edge.Add(M.Roundness);
        }

        if (spec.Shape == MaskShape.Stroke)
        {
            edge.Add(M.Thickness);
        }

        if (!image)
        {
            edge.Add(M.Opacity);
        }

        _edgeRows!.Build(BuiltInEffects.Mask, edge, context);
        var editingTargets = EditingTargets();
        int keyframes = _boxRows.Show(clip, mask.Id, editingTargets) + _edgeRows.Show(clip, mask.Id, editingTargets);
        ShapeSection.KeyframeSummary = keyframes == 0 ? "" : KeyframeDialog.Count(keyframes);
        ShowWeight(spec.Shape, points, session.SelectedMaskVertices);
        ShowShapeKeys(clip, mask, vertices, at);
        TrackSection.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
        ViewNormal.IsChecked = session.MaskView == MaskViewMode.None;
        ViewMatte.IsChecked = session.MaskView == MaskViewMode.Matte;
        ViewOverlay.IsChecked = session.MaskView == MaskViewMode.Overlay;
    }

    /// <remarks>
    /// Elegir un destino edita y refresca el panel; la edición va después del evento (<see cref="OnTargetSelected"/>) y los
    /// elementos solo se rehacen si cambió la lista y nunca con ella abierta, porque cambiarlos dentro del SelectionChanged
    /// (o con la lista abierta) falla dentro de WinUI y cierra la app.
    /// </remarks>
    private void ShowTargets(Clip clip, EffectInstance mask)
    {
        (ChoiceList<string> targets, int selected) = MaskTargetChoices.Build(clip, mask);
        _targets = ComboBoxChoices.Show(TargetBox, _targets, targets, selected, retry: _targetsRetry);
    }

    private void ShowWeight(MaskShape shape, MaskVertex[] points, IReadOnlyList<int> selected)
    {
        int[] chosen = [.. selected.Where(i => i >= 0 && i < points.Length)];
        bool show = shape is MaskShape.Bezier or MaskShape.Stroke && chosen.Length > 0;
        WeightRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || _weightDragging)
        {
            return;
        }

        float weight = chosen.Average(i => points[i].Weight);
        _updating = true;
        WeightLabel.Text = shape == MaskShape.Stroke ? "Grosor ×" : "Borde ×";
        ToolTipService.SetToolTip(WeightLabel, shape == MaskShape.Stroke
            ? "Multiplica el grosor del trazo en los puntos elegidos; entre puntos cambia de a poco"
            : "Multiplica el borde suave en los puntos elegidos (borde variable)");
        WeightSlider.Value = weight;
        WeightText.Text = weight.ToString("0.00", SpanishNumbers.Format);
        _updating = false;
    }

    private void ShowShapeKeys(Clip clip, EffectInstance mask, bool vertices, Time at)
    {
        AnimationSection.Visibility = vertices ? Visibility.Visible : Visibility.Collapsed;
        if (!vertices || _editor is null)
        {
            return;
        }

        MaskShapeTrack? track = MaskGeometry.IsShapeAnimated(mask) ? MaskShapeTrack.Parse(mask.Options.GetValueOrDefault(O.ShapeKeys)) : null;
        Time key = _editor.KeyframeTimeAtPlayhead(clip);
        int count = track?.Count ?? 0;
        KeyButton.IsChecked = track?.IndexOf(key) >= 0;
        KeyButton.IsEnabled = _editor.IsPlayheadInside(clip);
        PreviousKeyButton.IsEnabled = track is not null && track.Keys().Any(k => k.Time < key);
        NextKeyButton.IsEnabled = track is not null && track.Keys().Any(k => k.Time > key);
        ClearKeysButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        KeysText.Text = count switch
        {
            0 => "Forma fija: el rombo la anima",
            1 => "1 keyframe de forma",
            _ => $"{count} keyframes de forma",
        };
        AnimationSection.Summary = count == 0 ? "fija" : KeyframeDialog.Count(count);
    }

    /// <summary>Con la pestaña a la vista, el visor dibuja las máscaras del clip y las asas de la elegida.</summary>
    private void Publish() =>
        _services?.Session.SetMaskEditing(Visibility == Visibility.Visible && _clipId is not null, _clipId is null ? null : _maskId);

    private void Run(ICommand command) => _services?.Session.Execute(command);

    private string SequenceId => _services!.Session.SequenceId;

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && Enum.TryParse(tag, out MaskTool tool) && _services is not null)
        {
            _services.Session.SetMaskTool(tool);
            ShowTools(tool);
        }
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag } || _services is null || CurrentClip() is not { } clip)
        {
            return;
        }

        EditorSession session = _services.Session;
        double side = 0.35;
        SequenceEditCommand? Add(Clip target) => tag switch
        {
            "rectangle" => Edits.AddMask(SequenceId, target.Id, MaskShape.Rectangle),
            "ellipse" => Edits.AddMask(SequenceId, target.Id, MaskShape.Ellipse),
            "polygon" => Edits.AddVertexMask(SequenceId, target.Id, MaskShape.Bezier, 0.5, 0.5, side / AspectOf(target), side, MaskPath.Polygon(6), "Añadir un polígono"),
            "star" => Edits.AddVertexMask(SequenceId, target.Id, MaskShape.Bezier, 0.5, 0.5, side / AspectOf(target), side, MaskPath.Star(5), "Añadir una estrella"),
            "line" => Edits.AddVertexMask(SequenceId, target.Id, MaskShape.Stroke, 0.5, 0.5, 0.5, 0.1,
                [new MaskVertex(-0.5f, 0), new MaskVertex(0.5f, 0)], "Añadir una línea"),
            "blur" => Edits.AddMaskedZone(SequenceId, target.Id, ZoneEffect.Blur),
            "pixelate" => Edits.AddMaskedZone(SequenceId, target.Id, ZoneEffect.Pixelate),
            "subject" => SubjectCommand(target),
            _ => null,
        };

        // El contorno del sujeto es de cada clip: ese solo va al de ahora.
        SequenceEditCommand? command = Add(clip);
        List<SequenceEditCommand> others = tag == "subject" || command is null
            ? []
            : [.. Targets().Where(t => t.Clip.Id != clip.Id).Select(t => Add(t.Clip)).OfType<SequenceEditCommand>()];
        if (command is not null && InspectorTargets.AsOneStep([command, .. others]) is { } step && session.Execute(step))
        {
            _maskId = command.CreatedIds[^1];
            session.SetMaskTool(MaskTool.Select);
            Refresh();
            if (tag is "blur" or "pixelate")
            {
                session.Notify(NotificationSeverity.Informational, tag == "blur" ? "Desenfocar zona" : "Pixelar zona",
                    "Lleva la elipse sobre la cara o la matrícula en el visor y pulsa «Adelante» en Seguimiento para que la siga.");
            }
        }
    }

    /// <summary>Ancho entre alto de la imagen del clip (1 si no se sabe).</summary>
    private double AspectOf(Clip clip) =>
        _services!.Session.Project.FindMedia(clip.MediaId) is { Width: > 0, Height: > 0 } media ? (double)media.Width / media.Height : 16.0 / 9;

    /// <summary>El contorno del sujeto de «Quitar fondo» en el cursor, como una Bézier nueva.</summary>
    private SequenceEditCommand? SubjectCommand(Clip clip)
    {
        EditorSession session = _services!.Session;
        EffectInstance? subject = clip.Effects.Find(e => e.Type == BuiltInEffects.Mask && MaskGeometry.ParseShape(e.Options.GetValueOrDefault(O.Shape)) == MaskShape.Image);
        if (subject?.Options.GetValueOrDefault(O.Matte) is not { Length: > 0 } matte)
        {
            session.Notify(NotificationSeverity.Informational, "Sujeto como Bézier", "Primero usa «Quitar fondo» en este clip (menú del clip o Herramientas).");
            return null;
        }

        double start = double.TryParse(subject.Options.GetValueOrDefault(O.MatteStart), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? s : 0;
        double seconds = clip.ToEffectTime(session.Position).ToSeconds() - start;
        MatteFrame? frame = null;
        if (_services.Content is not { } content || !content.TryGetMatte(new ResourceKey(matte), Math.Max(0, seconds), out frame) || frame is null)
        {
            session.Notify(NotificationSeverity.Informational, "Sujeto como Bézier", "El video del sujeto todavía no se pudo leer; prueba de nuevo en un momento.");
            return null;
        }

        MaskVertex[] contour = MaskContour.Trace(frame.Gray, frame.Width, frame.Height, tolerance: Math.Max(1.5, frame.Width / 400.0));
        if (contour.Length < 3)
        {
            session.Notify(NotificationSeverity.Informational, "Sujeto como Bézier", "En este fotograma no hay un sujeto que contornear.");
            return null;
        }

        (double x, double y, double width, double height, MaskVertex[] vertices) = MaskPath.Fit(contour);
        return Edits.AddVertexMask(SequenceId, clip.Id, MaskShape.Bezier, x, y, width, height, vertices, "Sujeto como Bézier");
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || List.SelectedItem is not MaskItem item || item.EffectId == _maskId)
        {
            return;
        }

        _boxRows?.Finish();
        _edgeRows?.Finish();
        _maskId = item.EffectId;
        Refresh();
    }

    private void OnEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string id } toggle && _clipId is not null
            && !RunOnSameMask(id, (clipId, maskId) => Edits.SetEffectEnabled(SequenceId, clipId, maskId, toggle.IsChecked != true)))
        {
            // La fila no se rehace si el modelo no cambió.
            toggle.IsChecked = toggle.IsChecked != true;
        }
    }

    private void OnItemMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } button || CurrentClip() is not { } clip || clip.Effects.Find(m => m.Id == id) is not { } mask)
        {
            return;
        }

        var menu = new MenuFlyout();
        void Item(string text, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Item("Renombrar…", () => Rename(button, clip, mask));
        var colors = new MenuFlyoutSubItem { Text = "Color" };
        foreach ((string name, string value) in ColorNames)
        {
            var item = new MenuFlyoutItem
            {
                Text = name,
                Icon = new FontIcon { Glyph = "", Foreground = new SolidColorBrush(ParseColor(value)) },
            };
            item.Click += (_, _) => Run(Edits.SetEffectOption(SequenceId, clip.Id, id, O.Color, value));
            colors.Items.Add(item);
        }

        menu.Items.Add(colors);
        Item("Duplicar", () =>
        {
            SequenceEditCommand command = Edits.DuplicateMask(SequenceId, clip.Id, id);
            if (_services!.Session.Execute(command))
            {
                _maskId = command.CreatedIds[0];
                Refresh();
            }
        });
        MaskShape shape = MaskGeometry.ParseShape(mask.Options.GetValueOrDefault(O.Shape));
        Item("Convertir en Bézier", () => Convert(clip, id), shape is MaskShape.Rectangle or MaskShape.Ellipse or MaskShape.Path);
        menu.Items.Add(new MenuFlyoutSeparator());
        List<EffectInstance> masks = clip.Effects.FindAll(m => m.Type == BuiltInEffects.Mask);
        int order = masks.IndexOf(mask);
        Item("Subir", () => Run(Edits.MoveEffect(SequenceId, clip.Id, id, clip.Effects.IndexOf(masks[order - 1]))), order > 0);
        Item("Bajar", () => Run(Edits.MoveEffect(SequenceId, clip.Id, id, clip.Effects.IndexOf(masks[order + 1]))), order < masks.Count - 1);
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("Quitar", () => RunOnSameMask(id, (clipId, maskId) => Edits.RemoveEffect(SequenceId, clipId, maskId)));
        menu.ShowAt(button);
    }

    private void Rename(FrameworkElement anchor, Clip clip, EffectInstance mask)
    {
        var box = new TextBox
        {
            Text = mask.Options.GetValueOrDefault(O.Name) ?? "",
            Width = 220,
            MaxLength = 120,
            PlaceholderText = "Nombre de la máscara",
        };
        var flyout = new Flyout { Content = box };
        void Commit()
        {
            string name = box.Text.Trim();
            flyout.Hide();
            if (name.Length > 0 && name != mask.Options.GetValueOrDefault(O.Name))
            {
                Run(Edits.SetEffectOption(SequenceId, clip.Id, mask.Id, O.Name, name));
            }
        }

        box.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                args.Handled = true;
                Commit();
            }
        };
        flyout.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        flyout.ShowAt(anchor);
    }

    private void Convert(Clip clip, string maskId) => Run(Edits.ConvertMaskToBezier(SequenceId, clip.Id, maskId, clip.ToEffectTime(_services!.Session.Position),
        AspectOf(clip)));

    private void OnConvertClick(object sender, RoutedEventArgs e)
    {
        if (CurrentClip() is { } clip && _maskId is { } id)
        {
            Convert(clip, id);
        }
    }

    // La lista ya movió la fila: se pasa al modelo moviendo el efecto al lugar de la máscara que ocupaba ese puesto.
    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.Items.Count != 1 || args.Items[0] is not MaskItem item || CurrentClip() is not { } clip)
        {
            return;
        }

        List<EffectInstance> masks = clip.Effects.FindAll(m => m.Type == BuiltInEffects.Mask);
        int target = Items.IndexOf(item);
        if (target >= 0 && target < masks.Count && masks[target].Id != item.EffectId)
        {
            Run(Edits.MoveEffect(SequenceId, clip.Id, item.EffectId, clip.Effects.IndexOf(masks[target])));
        }
        else
        {
            Refresh();
        }
    }

    private void OnModeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && ModeBox.SelectedIndex >= 0 && _clipId is not null && _maskId is { } id)
        {
            RunOnSameMask(id, (clipId, maskId) => Edits.SetEffectOption(SequenceId, clipId, maskId, O.Mode, EffectRegistry.MaskModes[ModeBox.SelectedIndex]));
        }
    }

    private void OnInvertClick(object sender, RoutedEventArgs e)
    {
        if (_clipId is not null && _maskId is { } id)
        {
            RunOnSameMask(id, (clipId, maskId) => Edits.SetEffectOption(SequenceId, clipId, maskId, O.Invert, InvertBox.IsChecked == true ? "on" : "off"));
        }
    }

    private void OnTargetSelected(object sender, SelectionChangedEventArgs e)
    {
        // Elegir otro destino puede quitar «Un efecto que ya no está» y rehacer este desplegable al refrescar: la edición va
        // después del evento, y solo si el panel sigue en la misma máscara del mismo clip.
        DispatcherQueue.Choose(_targets, TargetBox.SelectedIndex, _updating, choice =>
        {
            if (_clipId is not { } clipId || _maskId is not { } id)
            {
                return null;
            }

            var picked = new DeferredChoice<string>(id, clipId);
            return () => ChooseTarget(picked, choice);
        });
    }

    private void ChooseTarget(DeferredChoice<string> picked, string choice)
    {
        // La decisión (aplicar o solo refrescar, y qué se guarda) es DeferredChoices.MaskTarget, que las pruebas ejercen.
        if (DeferredChoices.MaskTarget(picked, choice, _maskId, _clipId, CurrentClip()) is not { } edit)
        {
            Refresh();
            return;
        }

        string clipId = picked.ClipId!;
        string id = picked.Owner;
        Run(new SequenceEditCommand("Cambiar a qué se aplica la máscara", SequenceId, ctx =>
        {
            MaskEdits.SetTarget(ctx, clipId, id, edit.Target);
            EffectEdits.SetOption(ctx, clipId, id, O.Use, edit.Use);
        }));
    }

    private void OnWeightChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating || _services is null || CurrentClip() is not { } clip || CurrentMask() is not { } mask || _editor is null)
        {
            return;
        }

        EditorSession session = _services.Session;
        Time at = _editor.KeyframeTimeAtPlayhead(clip);
        MaskVertex[] vertices = MaskGeometry.Vertices(mask, clip.ToEffectTime(session.Position));
        WeightText.Text = e.NewValue.ToString("0.00", SpanishNumbers.Format);
        session.Execute(Edits.SetMaskVertices(SequenceId, clip.Id, mask.Id, MaskHandles.Weighted(vertices, session.SelectedMaskVertices, (float)e.NewValue),
            at, "Cambiar el borde de los puntos", "mascara-peso|" + mask.Id));
    }

    private void OnWeightReleased()
    {
        _weightDragging = false;
        _services?.Session.BreakMerge();
    }

    private void OnKeyClick(object sender, RoutedEventArgs e)
    {
        if (_editor is null || CurrentClip() is not { } clip || CurrentMask() is not { } mask)
        {
            return;
        }

        Time at = _editor.KeyframeTimeAtPlayhead(clip);
        bool present = MaskShapeTrack.Parse(mask.Options.GetValueOrDefault(O.ShapeKeys)) is { } track && track.IndexOf(at) >= 0;
        Run(present ? Edits.RemoveMaskShapeKey(SequenceId, clip.Id, mask.Id, at) : Edits.SetMaskShapeKey(SequenceId, clip.Id, mask.Id, at));
    }

    private void OnPreviousKeyClick(object sender, RoutedEventArgs e) => GoToKey(-1);

    private void OnNextKeyClick(object sender, RoutedEventArgs e) => GoToKey(1);

    private void GoToKey(int direction)
    {
        if (_editor is null || CurrentClip() is not { } clip || CurrentMask() is not { } mask
            || MaskShapeTrack.Parse(mask.Options.GetValueOrDefault(O.ShapeKeys)) is not { } track)
        {
            return;
        }

        Time at = _editor.KeyframeTimeAtPlayhead(clip);
        Time? target = direction < 0
            ? track.Keys().Select(k => k.Time).Where(t => t < at).Cast<Time?>().LastOrDefault()
            : track.Keys().Select(k => k.Time).Where(t => t > at).Cast<Time?>().FirstOrDefault();
        if (target is { } time)
        {
            _services!.Session.Seek(clip.TimeMap.TimelineOf(time));
        }
    }

    private void OnClearKeysClick(object sender, RoutedEventArgs e) => UiTask.Run(OnClearKeysClickAsync());

    private async Task OnClearKeysClickAsync()
    {
        if (CurrentClip() is not { } clip || _maskId is not { } id || _services is not { } services
            || MaskShapeTrack.Parse(clip.Effects.Find(e => e.Id == id)?.Options.GetValueOrDefault(O.ShapeKeys)) is not { } track
            || DialogGate.IsOpen || XamlRoot is not { } root)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "¿Quitar los keyframes de forma?",
            Content = $"Se borrarán {KeyframeDialog.Count(track.Count)} y la máscara quedará fija en la forma del cursor. Se puede deshacer con Ctrl+Z.",
            PrimaryButtonText = "Quitar keyframes",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ElementTheme.Dark,
        };
        if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary
            || _maskId != id || _clipId != clip.Id || services.Session.Sequence.FindClip(clip.Id) is not { } current
            || current.Clip.Effects.Find(e => e.Id == id) is not { } mask
            || MaskShapeTrack.Parse(mask.Options.GetValueOrDefault(O.ShapeKeys)) is not { Count: > 0 })
        {
            return;
        }

        Run(Edits.ClearMaskShapeKeys(SequenceId, clip.Id, id, current.Clip.ToEffectTime(services.Session.Position)));
    }

    private void OnViewClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && Enum.TryParse(tag, out MaskViewMode mode) && _services is not null)
        {
            _services.Session.SetMaskView(mode);
            Refresh();
        }
    }

    private void OnLinkTrackingClick(object sender, RoutedEventArgs e)
    {
        if (_services is not null && CurrentMask() is { } mask)
        {
            _services.Session.ShowTrackPanelForMask(mask.Id);
        }
    }

}
